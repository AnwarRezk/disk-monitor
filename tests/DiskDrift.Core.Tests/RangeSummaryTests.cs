using DiskDrift.Core.Analysis;
using DiskDrift.Core.Data;

namespace DiskDrift.Core.Tests;

public sealed class RangeSummaryTests
{
    [Fact]
    public void Summarises_free_now_change_and_lowest_over_the_range()
    {
        var summary = RangeSummary.From(
        [
            new VolumeSample(1, "C:", 100 * Bytes.GB, 40 * Bytes.GB),
            new VolumeSample(2, "C:", 100 * Bytes.GB, 31 * Bytes.GB),
            new VolumeSample(3, "C:", 100 * Bytes.GB, 37 * Bytes.GB),
        ]);

        Assert.Equal(new RangeSummary(37 * Bytes.GB, 100 * Bytes.GB, -3 * Bytes.GB, 31 * Bytes.GB, 1), summary);
        Assert.Equal(37.0, summary!.FreePercent);
        Assert.Null(RangeSummary.From([]));
        Assert.Null(new RangeSummary(1, 0, 0, 1, 0).FreePercent);
    }

    [Fact]
    public void Formats_sizes_for_the_dashboard()
    {
        Assert.Equal("49.5 GB", SizeText.Gb((long)(49.5 * Bytes.GB)));
        Assert.Equal("1.5 TB", SizeText.Gb(1536 * Bytes.GB));
        Assert.Equal("+1.4 GB", SizeText.SignedGb((long)(1.4 * Bytes.GB)));
        Assert.Equal("−2.0 GB", SizeText.SignedGb(-2 * Bytes.GB));
        Assert.Equal("±0.0 GB", SizeText.SignedGb(10 * Bytes.MB));
        Assert.Equal("820 MB", SizeText.Compact(820 * Bytes.MB));
        Assert.Equal("4.2 GB", SizeText.Compact((long)(4.2 * Bytes.GB)));
        Assert.Equal("640 KB", SizeText.Compact(640 * Bytes.KB));
    }
}
