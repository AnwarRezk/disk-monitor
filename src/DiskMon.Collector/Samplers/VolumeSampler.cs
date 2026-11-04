using DiskMon.Collector.Services;
using DiskMon.Core.Config;
using DiskMon.Core.Data;
using DiskMon.Core.Sampling;

namespace DiskMon.Collector.Samplers;

/// <summary>
/// Every <c>Intervals:VolumeSampleSeconds</c> (30 s), records one Volume Sample per configured Volume and
/// publishes it to the <see cref="VolumeSampleChannel"/> for Swing detection.
/// </summary>
public sealed class VolumeSampler(
    DiskMonOptions options,
    VolumeSampleStore store,
    VolumeSampleChannel channel,
    CollectorHeartbeat heartbeat,
    TimeProvider time,
    ILogger<VolumeSampler> logger) : BackgroundService
{
    private readonly VolumeReader _reader = new(options.Volumes, new SystemDriveSource());

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(options.Intervals.VolumeSampleSeconds);
        logger.LogInformation("Sampling Volumes {Volumes} every {Interval}", options.Volumes, interval);

        using var timer = new PeriodicTimer(interval, time);
        do
        {
            try
            {
                SampleOnce();
            }
            catch (Exception ex)
            {
                // One bad tick (DB busy beyond the timeout, a drive vanishing mid-read) must not stop the timeline.
                logger.LogError(ex, "Volume sampling failed; retrying next interval");
            }
        }
        while (await WaitAsync(timer, stoppingToken));
    }

    private void SampleOnce()
    {
        var ts = time.GetUtcNow().ToUnixTimeMilliseconds();
        var result = _reader.Read(ts);

        foreach (var skip in result.NewlySkipped)
            logger.LogWarning("Skipping Volume {Volume}: {Reason} {Detail}", skip.Volume, skip.Reason, skip.Detail);
        foreach (var volume in result.Recovered)
            logger.LogInformation("Volume {Volume} is available again; sampling resumed", volume);

        store.Insert(result.Samples);
        if (result.Samples.Count > 0)
            heartbeat.SampleStored(ts);
        foreach (var sample in result.Samples)
        {
            channel.Writer.TryWrite(sample);
            logger.LogDebug("{Volume} free {FreeBytes} of {TotalBytes} bytes", sample.Volume, sample.FreeBytes, sample.TotalBytes);
        }
    }

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
