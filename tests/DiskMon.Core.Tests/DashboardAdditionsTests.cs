using Dapper;
using DiskMon.Core.Analysis;
using DiskMon.Core.Config;
using DiskMon.Core.Data;
using DiskMon.Core.Probes;

namespace DiskMon.Core.Tests;

public sealed class DashboardAdditionsTests : IDisposable
{
    private const long Day = 86_400_000;
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Probe_sizes_show_the_newest_size_and_the_change_since_the_range_start_largest_first()
    {
        var db = Db.OpenOrCreate(_temp.File("diskmon.db"));
        using var connection = db.OpenConnection();
        var ids = ProbeStore.Upsert(connection,
        [
            new ExpandedProbe("Temp", ProbeKind.Dir, @"C:\Temp", "C:"),
            new ExpandedProbe("WSL disks (shado)", ProbeKind.Glob, @"C:\Users\shado\wsl\*", "C:"),
            new ExpandedProbe("New", ProbeKind.Dir, @"C:\New", "C:"),
            new ExpandedProbe("Other drive", ProbeKind.Dir, @"D:\Data", "D:"),
        ]);
        ProbeStore.InsertSamples(connection, 1_000, [(ids[0], M(5)), (ids[1], M(70)), (ids[3], M(1))]);
        ProbeStore.InsertSamples(connection, 3_000, [(ids[0], M(8)), (ids[1], M(69)), (ids[2], M(2))]);
        ProbeStore.InsertSamples(connection, 5_000, [(ids[0], M(9)), (ids[1], M(68)), (ids[2], M(3))]);
        ProbeStore.InsertSamples(connection, 9_000, [(ids[0], M(50))]);

        var sizes = ProbeReads.Sizes(connection, "c:", 2_000, 6_000);

        Assert.Equal(["WSL disks (shado)", "Temp", "New"], sizes.Select(s => s.Name));
        Assert.Equal([68 * Bytes.GB, 9 * Bytes.GB, 3 * Bytes.GB], sizes.Select(s => s.SizeBytes));
        // WSL and Temp compare with the sample before the range; New only has history inside it.
        Assert.Equal([-2 * Bytes.GB, 4 * Bytes.GB, 1 * Bytes.GB], sizes.Select(s => s.ChangeBytes));
        Assert.Equal(9_000, ProbeReads.LatestRunTs(connection));
    }

    [Fact]
    public void A_probe_with_one_sample_has_no_change()
    {
        var db = Db.OpenOrCreate(_temp.File("diskmon.db"));
        using var connection = db.OpenConnection();
        var ids = ProbeStore.Upsert(connection, [new ExpandedProbe("Temp", ProbeKind.Dir, @"C:\Temp", "C:")]);
        ProbeStore.InsertSamples(connection, 5_000, [(ids[0], M(1))]);

        Assert.Null(Assert.Single(ProbeReads.Sizes(connection, "C:", 1_000, 9_000)).ChangeBytes);
        Assert.Empty(ProbeReads.Sizes(connection, "C:", 1_000, 4_000));
    }

    [Fact]
    public void Swing_items_list_probes_before_processes_in_rank_order()
    {
        var db = Db.OpenOrCreate(_temp.File("diskmon.db"));
        using (var connection = db.OpenConnection())
        {
            connection.Execute("INSERT INTO swings (id, volume, kind, stage, ts_start) VALUES (1, 'C:', 'growth', 'closed', 0)");
            connection.Execute(
                """
                INSERT INTO swing_items (swing_id, source, label, path, delta_bytes, rank) VALUES
                (1, 'process', 'dotnet.exe', NULL, 900, 1),
                (1, 'probe', 'Docker', 'C:\d', 300, 2),
                (1, 'probe', 'User Temp', 'C:\t', 700, 1)
                """);
        }

        var items = new SwingStore(new ReadOnlyDb(db.Path)).Items(1);

        Assert.Equal(["User Temp", "Docker", "dotnet.exe"], items.Select(i => i.Label));
        Assert.Equal(new SwingItem("probe", "User Temp", @"C:\t", 700), items[0]);
    }

