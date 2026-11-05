using Dapper;
using DiskDrift.Core.Analysis;
using Microsoft.Data.Sqlite;

namespace DiskDrift.Core.Data;

/// <summary>
/// One program's bytes written and read over a dashboard range: a Top Writers row. <paramref name="ExePath"/> is
/// its busiest install path; <paramref name="OtherPaths"/> counts other paths under the same name, summed in.
/// </summary>
public sealed record WriterTotal(string Name, string? ExePath, long WriteBytes, long ReadBytes, long OtherPaths = 0);

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

    /// <summary>
    /// The top <paramref name="count"/> programs by bytes written in samples with <paramref name="fromTs"/> ≤ ts ≤
    /// <paramref name="toTs"/>, summed over the range. Rows are grouped by program name (case-insensitive), so an
    /// app that updated into a new versioned folder (WindowsApps, Squirrel) still ranks once; the path shown is the
    /// one that wrote the most, and <see cref="WriterTotal.OtherPaths"/> counts the rest. Only each interval's
    /// stored Top Writers count, so a program that rarely made the top 10 is under-counted: a ranking, not a total.
    /// </summary>
    public static IReadOnlyList<WriterTotal> TopWriters(SqliteConnection connection, long fromTs, long toTs, int count) =>
        connection.Query<WriterRow>(
            """
            WITH per_path AS (
                SELECT p.name AS name, p.exe_path AS exe_path,
                       SUM(s.write_bytes_delta) AS write_bytes, SUM(s.read_bytes_delta) AS read_bytes
                FROM process_io_samples s JOIN processes p ON p.id = s.process_id
                WHERE s.ts BETWEEN @fromTs AND @toTs
                GROUP BY s.process_id
            ),
            ranked AS (
                SELECT *, ROW_NUMBER() OVER (PARTITION BY lower(name) ORDER BY write_bytes DESC, exe_path) AS n,
                       SUM(write_bytes) OVER (PARTITION BY lower(name)) AS total_write,
                       SUM(read_bytes) OVER (PARTITION BY lower(name)) AS total_read,
                       COUNT(*) OVER (PARTITION BY lower(name)) AS paths
                FROM per_path
            )
            SELECT name AS Name, exe_path AS ExePath, total_write AS WriteBytes, total_read AS ReadBytes,
                   paths - 1 AS OtherPaths
            FROM ranked WHERE n = 1
            ORDER BY total_write DESC, name
            LIMIT @count
            """,
            new { fromTs, toTs, count })
            .Select(r => new WriterTotal(r.Name, r.ExePath, r.WriteBytes, r.ReadBytes, r.OtherPaths))
            .ToList();

    // Settable properties, not a constructor: with no rows SQLite reports computed columns as BLOB, which no
    // constructor signature matches.
    private sealed class WriterRow
    {
        public string Name { get; set; } = "";
        public string? ExePath { get; set; }
        public long WriteBytes { get; set; }
        public long ReadBytes { get; set; }
        public long OtherPaths { get; set; }
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
