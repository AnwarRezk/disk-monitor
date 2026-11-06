namespace DiskDrift.Core.Probes;

/// <summary>
/// One Probe's size at one run: a row of <c>probe_samples</c> minus its timestamp. <see cref="Error"/> is set when the
/// size may be incomplete (a timeout keeps the partial size). A missing path measures 0 with no error.
/// </summary>
public sealed record ProbeMeasurement(long SizeBytes, long? FileCount, long DurationMs, string? Error = null)
{
    public static readonly ProbeMeasurement Missing = new(0, 0, 0);
}
