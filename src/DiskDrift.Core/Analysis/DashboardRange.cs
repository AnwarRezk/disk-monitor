namespace DiskDrift.Core.Analysis;

/// <summary>The time ranges the dashboard offers: the last 6 hours, 24 hours, 7 days or 30 days.</summary>
public enum DashboardRange
{
    Hours6,
    Hours24,
    Days7,
    Days30,
}

public static class DashboardRanges
{
    public static readonly IReadOnlyList<DashboardRange> All =
        [DashboardRange.Hours6, DashboardRange.Hours24, DashboardRange.Days7, DashboardRange.Days30];

    public static TimeSpan Duration(this DashboardRange range) => range switch
    {
        DashboardRange.Hours6 => TimeSpan.FromHours(6),
        DashboardRange.Hours24 => TimeSpan.FromHours(24),
        DashboardRange.Days7 => TimeSpan.FromDays(7),
        DashboardRange.Days30 => TimeSpan.FromDays(30),
        _ => throw new ArgumentOutOfRangeException(nameof(range), range, null),
    };

    /// <summary>The button label: <c>6h</c>, <c>24h</c>, <c>7d</c> or <c>30d</c>.</summary>
    public static string Label(this DashboardRange range) => range switch
    {
        DashboardRange.Hours6 => "6h",
        DashboardRange.Hours24 => "24h",
        DashboardRange.Days7 => "7d",
        DashboardRange.Days30 => "30d",
        _ => throw new ArgumentOutOfRangeException(nameof(range), range, null),
    };

    /// <summary>For sentences: "the last 6 hours", "the last 7 days".</summary>
    public static string Description(this DashboardRange range) => range switch
    {
        DashboardRange.Hours6 => "the last 6 hours",
        DashboardRange.Hours24 => "the last 24 hours",
        DashboardRange.Days7 => "the last 7 days",
        DashboardRange.Days30 => "the last 30 days",
        _ => throw new ArgumentOutOfRangeException(nameof(range), range, null),
    };

    /// <summary>The range's start (UTC Unix ms) when it ends at <paramref name="endTs"/>.</summary>
    public static long StartTs(this DashboardRange range, long endTs) => endTs - (long)range.Duration().TotalMilliseconds;
}
