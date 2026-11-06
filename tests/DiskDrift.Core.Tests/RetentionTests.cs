using Dapper;
using DiskDrift.Core.Data;

namespace DiskDrift.Core.Tests;

public sealed class RetentionTests : IDisposable
{
    private const long Cutoff = 1_000_000;
    private const long Old = Cutoff - 1;
    private const long Recent = Cutoff;

    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Prunes_old_samples_and_unreferenced_processes_and_keeps_swings_forever()
    {
        var db = Db.OpenOrCreate(_temp.File("diskdrift.db"));
        using var connection = db.OpenConnection();
        connection.Execute(
            """
            INSERT INTO volume_samples (ts, volume, total_bytes, free_bytes) VALUES (@Old, 'C:', 100, 50), (@Recent, 'C:', 100, 40);

            INSERT INTO processes (id, name, exe_path) VALUES (1, 'old-only.exe', 'C:\old-only.exe'), (2, 'both.exe', 'C:\both.exe'), (3, 'System', NULL);
            INSERT INTO process_io_samples (ts, process_id, write_bytes_delta, read_bytes_delta)
              VALUES (@Old, 1, 5, 0), (@Old, 2, 5, 0), (@Recent, 2, 7, 0), (@Old, 3, 9, 0);

            INSERT INTO probes (id, name, kind, path, volume) VALUES (1, 'Temp', 'dir', 'C:\Temp', 'C:');
            INSERT INTO probe_samples (ts, probe_id, size_bytes) VALUES (@Old, 1, 10), (@Recent, 1, 20);

            INSERT INTO swings (id, volume, kind, stage, ts_start, ts_end) VALUES (1, 'C:', 'growth', 'closed', @Old - 5000, @Old);
            INSERT INTO swing_items (swing_id, source, label, path, delta_bytes, rank)
              VALUES (1, 'probe', 'Temp', 'C:\Temp', 10, 1), (1, 'process', 'old-only.exe', 'C:\old-only.exe', 5, 1);
            """,
            new { Old, Recent });

        var removed = Retention.Prune(connection, Cutoff);

        Assert.Equal(new RetentionResult(VolumeSamples: 1, ProcessIoSamples: 3, ProbeSamples: 1, Processes: 2), removed);
        Assert.Equal([Recent], connection.Query<long>("SELECT ts FROM volume_samples"));
        Assert.Equal([Recent], connection.Query<long>("SELECT ts FROM process_io_samples"));
        Assert.Equal([Recent], connection.Query<long>("SELECT ts FROM probe_samples"));
        Assert.Equal(["both.exe"], connection.Query<string>("SELECT name FROM processes"));
        Assert.Equal(1, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM probes"));
        Assert.Equal(1, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM swings"));
        Assert.Equal(2, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM swing_items"));
    }

    [Fact]
    public void Running_twice_removes_nothing_more()
    {
        var db = Db.OpenOrCreate(_temp.File("diskdrift.db"));
        using var connection = db.OpenConnection();
        connection.Execute("INSERT INTO volume_samples (ts, volume, total_bytes, free_bytes) VALUES (@Old, 'C:', 100, 50)", new { Old });

        Retention.Prune(connection, Cutoff);

        Assert.Equal(0, Retention.Prune(connection, Cutoff).Total);
    }
}
