using DiskDrift.Core.Analysis;

namespace DiskDrift.Core.Data;

/// <summary>
/// Runs the <see cref="SwingDetector"/> over the Collector's Volume Samples and writes what it decides to the
/// <c>swings</c> table. Holds one detector state per Volume and the id of each Volume's Open Swing. Not thread-safe:
/// the Collector's <c>SwingService</c> is its only caller.
/// </summary>
public sealed class SwingRecorder(SwingStore swings, VolumeSampleStore samples, SwingSettings settings)
{
    private readonly Dictionary<string, VolumeSwingState> _states = [];
    private readonly Dictionary<string, long> _openIds = [];

    /// <summary>
    /// Picks up where the last run stopped: reloads each Volume's last window of samples and closes any Swing left
    /// Open at the last sample before the restart. Call it before new samples are stored, so the restart gap is seen
    /// as Downtime. Returns what it applied.
    /// </summary>
    public IReadOnlyList<SwingAction> Restore(IEnumerable<string> volumes)
    {
        var applied = new List<SwingAction>();
        var open = swings.OpenSwings();
        foreach (var volume in volumes.Concat(open.Select(o => o.Volume)).Distinct())
        {
            var latest = samples.Latest(volume);
            var recent = latest is null ? [] : samples.Range(volume, latest.Ts - settings.WindowMs, latest.Ts);
            var state = VolumeSwingState.Empty(volume) with { Buffer = recent };
            foreach (var swing in open.Where(o => o.Volume == volume))
            {
                var freeAtStart = samples.AtOrBefore(volume, swing.TsStart)?.FreeBytes ?? recent.FirstOrDefault()?.FreeBytes ?? 0;
                var openSwing = new OpenSwing(swing.Kind, swing.TsStart, freeAtStart, swing.TsStart, freeAtStart);
                (state, var actions) = SwingDetector.Restore(volume, recent, openSwing, settings);
                _openIds[volume] = swing.Id;
                Apply(actions);
                applied.AddRange(actions);
            }
            _states[volume] = state;
        }
        return applied;
    }

    /// <summary>Feeds one new Volume Sample through the detector and applies the result. Returns what it applied.</summary>
    public IReadOnlyList<SwingAction> Add(VolumeSample sample)
    {
        var state = _states.GetValueOrDefault(sample.Volume) ?? VolumeSwingState.Empty(sample.Volume);
        (state, var actions) = SwingDetector.Detect(state, [sample], settings);
        _states[sample.Volume] = state; // The detector moves on even if a write below fails.
        Apply(actions);
        return actions;
    }

    private void Apply(IReadOnlyList<SwingAction> actions)
    {
        foreach (var action in actions)
        {
            switch (action)
            {
                case SwingOpened o:
                    _openIds[o.Volume] = swings.InsertOpen(o.Volume, o.Kind, o.TsStart);
                    break;
                case SwingClosed c when _openIds.Remove(c.Volume, out var id):
                    swings.MarkClosing(id, c.TsEnd, c.UsedDeltaBytes);
                    break;
                case SwingClosed:
                    break; // Nothing Open in the DB (its insert failed): nothing to close.
                case DowntimeSwing d:
                    swings.InsertAcrossDowntime(d.Volume, d.Kind, d.TsStart, d.TsEnd, d.UsedDeltaBytes);
                    break;
            }
        }
    }
}
