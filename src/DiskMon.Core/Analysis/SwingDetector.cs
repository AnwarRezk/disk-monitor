using DiskMon.Core.Config;
using DiskMon.Core.Data;

namespace DiskMon.Core.Analysis;

/// <summary>Growth: used space rises (free space drops). Release: used space falls (free space rises).</summary>
public enum SwingKind
{
    Growth,
    Release,
}

/// <summary>Why a Swing stopped being Open.</summary>
public enum SwingCloseReason
{
    /// <summary>The window Δ stayed below half the threshold for <see cref="SwingDetector.SettledSamples"/> samples.</summary>
    Settled,

    /// <summary>The Swing reached the maximum duration.</summary>
    MaxDuration,

    /// <summary>Free space moved back by the threshold from the peak: closed at the peak, the opposite kind opened.</summary>
    Flip,

    /// <summary>Volume Samples stopped (Downtime) while it was open: closed at the last sample before the gap.</summary>
    Downtime,

    /// <summary>The Collector restarted while it was open: closed at the last sample before the restart.</summary>
    Restart,
}

/// <summary>What the Collector must do to the <c>swings</c> table. One Volume has at most one Open Swing.</summary>
public abstract record SwingAction(string Volume);

/// <summary>Insert an Open Swing starting at <paramref name="TsStart"/>, and run the Probes.</summary>
public sealed record SwingOpened(string Volume, SwingKind Kind, long TsStart) : SwingAction(Volume);

/// <summary>Move the Volume's Open Swing to Closing, ending at <paramref name="TsEnd"/>, and run the Probes.</summary>
public sealed record SwingClosed(string Volume, SwingKind Kind, long TsStart, long TsEnd, long UsedDeltaBytes, SwingCloseReason Reason)
    : SwingAction(Volume);

/// <summary>Insert a Closing Swing measured across Downtime (<c>across_downtime = 1</c>), and run the Probes.</summary>
public sealed record DowntimeSwing(string Volume, SwingKind Kind, long TsStart, long TsEnd, long UsedDeltaBytes) : SwingAction(Volume);

/// <summary>The detector's tuning, from <see cref="SwingOptions"/> and the Volume Sample interval.</summary>
public sealed record SwingSettings(long ThresholdBytes, long WindowMs, long MaxDurationMs, long SampleIntervalMs)
{
    /// <summary>A gap between consecutive Volume Samples longer than this is Downtime.</summary>
    public long MaxGapMs => 2 * SampleIntervalMs;

    public static SwingSettings From(DiskMonOptions options) => new(
        options.Swing.ThresholdBytes,
        options.Swing.WindowMinutes * 60_000L,
        options.Swing.MaxDurationMinutes * 60_000L,
        options.Intervals.VolumeSampleSeconds * 1_000L);
}

/// <summary>A Swing that is still Open: where it started and the most extreme free space reached so far.</summary>
public sealed record OpenSwing(SwingKind Kind, long TsStart, long FreeAtStart, long PeakTs, long PeakFree);

/// <summary>
/// Everything the detector remembers about one Volume between samples: the ring buffer (samples of the last window,
/// oldest first), the Open Swing if any, how many consecutive samples looked settled, and the earliest time a new
/// Swing may start (the end of the last one, so Swings never overlap).
/// </summary>
public sealed record VolumeSwingState(
    string Volume,
    IReadOnlyList<VolumeSample> Buffer,
    OpenSwing? Open,
    int SettledCount,
    long NotBefore)
{
    public static VolumeSwingState Empty(string volume) => new(volume, [], null, 0, long.MinValue);

    public VolumeSample? Last => Buffer.Count > 0 ? Buffer[^1] : null;
}

/// <summary>
/// Detects Swings on one Volume from its Volume Samples (PLAN §5.4). A pure function: state plus ordered samples in,
/// new state plus actions out. No clock, no I/O; the Collector's <c>SwingService</c> applies the actions.
/// <list type="bullet">
/// <item>Open: |Δfree| ≥ threshold between the new sample and one in the window. The start is that compared sample:
/// the one furthest from the new sample (the latest of equals), so the start sits close to where the move began.</item>
/// <item>Direction flip: free space moves back by ≥ threshold from the Swing's peak. The Swing closes at its peak and
/// a Swing of the other kind opens there.</item>
/// <item>Close: the window Δ stays below threshold / 2 for <see cref="SettledSamples"/> samples, or the Swing reaches
/// its maximum duration. It ends at the current sample, when the after-side Probe run starts.</item>
/// <item>Downtime: a gap longer than 2 sample intervals closes any Open Swing at the last sample before the gap and
/// resets the ring buffer; a move ≥ threshold across the gap is recorded as a Swing across Downtime.</item>
/// </list>
/// </summary>
public static class SwingDetector
{
    /// <summary>Consecutive settled samples needed to close a Swing.</summary>
    public const int SettledSamples = 2;

