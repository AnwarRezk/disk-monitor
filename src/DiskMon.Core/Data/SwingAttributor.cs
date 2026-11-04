using Dapper;
using DiskMon.Core.Analysis;

namespace DiskMon.Core.Data;

/// <summary>A Swing that Attribution just moved to Closed, for the log.</summary>
public sealed record AttributedSwing(ClosingSwing Swing, AttributionResult Result);

/// <summary>
/// Moves Closing Swings to Closed: loads their Probe Samples and Top Writers, runs <see cref="Attribution"/> and
/// stores the result. A Swing is ready once a Probe run on its Volume has samples at or after its end; after a
/// restart, every Closing Swing is attributed with whatever samples exist.
/// </summary>
public sealed class SwingAttributor(IDbConnectionSource db)
{
    private readonly SwingStore _swings = new(db);

    /// <summary>Attributes each Closing Swing whose after-side Probe run has been stored. Returns what it closed.</summary>
    public IReadOnlyList<AttributedSwing> AttributeReady() => AttributeClosing(HasAfterSide);

    /// <summary>
    /// Attributes every Closing Swing now, as at Collector startup: a post-close Probe run that never finished before
    /// the restart isn't waited for (a run after the restart would mix in whatever changed during the Downtime), so
    /// Probes without an after-side sample are left out and their part counts as Unaccounted.
    /// </summary>
    public IReadOnlyList<AttributedSwing> AttributeAfterRestart() => AttributeClosing(_ => true);

    /// <summary>
    /// Attributes, without waiting, the Closing Swings that ended at or before <paramref name="ts"/>: a Swing closed
    /// by Downtime ends before the gap, so the next Probe run would also measure whatever changed during it.
    /// </summary>
    public IReadOnlyList<AttributedSwing> AttributeEndedBy(long ts) => AttributeClosing(s => s.TsEnd <= ts);

    private List<AttributedSwing> AttributeClosing(Func<ClosingSwing, bool> ready)
    {
        var closed = new List<AttributedSwing>();
        foreach (var swing in _swings.ClosingSwings())
        {
            if (!ready(swing))
                continue;
            var result = Attribute(swing);
            if (_swings.Close(swing.Id, result))
                closed.Add(new AttributedSwing(swing, result));
        }
        return closed;
    }

    private bool HasAfterSide(ClosingSwing swing)
    {
        using var connection = db.OpenConnection();
        return connection.ExecuteScalar<long>(
            """
            SELECT EXISTS (SELECT 1 FROM probe_samples s JOIN probes p ON p.id = s.probe_id
                           WHERE p.volume = @Volume COLLATE NOCASE AND s.ts >= @TsEnd)
            """,
            swing) != 0;
    }

    /// <summary>Runs Attribution for one Swing on the samples stored now.</summary>
    public AttributionResult Attribute(ClosingSwing swing)
    {
        using var connection = db.OpenConnection();

        // Probe runs share one ts per run, so the bracketing runs bound every Probe's nearest samples.
        var from = connection.ExecuteScalar<long?>(
            """
            SELECT MAX(s.ts) FROM probe_samples s JOIN probes p ON p.id = s.probe_id
            WHERE p.volume = @Volume COLLATE NOCASE AND s.ts <= @TsStart
            """, swing) ?? swing.TsStart;
        var to = connection.ExecuteScalar<long?>(
            """
            SELECT MIN(s.ts) FROM probe_samples s JOIN probes p ON p.id = s.probe_id
            WHERE p.volume = @Volume COLLATE NOCASE AND s.ts >= @TsEnd
            """, swing) ?? swing.TsEnd;

        var probes = connection.Query<(long Id, string Name, string Path, long Ts, long SizeBytes, string? Error)>(
            """
            SELECT p.id, p.name, p.path, s.ts, s.size_bytes, s.error
            FROM probes p JOIN probe_samples s ON s.probe_id = p.id
            WHERE p.volume = @volume COLLATE NOCASE AND s.ts BETWEEN @from AND @to
            """,
            new { volume = swing.Volume, from, to })
            .GroupBy(r => r.Id)
            .Select(g => new ProbeHistory(
                g.First().Name, g.First().Path, g.Select(r => new ProbePoint(r.Ts, r.SizeBytes, r.Error is not null)).ToList()))
            .ToList();

        var writers = ProcessIoStore.TopWriters(connection, swing.TsStart, swing.TsEnd, Attribution.MaxProcessItems)
            .Select(w => new WriterDelta(w.Name, w.ExePath, w.WriteBytes))
            .ToList();

        return Attribution.Attribute(swing.TsStart, swing.TsEnd, swing.UsedDeltaBytes, probes, writers);
    }
}
