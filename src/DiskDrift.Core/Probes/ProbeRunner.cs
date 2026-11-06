using DiskDrift.Core.Config;

namespace DiskDrift.Core.Probes;

/// <summary>One Probe and what a run measured for it.</summary>
public sealed record ProbeResult(ExpandedProbe Probe, ProbeMeasurement Measurement);

/// <summary>
/// Measures a list of Probes, a few at a time so a slow one (each stops at its own timeout) does not hold up the
/// rest. VSS shadow storage is read once per run for all vss Probes.
/// </summary>
public sealed class ProbeRunner(
    IShadowStorageReader shadowStorage,
    TimeSpan probeTimeout,
    int maxParallel = 2,
    ProbeExpander? expander = null)
{
    public async Task<IReadOnlyList<ProbeResult>> RunAsync(IReadOnlyList<ExpandedProbe> probes, CancellationToken token)
    {
        var results = new ProbeMeasurement[probes.Count];
        var vss = new Lazy<Func<string, ProbeMeasurement>>(ReadShadowStorage, LazyThreadSafetyMode.ExecutionAndPublication);

        await Parallel.ForEachAsync(
            Enumerable.Range(0, probes.Count),
            new ParallelOptions { MaxDegreeOfParallelism = maxParallel, CancellationToken = token },
            (i, ct) =>
            {
                var probe = probes[i];
                results[i] = probe.Kind == ProbeKind.Vss ? vss.Value(probe.Volume) : ProbeMeasurer.Measure(probe, probeTimeout, ct, expander);
                return ValueTask.CompletedTask;
            });

        return probes.Select((p, i) => new ProbeResult(p, results[i])).ToList();
    }

    private Func<string, ProbeMeasurement> ReadShadowStorage()
    {
        var started = DateTime.UtcNow;
        try
        {
            var byVolume = shadowStorage.Read()
                .GroupBy(s => s.Volume, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Sum(s => s.AllocatedBytes), StringComparer.OrdinalIgnoreCase);
            var ms = (long)(DateTime.UtcNow - started).TotalMilliseconds;

            // No shadow storage on a Volume (System Restore off) is 0, like a missing path.
            return volume => new ProbeMeasurement(byVolume.GetValueOrDefault(volume), null, ms);
        }
        catch (Exception ex)
        {
            var ms = (long)(DateTime.UtcNow - started).TotalMilliseconds;
            return _ => new ProbeMeasurement(0, null, ms, $"VSS read failed: {ex.Message}");
        }
    }
}
