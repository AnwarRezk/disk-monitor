using DiskMon.Core.Analysis;
using DiskMon.Core.Data;

namespace DiskMon.Core.Tests;

public sealed class ChartSeriesTests
{
    private static VolumeSample S(long ts, long free) => new(ts, "C:", 100, free);

    [Fact]
    public void Few_samples_are_kept_as_they_are()
    {
        var points = ChartSeries.Thin([S(1, 10), S(2, 20), S(3, 15)], 10);

        Assert.Equal([new ChartPoint(1, 10), new ChartPoint(2, 20), new ChartPoint(3, 15)], points);
    }

    [Fact]
    public void Thinning_keeps_each_buckets_low_and_high_in_time_order_so_a_dip_survives()
    {
        // 100 flat samples with one sharp dip at ts 57.
        var samples = Enumerable.Range(0, 100).Select(i => S(i, i == 57 ? 1 : 50)).ToList();

        var points = ChartSeries.Thin(samples, 10);

        Assert.True(points.Count <= 10);
        Assert.Contains(new ChartPoint(57, 1), points);
        Assert.Equal(points.OrderBy(p => p.Ts), points);
        Assert.Equal(0, points[0].Ts);
    }

    [Fact]
    public void An_empty_series_stays_empty()
    {
        Assert.Empty(ChartSeries.Thin([], 10));
        Assert.Empty(ChartSeries.Segments([], 10, 60));
    }

    [Fact]
    public void Downtime_splits_the_series_into_runs_that_share_the_point_budget()
    {
        // 0..999 every 1, then Downtime, then 5000..5999 every 1.
        var samples = Enumerable.Range(0, 1000).Select(i => S(i, 50))
            .Concat(Enumerable.Range(5000, 1000).Select(i => S(i, 40)))
            .ToList();

        var runs = ChartSeries.Segments(samples, 100, maxGapMs: 2);

        Assert.Equal(2, runs.Count);
        Assert.All(runs, r => Assert.InRange(r.Count, 2, 50));
        Assert.Equal(0, runs[0][0].Ts);
        Assert.Equal(5000, runs[1][0].Ts);
        Assert.Single(ChartSeries.Segments(samples.Take(10).ToList(), 100, maxGapMs: 2));
    }

    [Fact]
    public void Ranges_have_their_labels_and_lengths()
    {
        Assert.Equal(["6h", "24h", "7d", "30d"], DashboardRanges.All.Select(r => r.Label()));
        Assert.Equal(1_000L, DashboardRange.Hours6.StartTs(1_000 + (6 * 3_600_000L)));
    }
}
