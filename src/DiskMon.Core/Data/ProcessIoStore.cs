using Dapper;
using DiskMon.Core.Analysis;
using Microsoft.Data.Sqlite;

namespace DiskMon.Core.Data;

/// <summary>Writes Top Writers to <c>process_io_samples</c>, with process identity deduplicated in <c>processes</c>.</summary>
public static class ProcessIoStore
{
    /// <summary>Stores one interval's Top Writers at <paramref name="ts"/> (UTC Unix ms) in a single transaction.</summary>
    public static void Insert(SqliteConnection connection, long ts, IReadOnlyList<ProcessIoDelta> writers)
    {
        if (writers.Count == 0)
            return;

        using var tx = connection.BeginTransaction();
        foreach (var w in writers)
        {
            var processId = ProcessId(connection, tx, w.Name, w.ExePath);
            connection.Execute(
                "INSERT INTO process_io_samples (ts, process_id, write_bytes_delta, read_bytes_delta) VALUES (@ts, @processId, @write, @read)",
                new { ts, processId, write = w.WriteBytes, read = w.ReadBytes }, tx);
        }
        tx.Commit();
    }

    // Not cached across calls: retention may prune a process row between intervals.
    // "IS" rather than "=" because exe_path is NULL for processes without an image (System, Registry, ...),
    // and UNIQUE(name, exe_path) doesn't stop duplicate NULLs.
    private static long ProcessId(SqliteConnection connection, SqliteTransaction tx, string name, string? exePath) =>
        connection.ExecuteScalar<long?>(
            "SELECT id FROM processes WHERE name = @name AND exe_path IS @exePath LIMIT 1", new { name, exePath }, tx)
        ?? connection.ExecuteScalar<long>(
            "INSERT INTO processes (name, exe_path) VALUES (@name, @exePath) RETURNING id", new { name, exePath }, tx);
}
