using DiskMon.Core.Data;

namespace DiskMon.Core.Analysis;

/// <summary>One point of the free-space chart. <paramref name="Ts"/> is UTC Unix epoch ms.</summary>
public readonly record struct ChartPoint(long Ts, long FreeBytes);

/// <summary>
/// Turns Volume Samples into the points the free-space chart draws. Pure: the caller reads the samples.
/// </summary>
public static class ChartSeries
{
    /// <summary>
    /// The samples split into runs at every Downtime (a gap longer than <paramref name="maxGapMs"/>), so the chart
    /// draws a break there instead of a straight line across it. Each run is thinned with <see cref="Thin"/>, sharing
    /// <paramref name="maxPoints"/> in proportion to its length (at least 2 points each). Input must be ordered by time.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<ChartPoint>> Segments(IReadOnlyList<VolumeSample> samples, int maxPoints, long maxGapMs)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPoints, 2);
        var runs = new List<List<VolumeSample>>();
        foreach (var s in samples)
        {
            if (runs.Count == 0 || s.Ts - runs[^1][^1].Ts > maxGapMs)
                runs.Add([]);
            runs[^1].Add(s);
        }
        if (runs.Count == 0)
            return [];

        var span = Math.Max(1, samples[^1].Ts - samples[0].Ts);
        return runs
            .Select(run => Thin(run, Math.Max(2, (int)(maxPoints * (double)(run[^1].Ts - run[0].Ts) / span))))
            .ToList();
    }

    /// <summary>
    /// The samples as chart points, thinned to at most about <paramref name="maxPoints"/>. Samples are split into
    /// equal-width time buckets and each bucket keeps its lowest and highest free-space sample, in time order, so a
    /// Swing's dip or jump survives thinning. Input must be ordered by time; with few enough samples all are kept.
    /// </summary>
    public static IReadOnlyList<ChartPoint> Thin(IReadOnlyList<VolumeSample> samples, int maxPoints)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPoints, 2);
        if (samples.Count <= maxPoints)
            return samples.Select(s => new ChartPoint(s.Ts, s.FreeBytes)).ToList();

        var buckets = maxPoints / 2;
        var first = samples[0].Ts;
        var span = Math.Max(1, samples[^1].Ts - first + 1);
        var points = new List<ChartPoint>(buckets * 2);

        var i = 0;
        for (var b = 0; b < buckets && i < samples.Count; b++)
        {
            var bucketEnd = first + (long)Math.Ceiling(span * (b + 1) / (double)buckets);
            VolumeSample? min = null, max = null;
            for (; i < samples.Count && (samples[i].Ts < bucketEnd || b == buckets - 1); i++)
            {
                var s = samples[i];
                if (min is null || s.FreeBytes < min.FreeBytes)
                    min = s;
                if (max is null || s.FreeBytes > max.FreeBytes)
                    max = s;
            }

            if (min is null || max is null)
                continue;
            if (min.Ts == max.Ts)
            {
                points.Add(new ChartPoint(min.Ts, min.FreeBytes));
                continue;
            }

            var (early, late) = min.Ts < max.Ts ? (min, max) : (max, min);
            points.Add(new ChartPoint(early.Ts, early.FreeBytes));
            points.Add(new ChartPoint(late.Ts, late.FreeBytes));
        }

        return points;
    }
}
