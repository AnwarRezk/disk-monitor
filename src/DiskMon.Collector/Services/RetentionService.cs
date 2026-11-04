using DiskMon.Core.Config;
using DiskMon.Core.Data;

namespace DiskMon.Collector.Services;

/// <summary>
/// At startup and then daily, prunes Volume, process I/O and Probe Samples older than <c>Retention:SampleDays</c>
/// (default 30) and process rows no longer referenced. Swings and Swing items are kept forever.
/// </summary>
public sealed class RetentionService(Db db, DiskMonOptions options, ILogger<RetentionService> logger) : BackgroundService
{
    private static readonly TimeSpan Period = TimeSpan.FromDays(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Period);
        do
        {
            try
            {
                PruneOnce();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Retention run failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private void PruneOnce()
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-options.Retention.SampleDays);
        using var connection = db.OpenConnection();
        var removed = Retention.Prune(connection, cutoff.ToUnixTimeMilliseconds());
        logger.LogInformation(
            "Retention: removed {VolumeSamples} Volume, {ProcessIoSamples} process I/O and {ProbeSamples} Probe Samples older than {Cutoff:u}, and {Processes} unreferenced processes",
            removed.VolumeSamples, removed.ProcessIoSamples, removed.ProbeSamples, cutoff, removed.Processes);
    }
}
