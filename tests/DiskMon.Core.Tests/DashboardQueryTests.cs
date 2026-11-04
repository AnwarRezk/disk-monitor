using System.Diagnostics;
using Dapper;
using DiskMon.Core.Analysis;
using DiskMon.Core.Data;

namespace DiskMon.Core.Tests;

public sealed class DashboardQueryTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Range_returns_one_volumes_samples_inside_the_bounds_oldest_first()
    {
        var store = new VolumeSampleStore(Db.OpenOrCreate(_temp.File("diskmon.db")));
        store.Insert([new VolumeSample(3_000, "C:", 10, 3), new VolumeSample(3_000, "D:", 10, 9)]);
        store.Insert([new VolumeSample(1_000, "C:", 10, 1), new VolumeSample(2_000, "C:", 10, 2), new VolumeSample(4_000, "C:", 10, 4)]);

        Assert.Equal([1L, 2L, 3L], store.Range("C:", 1_000, 3_000).Select(s => s.FreeBytes));
        Assert.Equal(["C:", "D:"], store.Volumes());
    }

    [Fact]
    public void Top_writers_sum_each_program_over_the_range_and_rank_by_bytes_written()
    {
        var db = Db.OpenOrCreate(_temp.File("diskmon.db"));
        using var connection = db.OpenConnection();
        ProcessIoStore.Insert(connection, 1_000, [new("a.exe", @"C:\a.exe", 5 * Bytes.MB, 1), new("b.exe", @"C:\b.exe", 8 * Bytes.MB, 2)]);
        ProcessIoStore.Insert(connection, 61_000, [new("a.exe", @"C:\a.exe", 6 * Bytes.MB, 1), new("System", null, 2 * Bytes.MB, 0)]);
        ProcessIoStore.Insert(connection, 121_000, [new("c.exe", @"C:\c.exe", 99 * Bytes.MB, 0)]);

        var top = ProcessIoStore.TopWriters(connection, 0, 61_000, 2);

        Assert.Equal(
            [new WriterTotal("a.exe", @"C:\a.exe", 11 * Bytes.MB, 2), new WriterTotal("b.exe", @"C:\b.exe", 8 * Bytes.MB, 2)],
            top);
    }

    [Fact]
    public void Top_writers_merge_one_programs_install_paths_and_show_the_busiest()
    {
        var db = Db.OpenOrCreate(_temp.File("diskmon.db"));
        using var connection = db.OpenConnection();
        ProcessIoStore.Insert(connection, 1_000, [new("claude.exe", @"C:\Apps\Claude_1.0\claude.exe", 2 * Bytes.MB, 0), new("b.exe", @"C:\b.exe", 4 * Bytes.MB, 0)]);
        ProcessIoStore.Insert(connection, 61_000, [new("Claude.exe", @"C:\Apps\Claude_1.1\claude.exe", 3 * Bytes.MB, 0)]);

        var top = ProcessIoStore.TopWriters(connection, 0, 61_000, 10);

        Assert.Equal(
            [new WriterTotal("Claude.exe", @"C:\Apps\Claude_1.1\claude.exe", 5 * Bytes.MB, 0, 1), new WriterTotal("b.exe", @"C:\b.exe", 4 * Bytes.MB, 0)],
            top);
    }

    [Fact]
    public void Swings_overlapping_the_range_are_listed_newest_first_including_open_ones()
    {
        var db = Db.OpenOrCreate(_temp.File("diskmon.db"));
        using (var connection = db.OpenConnection())
        {
            connection.Execute(
                """
                INSERT INTO swings (volume, kind, stage, across_downtime, ts_start, ts_end, used_delta_bytes, attributed_bytes, unaccounted_bytes) VALUES
                ('C:', 'growth',  'closed', 0, 100, 200, 2000, 1500, 500),
                ('C:', 'release', 'closed', 1, 900, 1100, -3000, NULL, NULL),
                ('C:', 'growth',  'open',   0, 1500, NULL, NULL, NULL, NULL),
                ('D:', 'growth',  'open',   0, 1000, NULL, NULL, NULL, NULL);
                """);
        }

        var swings = new SwingStore(new ReadOnlyDb(db.Path)).Overlapping("C:", 1_000, 2_000);

        Assert.Equal(
            [
                new SwingRow(3, "C:", "growth", "open", false, 1500, null, null, null, null),
                new SwingRow(2, "C:", "release", "closed", true, 900, 1100, -3000, null, null),
            ],
            swings);
    }

    [Fact]
    public void A_day_of_samples_reads_and_thins_well_under_the_chart_budget()
    {
        var db = Db.OpenOrCreate(_temp.File("diskmon.db"));
        var store = new VolumeSampleStore(db);
        const long day = 24 * 60 * 60 * 1000L;
        // 30 days at the 30 s cadence, so the query has to skip the 29 days it does not want.
        var samples = Enumerable.Range(0, 30 * 2880)
            .Select(i => new VolumeSample(i * 30_000L, "C:", 500 * Bytes.GB, (50 * Bytes.GB) - (i * Bytes.MB)))
            .ToList();
        store.Insert(samples);
        var readOnly = new VolumeSampleStore(new ReadOnlyDb(db.Path));
        var end = samples[^1].Ts;

        readOnly.Range("C:", end - day, end); // warm up the JIT and the page cache
        var watch = Stopwatch.StartNew();
        var points = ChartSeries.Thin(readOnly.Range("C:", end - day, end), 2000);
        watch.Stop();

        Assert.Equal(2000, points.Count);
        Assert.True(watch.ElapsedMilliseconds < 250, $"took {watch.ElapsedMilliseconds} ms");
    }
}
