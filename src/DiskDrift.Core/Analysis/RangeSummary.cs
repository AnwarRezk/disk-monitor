using System.Globalization;
using DiskDrift.Core.Data;

namespace DiskDrift.Core.Analysis;

/// <summary>The figures above the dashboard chart for one Volume and range.</summary>
public sealed record RangeSummary(long FreeBytes, long TotalBytes, long ChangeBytes, long LowestFreeBytes, long FirstTs)
{
    /// <summary>Free space as a percentage of the Volume's size, or null when the size is unknown.</summary>
    public double? FreePercent => TotalBytes > 0 ? 100.0 * FreeBytes / TotalBytes : null;

    /// <summary>
    /// From the range's samples, oldest first: free space now (the newest sample), the change from the first
    /// sample to the newest, the lowest free space seen, and when the first sample was taken (the change is
    /// measured from there, which is later than the range start while history is short). Null when there are no samples.
    /// </summary>
    public static RangeSummary? From(IReadOnlyList<VolumeSample> samples)
    {
        if (samples.Count == 0)
            return null;
        var latest = samples[^1];
        return new RangeSummary(latest.FreeBytes, latest.TotalBytes, latest.FreeBytes - samples[0].FreeBytes, samples.Min(s => s.FreeBytes), samples[0].Ts);
    }
}

/// <summary>Byte and size text for the dashboard. Uses "−" (U+2212) for negative changes, as the tray tooltip does.</summary>
public static class SizeText
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary><c>12.3 GB</c>, or <c>1.2 TB</c> from 1000 GB up.</summary>
    public static string Gb(long bytes)
    {
        var gb = (double)bytes / Bytes.GB;
        return Math.Abs(gb) >= 1000
            ? (gb / 1024).ToString("0.0", Invariant) + " TB"
            : gb.ToString("0.0", Invariant) + " GB";
    }

    /// <summary><c>+1.4 GB</c> or <c>−2.0 GB</c>; a change under 0.05 GB is <c>±0.0 GB</c>.</summary>
    public static string SignedGb(long bytes)
    {
        if (Math.Abs(bytes) < Bytes.GB / 20)
            return "±0.0 GB";
        return (bytes < 0 ? "−" : "+") + Gb(Math.Abs(bytes));
    }

    /// <summary>A compact size for Top Writers: <c>640 KB</c>, <c>820 MB</c>, <c>4.2 GB</c>.</summary>
    public static string Compact(long bytes) => Math.Abs(bytes) switch
    {
        >= Bytes.GB => ((double)bytes / Bytes.GB).ToString("0.0", Invariant) + " GB",
        >= Bytes.MB => ((double)bytes / Bytes.MB).ToString("0", Invariant) + " MB",
        _ => ((double)bytes / Bytes.KB).ToString("0", Invariant) + " KB",
    };
}
