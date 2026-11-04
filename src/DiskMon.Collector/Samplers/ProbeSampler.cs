using DiskMon.Core;
using DiskMon.Core.Config;
using DiskMon.Core.Data;
using DiskMon.Core.Probes;

namespace DiskMon.Collector.Samplers;

/// <summary>What one Probe run measured, for the log and for the <c>probe_now</c> answer.</summary>
public sealed record ProbeRunSummary(long Ts, int Probes, int Errors, long TotalBytes, long DurationMs);

/// <summary>
/// Measures every Probe, VSS shadow storage included, at startup and every <c>Intervals:ProbeRunMinutes</c>
/// (default 10), and on demand through <see cref="RunNowAsync"/> (the pipe's <c>probe_now</c>, and Swing open and
/// close later). Only one run happens at a time: asking during a run joins it. A run completes once all its Probe
/// Samples are written. The Probe list is re-expanded and upserted at each run, so a new profile is picked up.
/// </summary>
public sealed class ProbeSampler(
    Db db,
    DiskMonOptions options,
    IShadowStorageReader shadowStorage,
    TimeProvider time,
    ILogger<ProbeSampler> logger) : BackgroundService
{
    private readonly SingleFlight<ProbeRunSummary> _flight = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ProbeExpander _expander = new(ProbeExpander.DefaultUsersRoot);
    private readonly ProbeRunner _runner = new(shadowStorage, TimeSpan.FromSeconds(options.Intervals.ProbeTimeoutSeconds));

    /// <summary>
    /// Starts a run, or joins the one in progress, and completes when its Probe Samples are written.
    /// <paramref name="token"/> only stops this caller waiting; the run itself carries on.
    /// </summary>
    public Task<ProbeRunSummary> RunNowAsync(CancellationToken token = default) =>
        _flight.RunAsync(() => RunOnceAsync(_shutdown.Token)).WaitAsync(token);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromMinutes(options.Intervals.ProbeRunMinutes);
        logger.LogInformation("Measuring {Count} configured Probes every {Interval}", options.Probes.Count, interval);

        using var timer = new PeriodicTimer(interval, time);
        do
        {
            try
            {
                await RunNowAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Probe run failed; retrying next interval");
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await _shutdown.CancelAsync();
        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _shutdown.Dispose();
        base.Dispose();
    }

    private async Task<ProbeRunSummary> RunOnceAsync(CancellationToken token)
    {
        var ts = time.GetUtcNow().ToUnixTimeMilliseconds();
        var started = time.GetTimestamp();

        var probes = _expander.Expand(options.Probes);
        IReadOnlyList<long> ids;
        using (var connection = db.OpenConnection())
            ids = ProbeStore.Upsert(connection, probes);

        var results = await _runner.RunAsync(probes, token);

        using (var connection = db.OpenConnection())
            ProbeStore.InsertSamples(connection, ts, results.Select((r, i) => (ids[i], r.Measurement)).ToList());

        foreach (var failed in results.Where(r => r.Measurement.Error is not null))
            logger.LogWarning("Probe {Name} ({Path}): {Error}", failed.Probe.Name, failed.Probe.Path, failed.Measurement.Error);

        var summary = new ProbeRunSummary(
            ts,
            results.Count,
            results.Count(r => r.Measurement.Error is not null),
            results.Sum(r => r.Measurement.SizeBytes),
            (long)time.GetElapsedTime(started).TotalMilliseconds);
        logger.LogInformation(
            "Probe run: {Probes} Probes, {TotalBytes} bytes, {Errors} with errors, in {DurationMs} ms",
            summary.Probes, summary.TotalBytes, summary.Errors, summary.DurationMs);
        logger.LogInformation("Largest Probes: {Largest}", Largest(results, LargestInLog));
        return summary;
    }

    /// <summary>How many Probes the run's log line names, biggest first. One line per run keeps the log small.</summary>
    private const int LargestInLog = 8;

    private static string Largest(IReadOnlyList<ProbeResult> results, int count) =>
        string.Join(", ", results
            .Where(r => r.Measurement.SizeBytes > 0)
            .OrderByDescending(r => r.Measurement.SizeBytes)
            .Take(count)
            .Select(r => $"{r.Probe.Name} {r.Measurement.SizeBytes / (double)Bytes.GB:0.0} GB"));

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken token)
    {
        try
        {
            return await timer.WaitForNextTickAsync(token);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