    [Fact]
    public void Forecast_divides_free_space_by_the_falling_trend()
    {
        // 30 GB free falling 2 GB a day for 3 days: 24 GB left, so 12 days.
        var samples = Enumerable.Range(0, 73).Select(h => Sample(h * 3_600_000L, 30 - (2.0 * h / 24))).ToList();

        var forecast = FullForecast.Estimate(samples)!;

        Assert.Equal(12, forecast.DaysToFull!.Value, 3);
        Assert.Equal(-2.0, forecast.BytesPerDay / Bytes.GB, 3);
        Assert.Equal(3, forecast.SpanDays, 3);
    }

    [Fact]
    public void Forecast_has_no_end_when_free_space_is_steady_or_rising()
    {
        var rising = Enumerable.Range(0, 49).Select(h => Sample(h * 3_600_000L, 10 + (h / 24.0))).ToList();
        var steady = Enumerable.Range(0, 49).Select(h => Sample(h * 3_600_000L, 10 - (0.05 * h / 24))).ToList();

        Assert.Null(FullForecast.Estimate(rising)!.DaysToFull);
        Assert.Null(FullForecast.Estimate(steady)!.DaysToFull);
    }

    [Fact]
    public void Forecast_needs_a_few_hours_of_history()
    {
        Assert.Null(FullForecast.Estimate([Sample(0, 9), Sample(Day / 48, 8), Sample(Day / 24, 7)]));
        Assert.Null(FullForecast.Estimate([Sample(0, 9), Sample(Day, 8)]));
    }

    [Theory]
    [InlineData(330, 33, 16.5)] // a big disk warns by percentage
    [InlineData(100, 20, 10)]   // a small disk warns by GB
    [InlineData(0, 20, 10)]     // total unknown: GB limits only
    public void Warning_levels_are_the_higher_of_the_gb_and_percent_limits(double totalGb, double amberGb, double redGb)
    {
        var (amber, red) = FreeSpaceStatus.WarningLevels((long)(totalGb * Bytes.GB), new TrayOptions());

        Assert.Equal(amberGb, (double)amber / Bytes.GB, 3);
        Assert.Equal(redGb, (double)red / Bytes.GB, 3);
    }

    [Fact]
    public void Warning_levels_agree_with_the_tray_colour()
    {
        var options = new TrayOptions();
        var total = 330 * Bytes.GB;
        var (amber, red) = FreeSpaceStatus.WarningLevels(total, options);

        Assert.Equal(FreeSpaceLevel.Amber, FreeSpaceStatus.Level(amber - 1, total, options));
        Assert.Equal(FreeSpaceLevel.Green, FreeSpaceStatus.Level(amber, total, options));
        Assert.Equal(FreeSpaceLevel.Red, FreeSpaceStatus.Level(red - 1, total, options));
    }

    [Theory]
    [InlineData("WSL disks (shado)", "wsl --manage <distro> --set-sparse true")]
    [InlineData("Docker (shado)", "docker system prune -a")]
    [InlineData("npm cache (anwar)", "npm cache clean --force")]
    [InlineData("Recycle Bin", "Clear-RecycleBin -Force")]
    public void Reclaim_hints_match_the_configured_name_ignoring_the_user(string probe, string command) =>
        Assert.Equal(command, ReclaimHints.For(probe)!.Command);

    [Fact]
    public void Probes_without_a_safe_command_get_a_note_and_unknown_probes_get_nothing()
    {
        Assert.Null(ReclaimHints.For("Page file")!.Command);
        Assert.NotEmpty(ReclaimHints.For("Downloads (shado)")!.Note);
        Assert.Null(ReclaimHints.For("My own folder"));
    }

    private static ProbeMeasurement M(long gb) => new(gb * Bytes.GB, 1, 10, null);

    private static VolumeSample Sample(long ts, double freeGb) => new(ts, "C:", 100 * Bytes.GB, (long)(freeGb * Bytes.GB));
}
