using Dapper;
using DiskMon.Core.Analysis;
using DiskMon.Core.Data;

namespace DiskMon.Core.Tests;

public sealed class ProcessIoStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Stores_writers_and_deduplicates_process_identity_including_processes_without_a_path()
    {
        var db = Db.OpenOrCreate(_temp.File("diskmon.db"));
        using var connection = db.OpenConnection();

        ProcessIoDelta[] writers =
        [
            new("a.exe", @"C:\Apps\a.exe", 20 * Bytes.MB, 1 * Bytes.MB),
            new("System", null, 5 * Bytes.MB, 0),
        ];
        ProcessIoStore.Insert(connection, 1_000, writers);
        ProcessIoStore.Insert(connection, 61_000, writers);

        Assert.Equal(2, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM processes"));
        var rows = connection.Query<(long Ts, string Name, string? ExePath, long Write, long Read)>(
            """
            SELECT s.ts, p.name, p.exe_path, s.write_bytes_delta, s.read_bytes_delta
            FROM process_io_samples s JOIN processes p ON p.id = s.process_id
            ORDER BY s.ts, s.write_bytes_delta DESC
            """).ToArray();
        Assert.Equal(
        [
            (1_000L, "a.exe", @"C:\Apps\a.exe", 20 * Bytes.MB, 1 * Bytes.MB),
            (1_000L, "System", null, 5 * Bytes.MB, 0L),
            (61_000L, "a.exe", @"C:\Apps\a.exe", 20 * Bytes.MB, 1 * Bytes.MB),
            (61_000L, "System", null, 5 * Bytes.MB, 0L),
        ], rows);
    }
}
