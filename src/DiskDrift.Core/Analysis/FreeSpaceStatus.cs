using System.Globalization;
using DiskDrift.Core.Config;
using DiskDrift.Core.Data;

namespace DiskDrift.Core.Analysis;

/// <summary>The tray icon's colour for a Volume's free space.</summary>
public enum FreeSpaceLevel
{
    Green,
    Amber,
    Red,
}

/// <summary>What the tray shows for one Volume: icon colour, the number drawn on the icon, and the tooltip.</summary>
public sealed record FreeSpaceView(FreeSpaceLevel Level, string IconLabel, string Tooltip);

/// <summary>
/// Turns Volume Samples into the tray's colour, icon label and tooltip. Pure: the caller reads the samples and
/// supplies the thresholds.
/// </summary>
public static class FreeSpaceStatus
{
    /// <summary>How far back the tooltip's change is measured.</summary>
    public static readonly TimeSpan ChangeWindow = TimeSpan.FromHours(24);

    /// <summary>Windows caps a tray tooltip at 127 characters.</summary>
    public const int MaxTooltipLength = 127;

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    /// <summary>
    /// Red below either red limit, amber below either amber limit, otherwise green. The percentage is ignored when
    /// <paramref name="totalBytes"/> is unknown (0).
    /// </summary>
    public static FreeSpaceLevel Level(long freeBytes, long totalBytes, TrayOptions options)
    {
        var freeGb = (double)freeBytes / Bytes.GB;
        var freePercent = totalBytes > 0 ? 100.0 * freeBytes / totalBytes : double.PositiveInfinity;

        if (freePercent < options.RedBelowPercent || freeGb < options.RedBelowGb)
            return FreeSpaceLevel.Red;
        if (freePercent < options.AmberBelowPercent || freeGb < options.AmberBelowGb)
            return FreeSpaceLevel.Amber;
        return FreeSpaceLevel.Green;
    }

    /// <summary>
    /// The free space below which a Volume of <paramref name="totalBytes"/> turns amber and red: the higher of the
    /// GB limit and the percentage limit, because <see cref="Level"/> warns when either is crossed. With the total
    /// unknown (0) only the GB limits apply.
    /// </summary>
    public static (long AmberBytes, long RedBytes) WarningLevels(long totalBytes, TrayOptions options)
    {
        static long Limit(double gb, double percent, long total) =>
            (long)Math.Max(gb * Bytes.GB, total > 0 ? total * percent / 100 : 0);
        return (Limit(options.AmberBelowGb, options.AmberBelowPercent, totalBytes), Limit(options.RedBelowGb, options.RedBelowPercent, totalBytes));
    }

    /// <summary>
    /// The number drawn on the icon: whole free GB rounded down (never flatters a nearly full disk), or whole TB
    /// with a "T" (at least 1T) once there are 1000 GB or more, so it stays at most three characters for typical disks.
    /// </summary>
    public static string IconLabel(long freeBytes)
    {
        var freeGb = Math.Max(0, freeBytes) / Bytes.GB;
        return freeGb < 1000
            ? freeGb.ToString(Invariant)
            : Math.Max(1, freeGb / 1024).ToString(Invariant) + "T";
    }

    /// <summary>
    /// The sample the tooltip's change is measured from: the newest one at least <see cref="ChangeWindow"/> older
    /// than <paramref name="latest"/>, or, with less history than that, the oldest one there is.
    /// </summary>
    public static VolumeSample? ChangeBaseline(VolumeSample latest, VolumeSample? atOrBeforeWindowStart, VolumeSample? oldest) =>
        atOrBeforeWindowStart ?? (oldest is not null && oldest.Ts < latest.Ts ? oldest : null);

    /// <summary>
    /// For example <c>C: 12.4 GB free (−3.1 GB in 24h)</c>. The change is left out when there is no earlier sample,
    /// and its span is the real distance to the baseline (shorter with little history, longer across Downtime).
    /// </summary>
    public static string Tooltip(VolumeSample latest, VolumeSample? baseline)
    {
        var text = $"{latest.Volume} {GbRoundedDown(latest.FreeBytes)} GB free";
        if (baseline is not null && baseline.Ts < latest.Ts)
        {
            var delta = latest.FreeBytes - baseline.FreeBytes;
            var span = Span(TimeSpan.FromMilliseconds(latest.Ts - baseline.Ts));
            text += Math.Abs(delta) < Bytes.GB / 20
                ? $" (no change in {span})"
                : $" ({(delta < 0 ? "−" : "+")}{Gb(Math.Abs(delta))} GB in {span})";
        }
        return text;
    }

    public static FreeSpaceView Describe(VolumeSample latest, VolumeSample? baseline, TrayOptions options) =>
        new(Level(latest.FreeBytes, latest.TotalBytes, options), IconLabel(latest.FreeBytes), Tooltip(latest, baseline));

    private static string Gb(long bytes) => ((double)bytes / Bytes.GB).ToString("0.0", Invariant);

    // Explorer truncates free space to one decimal rather than rounding it; match it so the numbers agree.
    private static string GbRoundedDown(long bytes) =>
        (Math.Floor(Math.Max(0, bytes) * 10.0 / Bytes.GB) / 10).ToString("0.0", Invariant);

    // 24h is the normal case; samples land every 30 s, so allow a little slack before showing "23h".
    private static string Span(TimeSpan span) => span switch
    {
        { TotalHours: >= 48 } => $"{(int)span.TotalDays}d",
        { TotalHours: >= 23.5 and < 25 } => "24h",
        { TotalHours: >= 1 } => $"{(int)Math.Round(span.TotalHours)}h",
        _ => $"{Math.Max(1, (int)Math.Round(span.TotalMinutes))}m",
    };
}