    /// <summary>Feeds <paramref name="samples"/> (this Volume's, oldest first) through the detector.</summary>
    public static (VolumeSwingState State, IReadOnlyList<SwingAction> Actions) Detect(
        VolumeSwingState state, IEnumerable<VolumeSample> samples, SwingSettings settings)
    {
        var actions = new List<SwingAction>();
        foreach (var sample in samples)
            state = Step(state, sample, settings, actions);
        return (state, actions);
    }

    /// <summary>
    /// The state after a Collector restart, from the Volume's newest samples (oldest first, the last window is enough)
    /// and the Swing left Open in the DB, if any. That Swing is closed at the last sample before the restart. The gap
    /// to the first new sample then goes through the usual Downtime rule.
    /// </summary>
    public static (VolumeSwingState State, IReadOnlyList<SwingAction> Actions) Restore(
        string volume, IReadOnlyList<VolumeSample> recent, OpenSwing? open, SwingSettings settings)
    {
        var buffer = recent.Where(s => s.Volume == volume).OrderBy(s => s.Ts).ToList();
        if (buffer.Count > 0)
            buffer = Trim(buffer, buffer[^1].Ts, settings);
        var state = VolumeSwingState.Empty(volume) with { Buffer = buffer };
        if (open is null)
            return (state, []);

        // No sample since the Swing opened (pruned or never written): end it where it started.
        var last = buffer.Count > 0 && buffer[^1].Ts >= open.TsStart ? buffer[^1] : new VolumeSample(open.TsStart, volume, 0, open.FreeAtStart);
        var closed = new SwingClosed(volume, open.Kind, open.TsStart, last.Ts, open.FreeAtStart - last.FreeBytes, SwingCloseReason.Restart);
        return (state with { NotBefore = last.Ts }, [closed]);
    }

    private static VolumeSwingState Step(VolumeSwingState state, VolumeSample sample, SwingSettings settings, List<SwingAction> actions)
    {
        if (sample.Volume != state.Volume)
            throw new ArgumentException($"Sample for {sample.Volume} fed to the detector of {state.Volume}.", nameof(sample));

        var last = state.Last;
        if (last is not null && sample.Ts <= last.Ts)
            return state; // Out of order or duplicate: the timeline only moves forward.

        if (last is not null && sample.Ts - last.Ts > settings.MaxGapMs)
            state = AcrossDowntime(state, last, sample, settings, actions);

        var buffer = Trim([.. state.Buffer, sample], sample.Ts, settings);
        state = state with { Buffer = buffer };

        return state.Open is null
            ? TryOpen(state, sample, settings, actions)
            : Track(state, state.Open, sample, settings, actions);
    }

    private static VolumeSwingState AcrossDowntime(
        VolumeSwingState state, VolumeSample before, VolumeSample after, SwingSettings settings, List<SwingAction> actions)
    {
        if (state.Open is { } open)
            actions.Add(new SwingClosed(state.Volume, open.Kind, open.TsStart, before.Ts, open.FreeAtStart - before.FreeBytes, SwingCloseReason.Downtime));

        var freeDelta = after.FreeBytes - before.FreeBytes;
        if (Math.Abs(freeDelta) >= settings.ThresholdBytes)
            actions.Add(new DowntimeSwing(state.Volume, KindOf(freeDelta), before.Ts, after.Ts, -freeDelta));

        // The ring buffer restarts after the gap: the move across it is already accounted for.
        return state with { Buffer = [], Open = null, SettledCount = 0, NotBefore = after.Ts };
    }

