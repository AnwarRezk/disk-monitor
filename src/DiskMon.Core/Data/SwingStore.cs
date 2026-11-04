using Dapper;

namespace DiskMon.Core.Data;

/// <summary>A row of the dashboard's Swing list. Times are UTC Unix ms; byte columns are null until known.</summary>
public sealed record SwingRow(
    long Id,
    string Volume,
    string Kind,
    string Stage,
    bool AcrossDowntime,
    long TsStart,
    long? TsEnd,
    long? UsedDeltaBytes,
    long? AttributedBytes,
    long? UnaccountedBytes);

/// <summary>One attributed item of a Swing: <c>probe</c> deltas count towards Attributed, <c>process</c> write bytes only rank.</summary>
public sealed record SwingItem(string Source, string Label, string? Path, long DeltaBytes);

/// <summary>Reads the <c>swings</c> table for the dashboard. Swing detection (a later ticket) writes it.</summary>
public sealed class SwingStore(IDbConnectionSource db)
{
    /// <summary>
    /// Swings on <paramref name="volume"/> that overlap <paramref name="fromTs"/>..<paramref name="toTs"/>
    /// (an Open Swing has no end yet), newest first.
    /// </summary>
    public IReadOnlyList<SwingRow> Overlapping(string volume, long fromTs, long toTs)
    {
        using var connection = db.OpenConnection();
        return connection.Query<Row>(
            """
            SELECT id AS Id, volume AS Volume, kind AS Kind, stage AS Stage, across_downtime AS AcrossDowntime,
                   ts_start AS TsStart, ts_end AS TsEnd, used_delta_bytes AS UsedDeltaBytes,
                   attributed_bytes AS AttributedBytes, unaccounted_bytes AS UnaccountedBytes
            FROM swings
            WHERE volume = @volume AND ts_start <= @toTs AND (ts_end IS NULL OR ts_end >= @fromTs)
            ORDER BY ts_start DESC
            """,
            new { volume, fromTs, toTs })
            .Select(r => new SwingRow(
                r.Id, r.Volume, r.Kind, r.Stage, r.AcrossDowntime != 0, r.TsStart, r.TsEnd,
                r.UsedDeltaBytes, r.AttributedBytes, r.UnaccountedBytes))
            .ToList();
    }

    /// <summary>What a Swing's attribution found, largest first per source: Probe deltas, then Top Writers.</summary>
    public IReadOnlyList<SwingItem> Items(long swingId)
    {
        using var connection = db.OpenConnection();
        return connection.Query<SwingItem>(
            """
            SELECT source AS Source, label AS Label, path AS Path, delta_bytes AS DeltaBytes
            FROM swing_items WHERE swing_id = @swingId
            ORDER BY CASE source WHEN 'probe' THEN 0 WHEN 'usn' THEN 1 ELSE 2 END, rank
            """,
            new { swingId }).ToList();
    }

    // SQLite has no boolean type, so across_downtime arrives as an integer.
    private sealed record Row(
        long Id, string Volume, string Kind, string Stage, long AcrossDowntime, long TsStart, long? TsEnd,
        long? UsedDeltaBytes, long? AttributedBytes, long? UnaccountedBytes);
}
