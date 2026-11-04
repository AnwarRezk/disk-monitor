using DiskMon.Core.Analysis;
using DiskMon.Core.Config;
using DiskMon.Core.Data;
using DiskMon.Core.Native;

namespace DiskMon.Collector.Samplers;

/// <summary>
/// Every <c>Intervals:ProcessIoSampleSeconds</c> (default 60 s), stores the interval's Top Writers: the top
/// <c>TopWriters:Count</c> processes by bytes written, each at least <c>TopWriters:MinWriteMb</c>.
/// The counters include all I/O, not just disk writes, so they are a ranking signal, not bytes on disk.
/// </summary>
public sealed class ProcessIoSampler(Db db, DiskMonOptions options, ILogger<ProcessIoSampler> logger) : BackgroundService
{
    private readonly TopWriterTracker _tracker = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The first run only records baselines; Top Writers start from the second.
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Intervals.ProcessIoSampleSeconds));
        do
        {
            try
            {
                SampleOnce();
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Process I/O sample failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    private void SampleOnce()
    {
        var snapshots = ProcessIoReader.ReadAll(out var skipped);
        var ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (skipped > 0)
            logger.LogDebug("Skipped {Skipped} processes (access denied, protected or exited)", skipped);

        var writers = _tracker.Next(ts, snapshots, options.TopWriters.Count, options.TopWriters.MinWriteBytes);
        using var connection = db.OpenConnection();
        ProcessIoStore.Insert(connection, ts, writers);
        logger.LogDebug("Read {Processes} processes; stored {Writers} Top Writers", snapshots.Count, writers.Count);
    }
}
