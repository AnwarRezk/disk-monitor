using Dapper;
using DiskDrift.Core.Config;
using DiskDrift.Core.Data;
using DiskDrift.Core.Probes;

namespace DiskDrift.Core.Tests;

public sealed class ProbeStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Upsert_keeps_ids_stable_and_takes_the_new_name()
    {
        var db = Db.OpenOrCreate(_temp.File("diskdrift.db"));
        using var connection = db.OpenConnection();
        var temp = new ExpandedProbe("Windows Temp", ProbeKind.Dir, @"C:\Windows\Temp", "C:");
        var vss = new ExpandedProbe("VSS", ProbeKind.Vss, "C:", "C:");

        var first = ProbeStore.Upsert(connection, [temp, vss]);
        var second = ProbeStore.Upsert(connection, [vss, temp with { Name = "System Temp" }]);

        Assert.Equal([first[1], first[0]], second);
        Assert.Equal(2, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM probes"));
        Assert.Equal("System Temp", connection.ExecuteScalar<string>("SELECT name FROM probes WHERE id = @id", new { id = first[0] }));
        Assert.Equal("vss", connection.ExecuteScalar<string>("SELECT kind FROM probes WHERE id = @id", new { id = first[1] }));
    }

    [Fact]
    public void Samples_are_written_with_their_error_and_partial_size()
    {
        var db = Db.OpenOrCreate(_temp.File("diskdrift.db"));
        using var connection = db.OpenConnection();
        var ids = ProbeStore.Upsert(connection, [new ExpandedProbe("Big", ProbeKind.Dir, @"C:\Big", "C:"), new ExpandedProbe("Gone", ProbeKind.Dir, @"C:\Gone", "C:")]);

        ProbeStore.InsertSamples(connection, 1_000, [(ids[0], new ProbeMeasurement(42, 3, 60_000, "timed out")), (ids[1], ProbeMeasurement.Missing)]);

        var rows = connection.Query<(long Ts, long ProbeId, long Size, long? Files, long? Ms, string? Error)>(
            "SELECT ts, probe_id, size_bytes, file_count, duration_ms, error FROM probe_samples ORDER BY probe_id").ToList();
        Assert.Equal([(1_000L, ids[0], 42L, (long?)3, (long?)60_000, "timed out"), (1_000L, ids[1], 0L, (long?)0, (long?)0, (string?)null)], rows);
    }
}
