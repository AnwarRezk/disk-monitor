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

/// <summary>
/// One Probe's latest size on a Volume, and how it changed over a range. <see cref="ChangeBytes"/> is null when the
/// Probe has only one sample in reach (nothing to compare with).
/// </summary>
public sealed record ProbeSize(long Id, string Name, string Kind, string Path, long SizeBytes, long? ChangeBytes, long Ts, string? Error);

/// <summary>Reads Probe Samples for the dashboard.</summary>
public static class ProbeReads
{
    /// <summary>
    /// Each Probe on <paramref name="volume"/> with its newest sample at or before <paramref name="toTs"/>, largest
    /// first. The change is measured from the newest sample at or before <paramref name="fromTs"/>, or, with less
    /// history than the range, from the Probe's first sample inside it.
    /// </summary>
    public static IReadOnlyList<ProbeSize> Sizes(SqliteConnection connection, string volume, long fromTs, long toTs) =>
        connection.Query<SizeRow>(
            """
            WITH latest AS (
                SELECT p.id, p.name, p.kind, p.path,
                       (SELECT ts FROM probe_samples s WHERE s.probe_id = p.id AND s.ts <= @toTs ORDER BY ts DESC LIMIT 1) AS ts
                FROM probes p
                WHERE p.volume = @volume COLLATE NOCASE
            ),
            base AS (
                SELECT l.*,
                       COALESCE(
                           (SELECT ts FROM probe_samples s WHERE s.probe_id = l.id AND s.ts <= @fromTs ORDER BY ts DESC LIMIT 1),
                           (SELECT ts FROM probe_samples s WHERE s.probe_id = l.id AND s.ts > @fromTs AND s.ts <= @toTs ORDER BY ts LIMIT 1)) AS base_ts
                FROM latest l
                WHERE l.ts IS NOT NULL
            )
            SELECT b.id AS Id, b.name AS Name, b.kind AS Kind, b.path AS Path, b.ts AS Ts,
                   now.size_bytes AS SizeBytes, now.error AS Error,
                   CASE WHEN b.base_ts < b.ts THEN now.size_bytes - was.size_bytes END AS ChangeBytes
            FROM base b
            JOIN probe_samples now ON now.probe_id = b.id AND now.ts = b.ts
            LEFT JOIN probe_samples was ON was.probe_id = b.id AND was.ts = b.base_ts
            ORDER BY now.size_bytes DESC, b.name
            """,
            new { volume, fromTs, toTs })
            .Select(r => new ProbeSize(r.Id, r.Name, r.Kind, r.Path, r.SizeBytes, r.ChangeBytes, r.Ts, r.Error))
            .ToList();

    /// <summary>When the newest Probe run was written, or null before the first one.</summary>
    public static long? LatestRunTs(SqliteConnection connection) =>
        connection.ExecuteScalar<long?>("SELECT MAX(ts) FROM probe_samples");

    // Settable properties, not a constructor: a NULL change arrives untyped, which a constructor signature can't match.
    private sealed class SizeRow
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public string Kind { get; set; } = "";
        public string Path { get; set; } = "";
        public long Ts { get; set; }
        public long SizeBytes { get; set; }
        public string? Error { get; set; }
        public long? ChangeBytes { get; set; }
    }
}