    private static VolumeSwingState TryOpen(VolumeSwingState state, VolumeSample sample, SwingSettings settings, List<SwingAction> actions)
    {
        if (Furthest(state.Buffer, sample, state.NotBefore) is not { } reference
            || Math.Abs(sample.FreeBytes - reference.FreeBytes) < settings.ThresholdBytes)
            return state;

        var kind = KindOf(sample.FreeBytes - reference.FreeBytes);
        var peak = state.Buffer
            .Where(s => s.Ts > reference.Ts)
            .Aggregate(sample, (best, s) => AtOrBeyond(kind, s.FreeBytes, best.FreeBytes) ? s : best);
        actions.Add(new SwingOpened(state.Volume, kind, reference.Ts));
        return state with
        {
            Open = new OpenSwing(kind, reference.Ts, reference.FreeBytes, peak.Ts, peak.FreeBytes),
            SettledCount = 0,
        };
    }

    private static VolumeSwingState Track(
        VolumeSwingState state, OpenSwing open, VolumeSample sample, SwingSettings settings, List<SwingAction> actions)
    {
        // The peak is the latest sample at the most extreme free space, so a flip closes when the move turned back.
        if (AtOrBeyond(open.Kind, sample.FreeBytes, open.PeakFree))
            open = open with { PeakTs = sample.Ts, PeakFree = sample.FreeBytes };

        // Direction flip: back by the threshold from the peak. Close at the peak, open the other kind there.
        if (Math.Abs(sample.FreeBytes - open.PeakFree) >= settings.ThresholdBytes)
        {
            actions.Add(new SwingClosed(state.Volume, open.Kind, open.TsStart, open.PeakTs, open.FreeAtStart - open.PeakFree, SwingCloseReason.Flip));
            var kind = Opposite(open.Kind);
            actions.Add(new SwingOpened(state.Volume, kind, open.PeakTs));
            return state with
            {
                Open = new OpenSwing(kind, open.PeakTs, open.PeakFree, sample.Ts, sample.FreeBytes),
                SettledCount = 0,
                NotBefore = open.PeakTs,
            };
        }

        if (sample.Ts - open.TsStart >= settings.MaxDurationMs)
            return Close(state, open, sample, SwingCloseReason.MaxDuration, actions);

        var windowDelta = Furthest(state.Buffer, sample, long.MinValue) is { } reference
            ? Math.Abs(sample.FreeBytes - reference.FreeBytes)
            : 0;
        var settled = windowDelta < settings.ThresholdBytes / 2 ? state.SettledCount + 1 : 0;
        if (settled >= SettledSamples)
            return Close(state, open, sample, SwingCloseReason.Settled, actions);

        return state with { Open = open, SettledCount = settled };
    }

    private static VolumeSwingState Close(VolumeSwingState state, OpenSwing open, VolumeSample end, SwingCloseReason reason, List<SwingAction> actions)
    {
        actions.Add(new SwingClosed(state.Volume, open.Kind, open.TsStart, end.Ts, open.FreeAtStart - end.FreeBytes, reason));
        return state with { Open = null, SettledCount = 0, NotBefore = end.Ts };
    }

    /// <summary>The buffered sample (at or after <paramref name="notBefore"/>, before <paramref name="sample"/>) whose free space differs most from it; the latest of equals.</summary>
    private static VolumeSample? Furthest(IReadOnlyList<VolumeSample> buffer, VolumeSample sample, long notBefore)
    {
        VolumeSample? best = null;
        var bestDelta = -1L;
        foreach (var s in buffer)
        {
            if (s.Ts < notBefore || s.Ts >= sample.Ts)
                continue;
            var delta = Math.Abs(sample.FreeBytes - s.FreeBytes);
            if (delta >= bestDelta)
            {
                best = s;
                bestDelta = delta;
            }
        }
        return best;
    }

    /// <summary>Keeps the samples of the last window (and the newest), oldest first.</summary>
    private static List<VolumeSample> Trim(List<VolumeSample> buffer, long now, SwingSettings settings) =>
        buffer.Where(s => s.Ts >= now - settings.WindowMs).ToList();

    private static SwingKind KindOf(long freeDelta) => freeDelta < 0 ? SwingKind.Growth : SwingKind.Release;

    private static SwingKind Opposite(SwingKind kind) => kind == SwingKind.Growth ? SwingKind.Release : SwingKind.Growth;

    /// <summary>True when <paramref name="free"/> is as far in the Swing's direction as <paramref name="than"/>, or further.</summary>
    private static bool AtOrBeyond(SwingKind kind, long free, long than) => kind == SwingKind.Growth ? free <= than : free >= than;
}
