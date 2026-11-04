using DiskMon.Core.Config;
using Microsoft.Extensions.Configuration;

namespace DiskMon.Core.Tests;

public sealed class OptionsTests
{
    private static DiskMonOptions BindJson(string json)
    {
        var config = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json)))
            .Build();
        return DiskMonOptions.Bind(config);
    }

    internal static DiskMonOptions BindShippedAppSettings()
    {
        // The Collector's appsettings.json is linked into this project's output.
        var config = new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false)
            .Build();
        return DiskMonOptions.Bind(config);
    }

    [Fact]
    public void Empty_config_defaults_to_C_and_the_planned_values()
    {
        var options = BindJson("{}");

        Assert.Equal(["C:"], options.Volumes);
        Assert.Equal(30, options.Intervals.VolumeSampleSeconds);
        Assert.Equal(60, options.Intervals.ProcessIoSampleSeconds);
        Assert.Equal(10, options.Intervals.ProbeRunMinutes);
        Assert.Equal(1L << 30, options.Swing.ThresholdBytes);
        Assert.Equal(15, options.Swing.WindowMinutes);
        Assert.Equal(50L << 20, options.Swing.MinItemBytes);
        Assert.Equal(10, options.TopWriters.Count);
        Assert.Equal(1L << 20, options.TopWriters.MinWriteBytes);
        Assert.Equal(30, options.Retention.SampleDays);
        Assert.Equal(5, options.Tray.RedBelowPercent);
        Assert.Equal(20, options.Tray.AmberBelowGb);
        Assert.False(string.IsNullOrWhiteSpace(options.DataDirectory));
        Assert.EndsWith("diskmon.db", options.DatabasePath);
    }

    [Fact]
    public void Configured_volumes_replace_the_default_and_are_normalised()
    {
        var options = BindJson("""{ "DiskMon": { "Volumes": [ "c:", "D:\\", "d:" ] } }""");

        Assert.Equal(["C:", "D:"], options.Volumes);
    }

    [Fact]
    public void Every_section_binds_from_config()
    {
        var options = BindJson("""
        {
          "DiskMon": {
            "DataDirectory": "X:\\Data",
            "Intervals": { "VolumeSampleSeconds": 5, "ProcessIoSampleSeconds": 6, "ProbeRunMinutes": 7, "ProbeTimeoutSeconds": 8 },
            "Swing": { "ThresholdGb": 2, "WindowMinutes": 20, "MaxDurationMinutes": 60, "MinItemMb": 10 },
            "TopWriters": { "Count": 3, "MinWriteMb": 4 },
            "Retention": { "SampleDays": 9 },
            "Tray": { "RedBelowPercent": 1, "RedBelowGb": 2, "AmberBelowPercent": 3, "AmberBelowGb": 4 },
            "Probes": [ { "Name": "Temp", "Kind": "dir", "Path": "{user}\\AppData\\Local\\Temp" } ]
          }
        }
        """);

        Assert.Equal("X:\\Data", options.DataDirectory);
        Assert.Equal((5, 6, 7, 8), (options.Intervals.VolumeSampleSeconds, options.Intervals.ProcessIoSampleSeconds,
            options.Intervals.ProbeRunMinutes, options.Intervals.ProbeTimeoutSeconds));
        Assert.Equal(2L << 30, options.Swing.ThresholdBytes);
        Assert.Equal((20, 60), (options.Swing.WindowMinutes, options.Swing.MaxDurationMinutes));
        Assert.Equal(10L << 20, options.Swing.MinItemBytes);
        Assert.Equal((3, 4L << 20), (options.TopWriters.Count, options.TopWriters.MinWriteBytes));
        Assert.Equal(9, options.Retention.SampleDays);
        Assert.Equal((1d, 2d, 3d, 4d), (options.Tray.RedBelowPercent, options.Tray.RedBelowGb,
            options.Tray.AmberBelowPercent, options.Tray.AmberBelowGb));
        var probe = Assert.Single(options.Probes);
        Assert.Equal(ProbeKind.Dir, probe.Kind);
        Assert.True(probe.IsPerUser);
    }

    [Theory]
    [InlineData("""{ "DiskMon": { "Volumes": [ "C" ] } }""", "Volume 'C'")]
    [InlineData("""{ "DiskMon": { "Swing": { "ThresholdGb": 0 } } }""", "Swing:ThresholdGb")]
    [InlineData("""{ "DiskMon": { "Intervals": { "VolumeSampleSeconds": -1 } } }""", "Intervals:VolumeSampleSeconds")]
    [InlineData("""{ "DiskMon": { "Tray": { "RedBelowGb": 30 } } }""", "Tray:RedBelowGb")]
    [InlineData("""{ "DiskMon": { "Probes": [ { "Name": "VSS", "Kind": "Vss", "Path": "C:\\Windows" } ] } }""", "vss Probe")]
    public void Invalid_config_is_rejected_with_a_clear_message(string json, string expected)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => BindJson(json));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Shipped_appsettings_bind_with_C_and_the_default_probes()
    {
        var options = BindShippedAppSettings();

        Assert.Equal(["C:"], options.Volumes);
        Assert.Contains(options.Probes, p => p.Kind == ProbeKind.Vss && p.Path == "C:");
        Assert.Contains(options.Probes, p => p.Name == "User Temp" && p.IsPerUser);
        Assert.Contains(options.Probes, p => p.Kind == ProbeKind.Glob);
        Assert.Contains(options.Probes, p => p.Kind == ProbeKind.File && p.Path == @"C:\pagefile.sys");
        Assert.DoesNotContain(options.Probes, p => p.Path.Contains("WinSxS", StringComparison.OrdinalIgnoreCase));
    }
}
