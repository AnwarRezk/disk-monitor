using DiskDrift.Collector.Samplers;
using DiskDrift.Core;
using DiskDrift.Core.Analysis;
using DiskDrift.Core.Config;
using DiskDrift.Core.Data;
using DiskDrift.Core.Sampling;

namespace DiskDrift.Collector.Services;

/// <summary>
/// Detects Swings (PLAN §5.4) from the Volume Samples the <see cref="VolumeSampler"/> publishes, and writes them to
/// the <c>swings</c> table through a <see cref="SwingRecorder"/>. A Swing opening, closing or recorded across Downtime
/// starts a Probe run (VSS included). A Closing Swing is attributed and moved to Closed by the
/// <see cref="SwingAttributor"/> once a Probe run that started at or after its end has stored its samples.
/// </summary>
public sealed class SwingService(
    DiskDriftOptions options,
    Db db,
    VolumeSampleChannel channel,
    ProbeSampler probes,
    ILogger<SwingService> logger) : BackgroundService
{
    private readonly SwingRecorder _recorder = new(new SwingStore(db), new VolumeSampleStore(db), SwingSettings.From(options));
    private readonly SwingAttributor _attributor = new(db);

    // Samples arrive on the channel reader; Probe runs end on the thread pool. One at a time touches the Swings.
    private readonly Lock _gate = new();

    /// <summary>
    /// Restores the detector before any sampler starts. Hosted services start in registration order, so registering
    /// this before the <see cref="VolumeSampler"/> means the restart gap is measured from the last sample of the
    /// previous run, not from one this run already wrote.
    /// </summary>
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            foreach (var action in _recorder.Restore(options.Volumes))
                Log(action);

            // Swings left Closing (or just closed by the restart) can't get a post-close Probe run any more.
            foreach (var attributed in _attributor.AttributeAfterRestart())
                Log(attributed);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not restore Swing detection from the database; starting fresh");
        }

        probes.RunCompleted += OnProbeRunCompleted;

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
                    IReadOnlyList<SwingAction> actions;
                    IReadOnlyList<AttributedSwing> attributed = [];
                    lock (_gate)
                    {
                        actions = _recorder.Add(sample);
                        // A Swing closed by Downtime ended before the gap: its after-side would include the gap.
                        if (actions.OfType<SwingClosed>().FirstOrDefault(c => c.Reason == SwingCloseReason.Downtime) is { } cut)
                            attributed = _attributor.AttributeEndedBy(cut.TsEnd);
                    }
                    foreach (var a in attributed)
                        Log(a);
                    foreach (var action in actions)
                        Log(action);
                    if (actions.Count > 0)
                        RunProbes(actions.Select(EndOf).Max());
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

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        probes.RunCompleted -= OnProbeRunCompleted;
        return base.StopAsync(cancellationToken);
    }

    /// <summary>
    /// Starts (or joins) a Probe run without waiting for it: detection must keep up with the samples. A joined run
    /// that started before <paramref name="notBefore"/> can't be a Swing's after-side, so another one follows it.
    /// </summary>
    private void RunProbes(long notBefore) =>
        _ = RunProbesAsync(notBefore);

    private async Task RunProbesAsync(long notBefore)
    {
        try
        {
            var run = await probes.RunNowAsync();
            if (run.Ts < notBefore)
                await probes.RunNowAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Probe run for a Swing failed");
        }
    }

    /// <summary>The newest time a Swing action needs Probe Samples for: its end once closed, its start while open.</summary>
    private static long EndOf(SwingAction action) => action switch
    {
        SwingClosed c => c.TsEnd,
        DowntimeSwing d => d.TsEnd,
        SwingOpened o => o.TsStart,
        _ => 0,
    };

    private void OnProbeRunCompleted(ProbeRunSummary run)
    {
        try
        {
            IReadOnlyList<AttributedSwing> attributed;
            lock (_gate)
                attributed = _attributor.AttributeReady();
            foreach (var a in attributed)
                Log(a);
        }
        catch (Exception ex)
        {
            // The Swing stays Closing; the next Probe run tries again.
            logger.LogError(ex, "Attributing Swings after the Probe run at {Ts} failed", run.Ts);
        }
    }

    private void Log(AttributedSwing a)
    {
        var top = a.Result.Items.Where(i => i.Source == Attribution.ProbeSource).Take(3)
            .Select(i => $"{i.Label} {i.DeltaBytes / (double)Bytes.GB:+0.00;-0.00} GB");
        logger.LogInformation(
            "Swing {Id} on {Volume} attributed: {AttributedGb:+0.00;-0.00} GB to Probes, {UnaccountedGb:+0.00;-0.00} GB Unaccounted ({Top})",
            a.Swing.Id, a.Swing.Volume, a.Result.AttributedBytes / (double)Bytes.GB, a.Result.UnaccountedBytes / (double)Bytes.GB,
            string.Join(", ", top) is { Length: > 0 } t ? t : "no Probe moved 50 MB");
    }

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
