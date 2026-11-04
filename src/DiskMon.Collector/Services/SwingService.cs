using DiskMon.Collector.Samplers;
using DiskMon.Core;
using DiskMon.Core.Analysis;
using DiskMon.Core.Config;
using DiskMon.Core.Data;
using DiskMon.Core.Sampling;

namespace DiskMon.Collector.Services;

/// <summary>
/// Detects Swings (PLAN §5.4) from the Volume Samples the <see cref="VolumeSampler"/> publishes, and writes them to
/// the <c>swings</c> table through a <see cref="SwingRecorder"/>. A Swing opening, closing or recorded across Downtime
/// starts a Probe run (VSS included). Closing Swings stay Closing until Attribution (a later ticket) closes them.
/// </summary>
public sealed class SwingService(
    DiskMonOptions options,
    Db db,
    VolumeSampleChannel channel,
    ProbeSampler probes,
    ILogger<SwingService> logger) : BackgroundService
{
    private readonly SwingRecorder _recorder = new(new SwingStore(db), new VolumeSampleStore(db), SwingSettings.From(options));

    /// <summary>
    /// Restores the detector before any sampler starts. Hosted services start in registration order, so registering
    /// this before the <see cref="VolumeSampler"/> means the restart gap is measured from the last sample of the
    /// previous run, not from one this run already wrote.
    /// </summary>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            var restored = _recorder.Restore(options.Volumes);
            foreach (var action in restored)
                Log(action);
            if (restored.Count > 0)
                RunProbes();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not restore Swing detection from the database; starting fresh");
        }

        var settings = SwingSettings.From(options);
        logger.LogInformation(
            "Detecting Swings of {ThresholdGb} GB within {WindowMinutes} min (at most {MaxMinutes} min; Downtime after {GapSeconds} s without samples)",
            options.Swing.ThresholdGb, options.Swing.WindowMinutes, options.Swing.MaxDurationMinutes, settings.MaxGapMs / 1000);
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var sample in channel.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    var actions = _recorder.Add(sample);
                    foreach (var action in actions)
                        Log(action);
                    if (actions.Count > 0)
                        RunProbes();
                }
                catch (Exception ex)
                {
                    // A failed write (DB busy beyond the timeout) loses that change, not the detector.
                    logger.LogError(ex, "Swing detection failed for the {Volume} sample at {Ts}", sample.Volume, sample.Ts);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Starts (or joins) a Probe run without waiting for it: detection must keep up with the samples.</summary>
    private void RunProbes() =>
        _ = probes.RunNowAsync().ContinueWith(
            t => logger.LogError(t.Exception, "Probe run for a Swing failed"),
            TaskContinuationOptions.OnlyOnFaulted);

    private void Log(SwingAction action)
    {
        switch (action)
        {
            case SwingOpened o:
                logger.LogInformation("{Kind} Swing opened on {Volume}, starting {Start:u}", o.Kind, o.Volume, Time(o.TsStart));
                break;
            case SwingClosed c:
                logger.LogInformation(
                    "{Kind} Swing on {Volume} ({Start:u} to {End:u}) closing ({Reason}): used space {UsedGb:+0.00;-0.00} GB",
                    c.Kind, c.Volume, Time(c.TsStart), Time(c.TsEnd), c.Reason, c.UsedDeltaBytes / (double)Bytes.GB);
                break;
            case DowntimeSwing d:
                logger.LogInformation(
                    "{Kind} Swing across Downtime on {Volume} ({Start:u} to {End:u}): used space {UsedGb:+0.00;-0.00} GB",
                    d.Kind, d.Volume, Time(d.TsStart), Time(d.TsEnd), d.UsedDeltaBytes / (double)Bytes.GB);
                break;
        }
    }

    private static DateTimeOffset Time(long ts) => DateTimeOffset.FromUnixTimeMilliseconds(ts);
}
