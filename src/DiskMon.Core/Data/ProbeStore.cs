using Dapper;
using DiskMon.Core.Probes;
using Microsoft.Data.Sqlite;

namespace DiskMon.Core.Data;

/// <summary>Writes the <c>probes</c> list and <c>probe_samples</c>.</summary>
public static class ProbeStore
{
    /// <summary>
    /// Inserts or renames each Probe (keyed by kind and path, so history survives a rename in config) and returns
    /// their ids in the same order. Probes dropped from config keep their rows and history; they just stop getting samples.
    /// </summary>
    public static IReadOnlyList<long> Upsert(SqliteConnection connection, IReadOnlyList<ExpandedProbe> probes)
    {
        using var tx = connection.BeginTransaction();
        var ids = probes.Select(p => connection.ExecuteScalar<long>(
            """
            INSERT INTO probes (name, kind, path, volume) VALUES (@Name, @KindText, @Path, @Volume)
            ON CONFLICT (kind, path) DO UPDATE SET name = excluded.name, volume = excluded.volume
            RETURNING id
            """,
            p, tx)).ToList();
        tx.Commit();
        return ids;
    }

    /// <summary>Stores one run's Probe Samples at <paramref name="ts"/> (UTC Unix ms) in a single transaction.</summary>
    public static void InsertSamples(SqliteConnection connection, long ts, IReadOnlyList<(long ProbeId, ProbeMeasurement Measurement)> samples)
    {
        using var tx = connection.BeginTransaction();
        connection.Execute(
            "INSERT INTO probe_samples (ts, probe_id, size_bytes, file_count, duration_ms, error) VALUES (@ts, @ProbeId, @SizeBytes, @FileCount, @DurationMs, @Error)",
            samples.Select(s => new { ts, s.ProbeId, s.Measurement.SizeBytes, s.Measurement.FileCount, s.Measurement.DurationMs, s.Measurement.Error }),
            tx);
        tx.Commit();
    }
}
