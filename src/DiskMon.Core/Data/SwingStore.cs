using Dapper;
using DiskMon.Core.Analysis;

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
    long? UnaccountedBytes,
    long? ProbeWindowStart = null,
    long? ProbeWindowEnd = null);

/// <summary>One attributed item of a Swing: <c>probe</c> deltas count towards Attributed, <c>process</c> write bytes only rank.</summary>
public sealed record SwingItem(string Source, string Label, string? Path, long DeltaBytes);

/// <summary>A Swing waiting for Attribution: its move has ended and its after-side Probe run may not have finished.</summary>
public sealed record ClosingSwing(long Id, string Volume, long TsStart, long TsEnd, long UsedDeltaBytes);

/// <summary>A Swing the Collector left Open, as read back at startup.</summary>
public sealed record StoredOpenSwing(long Id, string Volume, SwingKind Kind, long TsStart);

/// <summary>
/// The <c>swings</c> table: the dashboard reads it, the Collector's Swing detection writes it (the Tray's read-only
/// connection makes the writes fail there).
/// </summary>
public sealed class SwingStore(IDbConnectionSource db)
{
    public static string KindText(SwingKind kind) => kind == SwingKind.Growth ? "growth" : "release";

    /// <summary>Inserts an Open Swing and returns its id.</summary>
    public long InsertOpen(string volume, SwingKind kind, long tsStart)
    {
        using var connection = db.OpenConnection();
        return connection.ExecuteScalar<long>(
            "INSERT INTO swings (volume, kind, stage, ts_start) VALUES (@volume, @kind, 'open', @tsStart) RETURNING id",
            new { volume, kind = KindText(kind), tsStart });
    }

    /// <summary>Ends an Open Swing: it moves to Closing until Attribution has run.</summary>
    public void MarkClosing(long id, long tsEnd, long usedDeltaBytes)
    {
        using var connection = db.OpenConnection();
        connection.Execute(
            "UPDATE swings SET stage = 'closing', ts_end = @tsEnd, used_delta_bytes = @usedDeltaBytes WHERE id = @id AND stage = 'open'",
            new { id, tsEnd, usedDeltaBytes });
    }

    /// <summary>Inserts a Swing measured across Downtime, straight into Closing, and returns its id.</summary>
    public long InsertAcrossDowntime(string volume, SwingKind kind, long tsStart, long tsEnd, long usedDeltaBytes)
    {
        using var connection = db.OpenConnection();
        return connection.ExecuteScalar<long>(
            """
            INSERT INTO swings (volume, kind, stage, across_downtime, ts_start, ts_end, used_delta_bytes)
            VALUES (@volume, @kind, 'closing', 1, @tsStart, @tsEnd, @usedDeltaBytes) RETURNING id
            """,
            new { volume, kind = KindText(kind), tsStart, tsEnd, usedDeltaBytes });
    }

    /// <summary>Swings in Closing, oldest first.</summary>
    public IReadOnlyList<ClosingSwing> ClosingSwings()
    {
        using var connection = db.OpenConnection();
        return connection.Query<ClosingSwing>(
            """
            SELECT id AS Id, volume AS Volume, ts_start AS TsStart, ts_end AS TsEnd, used_delta_bytes AS UsedDeltaBytes
            FROM swings
            WHERE stage = 'closing' AND ts_end IS NOT NULL AND used_delta_bytes IS NOT NULL
            ORDER BY ts_start, id
            """).AsList();
    }

    /// <summary>
    /// Stores a Swing's Attribution and moves it to Closed, in one transaction. Does nothing (and returns false) unless
    /// the Swing is still Closing, so attributing twice can't duplicate its items.
    /// </summary>
    public bool Close(long id, AttributionResult result)
    {
        using var connection = db.OpenConnection();
        using var tx = connection.BeginTransaction();
        var updated = connection.Execute(
            """
            UPDATE swings SET stage = 'closed', probe_window_start = @ProbeWindowStart, probe_window_end = @ProbeWindowEnd,
                              attributed_bytes = @AttributedBytes, unaccounted_bytes = @UnaccountedBytes
            WHERE id = @id AND stage = 'closing'
            """,
            new { id, result.ProbeWindowStart, result.ProbeWindowEnd, result.AttributedBytes, result.UnaccountedBytes }, tx);
        if (updated == 0)
            return false;
        connection.Execute(
            "INSERT INTO swing_items (swing_id, source, label, path, delta_bytes, rank) VALUES (@id, @Source, @Label, @Path, @DeltaBytes, @Rank)",
            result.Items.Select(i => new { id, i.Source, i.Label, i.Path, i.DeltaBytes, i.Rank }), tx);
        tx.Commit();
        return true;
    }

    /// <summary>Swings still Open, oldest first: at most one per Volume while the Collector runs.</summary>
    public IReadOnlyList<StoredOpenSwing> OpenSwings()
    {
        using var connection = db.OpenConnection();
        return connection.Query<(long Id, string Volume, string Kind, long TsStart)>(
            "SELECT id, volume, kind, ts_start FROM swings WHERE stage = 'open' ORDER BY ts_start, id")
            .Select(r => new StoredOpenSwing(r.Id, r.Volume, r.Kind == "growth" ? SwingKind.Growth : SwingKind.Release, r.TsStart))
            .ToList();
    }

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
                   attributed_bytes AS AttributedBytes, unaccounted_bytes AS UnaccountedBytes,
                   probe_window_start AS ProbeWindowStart, probe_window_end AS ProbeWindowEnd
            FROM swings
            WHERE volume = @volume AND ts_start <= @toTs AND (ts_end IS NULL OR ts_end >= @fromTs)
            ORDER BY ts_start DESC
            """,
            new { volume, fromTs, toTs })
            .Select(r => new SwingRow(
                r.Id, r.Volume, r.Kind, r.Stage, r.AcrossDowntime != 0, r.TsStart, r.TsEnd,
                r.UsedDeltaBytes, r.AttributedBytes, r.UnaccountedBytes, r.ProbeWindowStart, r.ProbeWindowEnd))
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
        long? UsedDeltaBytes, long? AttributedBytes, long? UnaccountedBytes, long? ProbeWindowStart, long? ProbeWindowEnd);
}
