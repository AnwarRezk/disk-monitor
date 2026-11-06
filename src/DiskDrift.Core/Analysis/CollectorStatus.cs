using DiskDrift.Core.Ipc;

namespace DiskDrift.Core.Analysis;

/// <summary>Whether the Tray should treat the Collector as running (PLAN §5.6).</summary>
public static class CollectorStatus
{
    /// <summary>A newest sample older than this means the Collector is not sampling, even if it answers.</summary>
    public static readonly TimeSpan MaxSampleAge = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Running only if <paramref name="ping"/> succeeded and reports a Volume Sample at most
    /// <see cref="MaxSampleAge"/> old at <paramref name="nowMs"/>. A null ping means it failed.
    /// </summary>
    public static bool IsRunning(PipeResponse? ping, long nowMs) =>
        ping is { Ok: true, LastSampleTs: { } last } && nowMs - last <= (long)MaxSampleAge.TotalMilliseconds;
}
