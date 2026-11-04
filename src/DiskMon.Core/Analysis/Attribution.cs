namespace DiskMon.Core.Analysis;

/// <summary>One Probe Sample as Attribution sees it. <paramref name="Failed"/> marks a sample stored with an error.</summary>
public sealed record ProbePoint(long Ts, long SizeBytes, bool Failed = false);

/// <summary>A Probe on the Swing's Volume and its samples around the Swing, in any order.</summary>
public sealed record ProbeHistory(string Name, string Path, IReadOnlyList<ProbePoint> Samples);

/// <summary>One program's bytes written during a Swing (a Top Writer), before ranking.</summary>
public sealed record WriterDelta(string Name, string? ExePath, long WriteBytes);

/// <summary>
/// One row of a Swing's Attribution, as stored in <c>swing_items</c>. <paramref name="Source"/> is <c>probe</c> or
/// <c>process</c>; <paramref name="Rank"/> counts from 1 within each source.
/// </summary>
public sealed record AttributionItem(string Source, string Label, string? Path, long DeltaBytes, int Rank);

/// <summary>
/// What Attribution found for one Swing. The Probe Window is null when no Probe had samples on both sides.
/// <see cref="AttributedBytes"/> is the sum of the kept Probe items, so the items plus Unaccounted add up to the
/// Swing's used delta.
/// </summary>
public sealed record AttributionResult(
    long? ProbeWindowStart,
    long? ProbeWindowEnd,
    long AttributedBytes,
    long UnaccountedBytes,
    IReadOnlyList<AttributionItem> Items);

/// <summary>
/// Explains a Swing (PLAN §5.4): which Probes changed across its Probe Window, and which programs wrote the most
/// during it. Pure: the Collector loads the samples and stores the result.
/// </summary>
public static class Attribution
{
    public const string ProbeSource = "probe";
    public const string ProcessSource = "process";

    /// <summary>Probe items smaller than this (either way) are left out, and so count towards Unaccounted.</summary>
    public const long MinProbeDeltaBytes = 50 * Bytes.MB;

    /// <summary>How many Top Writers a Swing keeps.</summary>
    public const int MaxProcessItems = 10;

    /// <summary>
    /// For each Probe, compares its nearest sample at or before <paramref name="tsStart"/> with its nearest at or
    /// after <paramref name="tsEnd"/>. A Probe without a sample on both sides, or whose bracketing samples failed
    /// (a timed-out scan keeps a partial size), is left out. The Probe Window is the earliest before-side and latest
    /// after-side time of the Probes compared.
    /// </summary>
    public static AttributionResult Attribute(
        long tsStart,
        long tsEnd,
        long usedDeltaBytes,
        IReadOnlyList<ProbeHistory> probes,
        IReadOnlyList<WriterDelta> writers)
    {
        long? windowStart = null, windowEnd = null;
        var deltas = new List<(ProbeHistory Probe, long Delta)>();
        foreach (var probe in probes)
        {
            var before = probe.Samples.Where(s => s.Ts <= tsStart).MaxBy(s => s.Ts);
            var after = probe.Samples.Where(s => s.Ts >= tsEnd).MinBy(s => s.Ts);
            if (before is null || after is null || before.Failed || after.Failed)
                continue;
            windowStart = windowStart is { } ws ? Math.Min(ws, before.Ts) : before.Ts;
            windowEnd = windowEnd is { } we ? Math.Max(we, after.Ts) : after.Ts;
            deltas.Add((probe, after.SizeBytes - before.SizeBytes));
        }

        var probeItems = deltas
            .Where(d => Math.Abs(d.Delta) >= MinProbeDeltaBytes)
            .OrderByDescending(d => Math.Abs(d.Delta))
            .ThenBy(d => d.Probe.Name, StringComparer.OrdinalIgnoreCase)
            .Select((d, i) => new AttributionItem(ProbeSource, d.Probe.Name, d.Probe.Path, d.Delta, i + 1))
            .ToList();

        var processItems = writers
            .Where(w => w.WriteBytes > 0)
            .OrderByDescending(w => w.WriteBytes)
            .ThenBy(w => w.Name, StringComparer.OrdinalIgnoreCase)
            .Take(MaxProcessItems)
            .Select((w, i) => new AttributionItem(ProcessSource, w.Name, w.ExePath, w.WriteBytes, i + 1))
            .ToList();

        // Process write bytes are a ranking signal on another axis: never part of Attributed.
        var attributed = probeItems.Sum(i => i.DeltaBytes);
        return new AttributionResult(windowStart, windowEnd, attributed, usedDeltaBytes - attributed, [.. probeItems, .. processItems]);
    }
}
