using System.Text.RegularExpressions;
using DiskMon.Core.Security;
using Microsoft.Extensions.Configuration;

namespace DiskMon.Core.Config;

/// <summary>Everything configurable, bound from the <c>DiskMon</c> section of the Collector's appsettings.json.</summary>
public sealed partial class DiskMonOptions
{
    public const string SectionName = "DiskMon";
    public const string DefaultVolume = "C:";

    /// <summary>Holds diskmon.db and logs/. Defaults to %ProgramData%\DiskMon.</summary>
    public string DataDirectory { get; set; } = "";

    /// <summary>Volumes to sample, like "C:". Empty means just C:, the default and priority Volume.</summary>
    public List<string> Volumes { get; set; } = [];

    public IntervalOptions Intervals { get; set; } = new();
    public SwingOptions Swing { get; set; } = new();
    public TopWriterOptions TopWriters { get; set; } = new();
    public List<ProbeDefinition> Probes { get; set; } = [];
    public RetentionOptions Retention { get; set; } = new();
    public TrayOptions Tray { get; set; } = new();

    public string DatabasePath => Path.Combine(DataDirectory, Data.Db.FileName);
    public string LogDirectory => Path.Combine(DataDirectory, "logs");

    /// <summary>Binds, fills defaults, normalises and validates. Throws on invalid config.</summary>
    public static DiskMonOptions Bind(IConfiguration configuration)
    {
        var options = new DiskMonOptions();
        configuration.GetSection(SectionName).Bind(options);
        options.Normalize();
        options.Validate();
        return options;
    }

    // Lists are filled after binding: the binder appends to, rather than replaces, a pre-populated list.
    internal void Normalize()
    {
        if (string.IsNullOrWhiteSpace(DataDirectory))
            DataDirectory = DataFolder.DefaultPath;
        DataDirectory = Environment.ExpandEnvironmentVariables(DataDirectory);

        Volumes = Volumes
            .Select(v => v.Trim().TrimEnd('\\').ToUpperInvariant())
            .Where(v => v.Length > 0)
            .Distinct()
            .ToList();
        if (Volumes.Count == 0)
            Volumes.Add(DefaultVolume);
    }

    internal void Validate()
    {
        var errors = new List<string>();

        foreach (var volume in Volumes.Where(v => !VolumePattern().IsMatch(v)))
            errors.Add($"Volume '{volume}' must be a drive letter like 'C:'.");

        Positive(errors, Intervals.VolumeSampleSeconds, "Intervals:VolumeSampleSeconds");
        Positive(errors, Intervals.ProcessIoSampleSeconds, "Intervals:ProcessIoSampleSeconds");
        Positive(errors, Intervals.ProbeRunMinutes, "Intervals:ProbeRunMinutes");
        Positive(errors, Intervals.ProbeTimeoutSeconds, "Intervals:ProbeTimeoutSeconds");
        Positive(errors, Swing.ThresholdGb, "Swing:ThresholdGb");
        Positive(errors, Swing.WindowMinutes, "Swing:WindowMinutes");
        Positive(errors, Swing.MaxDurationMinutes, "Swing:MaxDurationMinutes");
        NotNegative(errors, Swing.MinItemMb, "Swing:MinItemMb");
        Positive(errors, TopWriters.Count, "TopWriters:Count");
        NotNegative(errors, TopWriters.MinWriteMb, "TopWriters:MinWriteMb");
        Positive(errors, Retention.SampleDays, "Retention:SampleDays");

        if (Tray.RedBelowPercent > Tray.AmberBelowPercent)
            errors.Add("Tray:RedBelowPercent must not exceed Tray:AmberBelowPercent.");
        if (Tray.RedBelowGb > Tray.AmberBelowGb)
            errors.Add("Tray:RedBelowGb must not exceed Tray:AmberBelowGb.");

        foreach (var (probe, i) in Probes.Select((p, i) => (p, i)))
        {
            if (string.IsNullOrWhiteSpace(probe.Name))
                errors.Add($"Probes[{i}] needs a Name.");
            if (string.IsNullOrWhiteSpace(probe.Path))
                errors.Add($"Probes[{i}] ('{probe.Name}') needs a Path.");
            else if (probe.Kind == ProbeKind.Vss && !VolumePattern().IsMatch(probe.Path.ToUpperInvariant()))
                errors.Add($"Probes[{i}] ('{probe.Name}') is a vss Probe; its Path must be a Volume like 'C:'.");
        }

        if (errors.Count > 0)
            throw new InvalidOperationException("Invalid DiskMon configuration:" + Environment.NewLine + string.Join(Environment.NewLine, errors));
    }

    private static void Positive(List<string> errors, double value, string name)
    {
        if (value <= 0) errors.Add($"{name} must be greater than 0.");
    }

    private static void NotNegative(List<string> errors, double value, string name)
    {
        if (value < 0) errors.Add($"{name} must not be negative.");
    }

    [GeneratedRegex("^[A-Z]:$")]
    private static partial Regex VolumePattern();
}

public sealed class IntervalOptions
{
    public int VolumeSampleSeconds { get; set; } = 30;
    public int ProcessIoSampleSeconds { get; set; } = 60;
    public int ProbeRunMinutes { get; set; } = 10;
    public int ProbeTimeoutSeconds { get; set; } = 60;
}

/// <summary>One global Swing threshold for every Volume.</summary>
public sealed class SwingOptions
{
    public double ThresholdGb { get; set; } = 1;
    public int WindowMinutes { get; set; } = 15;
    public int MaxDurationMinutes { get; set; } = 120;

    /// <summary>Probe items smaller than this (absolute delta) are left out of Attribution.</summary>
    public double MinItemMb { get; set; } = 50;

    public long ThresholdBytes => (long)(ThresholdGb * Bytes.GB);
    public long MinItemBytes => (long)(MinItemMb * Bytes.MB);
}

public sealed class TopWriterOptions
{
    public int Count { get; set; } = 10;
    public double MinWriteMb { get; set; } = 1;

    public long MinWriteBytes => (long)(MinWriteMb * Bytes.MB);
}

public sealed class RetentionOptions
{
    /// <summary>Volume, process I/O and Probe samples older than this are pruned. Swings are kept forever.</summary>
    public int SampleDays { get; set; } = 30;
}

/// <summary>Tray icon colour: red below either red limit, amber below either amber limit, otherwise green.</summary>
public sealed class TrayOptions
{
    public double RedBelowPercent { get; set; } = 5;
    public double RedBelowGb { get; set; } = 10;
    public double AmberBelowPercent { get; set; } = 10;
    public double AmberBelowGb { get; set; } = 20;
}
