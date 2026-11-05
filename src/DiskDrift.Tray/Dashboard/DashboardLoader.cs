using System.Diagnostics;
using DiskDrift.Core.Analysis;
using DiskDrift.Core.Config;
using DiskDrift.Core.Data;

namespace DiskDrift.Tray.Dashboard;

/// <summary>Everything the dashboard shows for one Volume and range, read in one go.</summary>
internal sealed record DashboardSnapshot(
    IReadOnlyList<string> Volumes,
    string Volume,
    DashboardRange Range,
    long FromTs,
    long ToTs,
    IReadOnlyList<IReadOnlyList<ChartPoint>> Runs,
    RangeSummary? Summary,
    long? LatestTs,
    IReadOnlyList<SwingRow> Swings,
    IReadOnlyList<WriterTotal> Writers,
    IReadOnlyList<ProbeSize> Probes,
    long? LatestProbeTs,
    FullForecast? Forecast,
    long ForecastFromTs,
    ReadOnlyOpenMode? Mode,
    TimeSpan ReadTime);

/// <summary>Reads a <see cref="DashboardSnapshot"/> from the database, read-only (immutable when the Collector is stopped).</summary>
internal sealed class DashboardLoader(ReadOnlyDb db, DiskDriftOptions options)
{
    /// <summary>More points than a chart a few thousand pixels wide can show; thinning keeps each bucket's low and high.</summary>
    private const int MaxChartPoints = 2000;
    private const int TopWriterCount = 10;

    private readonly VolumeSampleStore _samples = new(db);

    // Downtime is a gap longer than two Volume Sample intervals (GLOSSARY): the chart breaks its line there.
    private long MaxGapMs => 2L * options.Intervals.VolumeSampleSeconds * 1000;
    private readonly SwingStore _swings = new(db);

    /// <summary>Config Volumes first (C: leading), then any other Volume the database has samples for.</summary>
    public IReadOnlyList<string> Volumes()
    {
        var known = _samples.Volumes();
        return options.Volumes
            .Concat(known)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(v => v.Equals(DiskDriftOptions.DefaultVolume, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public string DatabasePath => db.Path;

    public TrayOptions TrayOptions => options.Tray;

    /// <summary>What attribution found for one Swing (empty until attribution has run).</summary>
    public IReadOnlyList<SwingItem> SwingItems(long swingId) => _swings.Items(swingId);

    public DashboardSnapshot Load(string volume, DashboardRange range, long nowMs)
    {
        var watch = Stopwatch.StartNew();
        var from = range.StartTs(nowMs);
        var volumes = Volumes();
        var samples = _samples.Range(volume, from, nowMs);
        IReadOnlyList<WriterTotal> writers;
        IReadOnlyList<ProbeSize> probes;
        long? latestProbe;
        using (var connection = db.OpenConnection())
        {
            writers = ProcessIoStore.TopWriters(connection, from, nowMs, TopWriterCount);
            probes = ProbeReads.Sizes(connection, volume, from, nowMs);
            latestProbe = ProbeReads.LatestRunTs(connection);
        }
        var swings = _swings.Overlapping(volume, from, nowMs);

        // The forecast always looks at the last 7 days, whatever range is on screen.
        var trendFrom = nowMs - (long)FullForecast.Window.TotalMilliseconds;
        var trendSamples = from <= trendFrom ? samples.Where(x => x.Ts >= trendFrom).ToList() : _samples.Range(volume, trendFrom, nowMs);
        var forecast = FullForecast.Estimate(trendSamples);
        var snapshot = new DashboardSnapshot(
            volumes, volume, range, from, nowMs,
            ChartSeries.Segments(samples, MaxChartPoints, MaxGapMs),
            RangeSummary.From(samples),
            samples.Count > 0 ? samples[^1].Ts : _samples.Latest(volume)?.Ts,
            swings, writers, probes, latestProbe, forecast, trendSamples.Count > 0 ? trendSamples[0].Ts : trendFrom,
            db.LastMode, watch.Elapsed);
        return snapshot;
    }
}
