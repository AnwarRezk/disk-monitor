using DiskDrift.Core.Data;

namespace DiskDrift.Core.Analysis;

/// <summary>
/// "At this rate, C: is full in N days": a least-squares line through free space over time. <see cref="DaysToFull"/>
/// is null when free space is not falling (or there is too little history to tell); <see cref="SpanDays"/> says how
/// much history the trend rests on.
/// </summary>
public sealed record FullForecast(double? DaysToFull, double BytesPerDay, double SpanDays)
{
    /// <summary>How far back the trend looks.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromDays(7);

    /// <summary>Less history than this says nothing about a trend.</summary>
    public static readonly TimeSpan MinSpan = TimeSpan.FromHours(6);

    /// <summary>Falling slower than this (bytes a day) counts as steady.</summary>
    public const long SteadyBytesPerDay = Bytes.GB / 10;

    /// <summary>The trend over <paramref name="samples"/> (oldest first, one Volume), or null with too little history.</summary>
    public static FullForecast? Estimate(IReadOnlyList<VolumeSample> samples)
    {
        if (samples.Count < 3)
            return null;
        var t0 = samples[0].Ts;
        var spanMs = samples[^1].Ts - t0;
        if (spanMs < MinSpan.TotalMilliseconds)
            return null;

        // Fit in days and GB to keep the sums well-conditioned.
        double n = samples.Count, sx = 0, sy = 0, sxx = 0, sxy = 0;
        foreach (var s in samples)
        {
            var x = (s.Ts - t0) / 86_400_000.0;
            var y = (double)s.FreeBytes / Bytes.GB;
            sx += x;
            sy += y;
            sxx += x * x;
            sxy += x * y;
        }
        var denominator = (n * sxx) - (sx * sx);
        if (denominator <= 0)
            return null;
        var gbPerDay = ((n * sxy) - (sx * sy)) / denominator;
        var bytesPerDay = gbPerDay * Bytes.GB;
        var spanDays = spanMs / 86_400_000.0;
        var free = samples[^1].FreeBytes;
        double? days = bytesPerDay < -SteadyBytesPerDay ? Math.Max(0, free / -bytesPerDay) : null;
        return new FullForecast(days, bytesPerDay, spanDays);
    }
}
