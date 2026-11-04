using System.Reflection;

namespace DiskMon.Collector.Services;

/// <summary>What <c>ping</c> reports: the Collector's version and when <see cref="Samplers.VolumeSampler"/> last recorded a sample.</summary>
public sealed class CollectorHeartbeat
{
    private long _lastSampleTs = -1;

    public static string Version { get; } =
        typeof(CollectorHeartbeat).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(CollectorHeartbeat).Assembly.GetName().Version?.ToString()
        ?? "unknown";

    /// <summary>UTC epoch ms of the newest stored Volume Sample, or null before the first one.</summary>
    public long? LastSampleTs => Interlocked.Read(ref _lastSampleTs) is var ts and >= 0 ? ts : null;

    public void SampleStored(long ts) => Interlocked.Exchange(ref _lastSampleTs, ts);
}
