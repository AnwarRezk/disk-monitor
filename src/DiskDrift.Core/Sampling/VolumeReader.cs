using DiskDrift.Core.Data;

namespace DiskDrift.Core.Sampling;

/// <summary>Why a configured Volume produced no sample.</summary>
public enum VolumeSkipReason
{
    NotFound,
    NotFixed,
    NotReady,
    Failed,
}

/// <summary>A configured Volume that was skipped this tick, or whose skip state changed.</summary>
public sealed record VolumeSkip(string Volume, VolumeSkipReason Reason, string? Detail = null);

/// <summary>
/// One tick's outcome. <see cref="NewlySkipped"/> and <see cref="Recovered"/> only list changes since the
/// previous tick, so a missing drive is logged once rather than every 30 seconds.
/// </summary>
public sealed record VolumeReadResult(
    IReadOnlyList<VolumeSample> Samples,
    IReadOnlyList<VolumeSkip> NewlySkipped,
    IReadOnlyList<string> Recovered);

/// <summary>
/// Reads the configured Volumes. Only fixed, ready drives are sampled; anything else (removable, network,
/// optical, missing, locked) is skipped. Keeps the last skip reason per Volume to report changes only.
/// </summary>
public sealed class VolumeReader(IReadOnlyList<string> volumes, IDriveSource drives)
{
    private readonly Dictionary<string, VolumeSkipReason> _skipped = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<string> Volumes => volumes;

    public VolumeReadResult Read(long ts)
    {
        var samples = new List<VolumeSample>(volumes.Count);
        var newlySkipped = new List<VolumeSkip>();
        var recovered = new List<string>();

        foreach (var volume in volumes)
        {
            var skip = TryRead(volume, ts, out var sample);
            if (skip is null)
            {
                samples.Add(sample!);
                if (_skipped.Remove(volume))
                    recovered.Add(volume);
            }
            else if (!_skipped.TryGetValue(volume, out var previous) || previous != skip.Reason)
            {
                _skipped[volume] = skip.Reason;
                newlySkipped.Add(skip);
            }
        }

        return new VolumeReadResult(samples, newlySkipped, recovered);
    }

    private VolumeSkip? TryRead(string volume, long ts, out VolumeSample? sample)
    {
        sample = null;
        DriveState? state;
        try
        {
            state = drives.Get(volume);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new VolumeSkip(volume, VolumeSkipReason.Failed, ex.Message);
        }

        if (state is null)
            return new VolumeSkip(volume, VolumeSkipReason.NotFound);
        if (state.Type != DriveType.Fixed)
            return new VolumeSkip(volume, VolumeSkipReason.NotFixed, state.Type.ToString());
        if (!state.IsReady)
            return new VolumeSkip(volume, VolumeSkipReason.NotReady);

        sample = new VolumeSample(ts, volume, state.TotalBytes, state.FreeBytes);
        return null;
    }
}
