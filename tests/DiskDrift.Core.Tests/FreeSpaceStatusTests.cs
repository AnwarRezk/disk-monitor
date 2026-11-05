using DiskDrift.Core.Analysis;
using DiskDrift.Core.Config;
using DiskDrift.Core.Data;

namespace DiskDrift.Core.Tests;

public sealed class FreeSpaceStatusTests
{
    private const long Hour = 3_600_000;
    private static readonly TrayOptions Defaults = new();

    private static long Gb(double gb) => (long)Math.Ceiling(gb * Bytes.GB);

    [Theory]
    // 100 GB disk: the GB limits bite first (5 % = 5 GB, 10 % = 10 GB).
    [InlineData(9.9, 100, FreeSpaceLevel.Red)]
    [InlineData(10, 100, FreeSpaceLevel.Amber)]
    [InlineData(19.9, 100, FreeSpaceLevel.Amber)]
    [InlineData(20, 100, FreeSpaceLevel.Green)]
    // 4 TB disk: the percentage limits bite first (5 % = 200 GB, 10 % = 400 GB).
    [InlineData(199, 4000, FreeSpaceLevel.Red)]
    [InlineData(200, 4000, FreeSpaceLevel.Amber)]
    [InlineData(399, 4000, FreeSpaceLevel.Amber)]
    [InlineData(400, 4000, FreeSpaceLevel.Green)]
    public void Colour_is_red_or_amber_below_either_the_percentage_or_the_GB_limit(double freeGb, double totalGb, FreeSpaceLevel expected)
    {
        Assert.Equal(expected, FreeSpaceStatus.Level(Gb(freeGb), Gb(totalGb), Defaults));
    }

    [Fact]
    public void Colour_thresholds_come_from_config()
    {
        var options = new TrayOptions { RedBelowPercent = 1, RedBelowGb = 50, AmberBelowPercent = 2, AmberBelowGb = 100 };

        Assert.Equal(FreeSpaceLevel.Red, FreeSpaceStatus.Level(Gb(40), Gb(1000), options));
        Assert.Equal(FreeSpaceLevel.Amber, FreeSpaceStatus.Level(Gb(60), Gb(1000), options));
        Assert.Equal(FreeSpaceLevel.Green, FreeSpaceStatus.Level(Gb(100), Gb(1000), options));
    }

    [Fact]
    public void An_unknown_total_uses_only_the_GB_limits()
    {
        Assert.Equal(FreeSpaceLevel.Green, FreeSpaceStatus.Level(Gb(25), 0, Defaults));
        Assert.Equal(FreeSpaceLevel.Red, FreeSpaceStatus.Level(Gb(5), 0, Defaults));
    }

    [Theory]
    [InlineData(0.4, "0")]
    [InlineData(12.9, "12")]
    [InlineData(999.9, "999")]
    [InlineData(1000, "1T")]
    [InlineData(2047, "1T")]
    [InlineData(2600, "2T")]
    public void Icon_label_is_whole_GB_rounded_down_then_whole_TB(double freeGb, string expected)
    {
        Assert.Equal(expected, FreeSpaceStatus.IconLabel(Gb(freeGb)));
    }

    [Fact]
    public void Tooltip_shows_free_space_and_the_24_hour_change()
    {
        var latest = new VolumeSample(48 * Hour, "C:", Gb(500), Gb(12.4));
        var dayAgo = new VolumeSample(24 * Hour, "C:", Gb(500), Gb(15.5));

        Assert.Equal("C: 12.4 GB free (−3.1 GB in 24h)", FreeSpaceStatus.Tooltip(latest, dayAgo));
    }

    [Fact]
    public void Tooltip_free_space_is_rounded_down_like_Explorer()
    {
        Assert.Equal("C: 16.2 GB free", FreeSpaceStatus.Tooltip(new VolumeSample(Hour, "C:", Gb(500), Gb(16.29)), null));
    }

    [Fact]
    public void Tooltip_shows_growth_of_free_space_with_a_plus()
    {
        var latest = new VolumeSample(48 * Hour, "C:", Gb(500), Gb(20));
        var dayAgo = new VolumeSample(24 * Hour - 20_000, "C:", Gb(500), Gb(12.5));

        Assert.Equal("C: 20.0 GB free (+7.5 GB in 24h)", FreeSpaceStatus.Tooltip(latest, dayAgo));
    }

    [Fact]
    public void Tooltip_names_the_real_span_when_history_is_shorter_or_longer_than_a_day()
    {
        var latest = new VolumeSample(100 * Hour, "C:", Gb(500), Gb(10));

        Assert.EndsWith("in 3h)", FreeSpaceStatus.Tooltip(latest, latest with { Ts = 97 * Hour, FreeBytes = Gb(11) }));
        Assert.EndsWith("in 20m)", FreeSpaceStatus.Tooltip(latest, latest with { Ts = 100 * Hour - 20 * 60_000, FreeBytes = Gb(11) }));
        Assert.EndsWith("in 3d)", FreeSpaceStatus.Tooltip(latest, latest with { Ts = 28 * Hour, FreeBytes = Gb(11) }));
    }

    [Fact]
    public void Tooltip_says_no_change_for_tiny_moves_and_omits_the_change_without_history()
    {
        var latest = new VolumeSample(48 * Hour, "C:", Gb(500), Gb(12.4));

        Assert.Equal("C: 12.4 GB free (no change in 24h)", FreeSpaceStatus.Tooltip(latest, latest with { Ts = 24 * Hour, FreeBytes = Gb(12.42) }));
        Assert.Equal("C: 12.4 GB free", FreeSpaceStatus.Tooltip(latest, null));
        Assert.Equal("C: 12.4 GB free", FreeSpaceStatus.Tooltip(latest, latest));
    }

    [Fact]
    public void Change_baseline_prefers_the_sample_a_day_back_then_the_oldest()
    {
        var latest = new VolumeSample(48 * Hour, "C:", 1, 1);
        var dayAgo = latest with { Ts = 24 * Hour };
        var oldest = latest with { Ts = 30 * Hour };

        Assert.Same(dayAgo, FreeSpaceStatus.ChangeBaseline(latest, dayAgo, oldest));
        Assert.Same(oldest, FreeSpaceStatus.ChangeBaseline(latest, null, oldest));
        Assert.Null(FreeSpaceStatus.ChangeBaseline(latest, null, latest));
        Assert.Null(FreeSpaceStatus.ChangeBaseline(latest, null, null));
    }

    [Fact]
    public void Describe_combines_colour_label_and_tooltip()
    {
        var view = FreeSpaceStatus.Describe(new VolumeSample(Hour, "C:", Gb(150), Gb(12.4)), null, Defaults);

        Assert.Equal(new FreeSpaceView(FreeSpaceLevel.Amber, "12", "C: 12.4 GB free"), view);
    }
}
