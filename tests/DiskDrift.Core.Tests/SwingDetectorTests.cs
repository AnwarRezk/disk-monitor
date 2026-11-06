using DiskDrift.Core.Analysis;
using DiskDrift.Core.Data;

namespace DiskDrift.Core.Tests;

/// <summary>The Swing engine's primary seam: synthetic Volume Sample sequences in, actions out (PLAN §5.4, §7).</summary>
public sealed class SwingDetectorTests
{
    private const long Gb = Bytes.GB;
    private const long Min = 60_000;
    private const long T0 = 1_800_000_000_000;

    // 1 GB over 15 min, 2 h maximum, a sample every 30 s: the defaults.
    private static readonly SwingSettings Settings = new(Gb, 15 * Min, 120 * Min, 30_000);

    private static VolumeSample At(double minute, double freeGb) =>
        new(T0 + (long)(minute * Min), "C:", 500 * Gb, (long)(freeGb * Gb));

    private static long Ts(double minute) => T0 + (long)(minute * Min);

    /// <summary>A sample every 30 s from <paramref name="from"/> to <paramref name="to"/> minutes, free space linear between the two values.</summary>
    private static IEnumerable<VolumeSample> Ramp(double from, double to, double fromGb, double toGb)
    {
        for (var m = from; m <= to + 1e-9; m += 0.5)
            yield return At(m, fromGb + ((toGb - fromGb) * (to == from ? 0 : (m - from) / (to - from))));
    }

    private static (VolumeSwingState State, IReadOnlyList<SwingAction> Actions) Run(IEnumerable<VolumeSample> samples, VolumeSwingState? state = null) =>
        SwingDetector.Detect(state ?? VolumeSwingState.Empty("C:"), samples, Settings);

    [Fact]
    public void Steady_free_space_opens_nothing()
    {
        var (state, actions) = Run(Ramp(0, 60, 100, 100));

        Assert.Empty(actions);
        Assert.Null(state.Open);
    }

    [Fact]
    public void A_2_GB_drop_opens_a_Growth_Swing_starting_at_the_last_sample_before_the_drop()
    {
        var (state, actions) = Run([.. Ramp(0, 10, 100, 100), At(10.5, 98)]);

        var opened = Assert.IsType<SwingOpened>(Assert.Single(actions));
        Assert.Equal(new SwingOpened("C:", SwingKind.Growth, Ts(10)), opened);
        Assert.Equal(SwingKind.Growth, state.Open!.Kind);
    }

    [Fact]
    public void A_rise_opens_a_Release_Swing()
    {
        var (_, actions) = Run([.. Ramp(0, 5, 100, 100), At(5.5, 101.5)]);

        Assert.Equal([new SwingOpened("C:", SwingKind.Release, Ts(5))], actions);
    }

    [Fact]
    public void A_move_spread_over_the_window_opens_once_it_reaches_the_threshold()
    {
        // 1.2 GB over 12 minutes: 0.1 GB a minute crosses 1 GB at minute 10, compared with the minute-0 sample.
        var (_, actions) = Run(Ramp(0, 12, 100, 98.8));

        var opened = Assert.IsType<SwingOpened>(Assert.Single(actions));
        Assert.Equal(Ts(0), opened.TsStart);
    }

    [Fact]
    public void Slow_growth_below_the_threshold_produces_no_Swing()
    {
        // 3 GB over 3 hours is 0.25 GB per 15 min: the Volume fills, but never fast enough to be a Swing.
        var (_, actions) = Run(Ramp(0, 180, 100, 97));

        Assert.Empty(actions);
    }

    [Fact]
    public void A_Swing_closes_once_the_window_has_settled_for_two_samples_and_ends_there()
    {
        // Drop at 10.5, then flat. The pre-drop sample leaves the 15-min window after minute 25: the windows ending
        // at 25.5 and 26 are both flat, so the Swing closes at 26.
        var (state, actions) = Run([.. Ramp(0, 10, 100, 100), .. Ramp(10.5, 40, 98, 98)]);

        Assert.Equal(
            [
                new SwingOpened("C:", SwingKind.Growth, Ts(10)),
                new SwingClosed("C:", SwingKind.Growth, Ts(10), Ts(26), 2 * Gb, SwingCloseReason.Settled),
            ],
            actions);
        Assert.Null(state.Open);
    }

    [Fact]
    public void One_unsettled_sample_resets_the_settle_count()
    {
        var samples = new List<VolumeSample>([.. Ramp(0, 10, 100, 100), .. Ramp(10.5, 25.5, 98, 98)]);
        samples.Add(At(26, 97.4)); // A 0.6 GB wobble: the window Δ is back above half the threshold.
        samples.AddRange(Ramp(26.5, 50, 97.4, 97.4));

        var (_, actions) = Run(samples);

        var closed = Assert.IsType<SwingClosed>(actions[^1]);
        Assert.Equal(Ts(41.5), closed.TsEnd); // The last 98 GB sample left the window after 40.5; settled at 41 and 41.5.
        Assert.Equal(100 * Gb - (long)(97.4 * Gb), closed.UsedDeltaBytes);
    }

    [Fact]
    public void A_Swing_that_keeps_moving_closes_at_the_maximum_duration()
    {
        // 1 GB every 10 minutes for 3 hours never settles.
        var (_, actions) = Run(Ramp(0, 180, 200, 182));

        var closed = actions.OfType<SwingClosed>().First();
        Assert.Equal(SwingCloseReason.MaxDuration, closed.Reason);
        Assert.Equal(120 * Min, closed.TsEnd - closed.TsStart);

        // The next Swing starts no earlier than the previous one ended: Swings never overlap.
        var reopened = actions.SkipWhile(a => a != closed).OfType<SwingOpened>().First();
        Assert.True(reopened.TsStart >= closed.TsEnd);
    }

    [Fact]
    public void Flapping_near_the_threshold_opens_one_Swing_not_many()
    {
        // Free space bounces between 99.05 and 98.95 GB after a 1 GB drop: just either side of the threshold.
        var samples = new List<VolumeSample>(Ramp(0, 10, 100, 100));
        for (var m = 10.5; m <= 24; m += 0.5)
            samples.Add(At(m, (int)(m * 2) % 2 == 0 ? 98.95 : 99.05));

        var (state, actions) = Run(samples);

        Assert.Equal([new SwingOpened("C:", SwingKind.Growth, Ts(10))], actions);
        Assert.NotNull(state.Open);
    }

    [Fact]
    public void A_move_back_by_the_threshold_closes_at_the_peak_and_opens_the_opposite_kind()
    {
        // Create a 2 GB file, then delete it 6 minutes later: two Swings, not one netted to zero.
        var (state, actions) = Run([.. Ramp(0, 10, 100, 100), .. Ramp(10.5, 16, 98, 98), .. Ramp(16.5, 20, 100, 100)]);

        Assert.Equal(
            [
                new SwingOpened("C:", SwingKind.Growth, Ts(10)),
                new SwingClosed("C:", SwingKind.Growth, Ts(10), Ts(16), 2 * Gb, SwingCloseReason.Flip),
                new SwingOpened("C:", SwingKind.Release, Ts(16)),
            ],
            actions);
        Assert.Equal(SwingKind.Release, state.Open!.Kind);
        Assert.Equal(98 * Gb, state.Open.FreeAtStart);
    }

    [Fact]
    public void The_peak_is_the_most_extreme_sample_even_after_a_partial_retreat()
    {
        // Down 3 GB, back 0.5 GB (no flip), then back 2 GB more: the flip is measured from, and closes at, the 97 GB peak.
        var (_, actions) = Run([.. Ramp(0, 5, 100, 100), At(5.5, 97), At(6, 97.5), At(6.5, 98.5)]);

        Assert.Equal(
            [
                new SwingOpened("C:", SwingKind.Growth, Ts(5)),
                new SwingClosed("C:", SwingKind.Growth, Ts(5), Ts(5.5), 3 * Gb, SwingCloseReason.Flip),
                new SwingOpened("C:", SwingKind.Release, Ts(5.5)),
            ],
            actions);
    }

    [Fact]
    public void The_Swing_after_a_flip_closes_normally_with_its_own_delta()
    {
        var (_, actions) = Run([.. Ramp(0, 10, 100, 100), .. Ramp(10.5, 16, 98, 98), .. Ramp(16.5, 60, 100, 100)]);

        var last = Assert.IsType<SwingClosed>(actions[^1]);
        Assert.Equal(SwingKind.Release, last.Kind);
        Assert.Equal(Ts(16), last.TsStart);
        Assert.Equal(-2 * Gb, last.UsedDeltaBytes);
        Assert.Equal(SwingCloseReason.Settled, last.Reason);
    }

    [Fact]
    public void Downtime_with_a_threshold_move_records_a_Swing_across_it()
    {
        // Collector stopped from minute 10 to 40; a 2 GB file was created meanwhile.
        var (state, actions) = Run([.. Ramp(0, 10, 100, 100), .. Ramp(40, 60, 98, 98)]);

        Assert.Equal([new DowntimeSwing("C:", SwingKind.Growth, Ts(10), Ts(40), 2 * Gb)], actions);
        Assert.Null(state.Open);
    }

    [Fact]
    public void Downtime_without_a_threshold_move_records_nothing_and_resets_the_window()
    {
        // 0.8 GB across the gap, then another 0.5 GB just after it: 1.3 GB from before the gap, but the ring buffer
        // restarted at the gap, so no Swing.
        var (_, actions) = Run([.. Ramp(0, 10, 100, 100), .. Ramp(40, 45, 99.2, 99.2), .. Ramp(45.5, 55, 98.7, 98.7)]);

        Assert.Empty(actions);
    }

    [Fact]
    public void A_gap_of_exactly_two_intervals_is_not_Downtime()
    {
        var (_, actions) = Run([.. Ramp(0, 10, 100, 100), At(11, 98)]);

        Assert.Equal([new SwingOpened("C:", SwingKind.Growth, Ts(10))], actions);
    }

    [Fact]
    public void Downtime_closes_an_Open_Swing_at_the_last_sample_before_the_gap()
    {
        var (_, actions) = Run([.. Ramp(0, 10, 100, 100), .. Ramp(10.5, 12, 98, 97.5), .. Ramp(30, 35, 97.5, 97.5)]);

        Assert.Equal(
            [
                new SwingOpened("C:", SwingKind.Growth, Ts(10)),
                new SwingClosed("C:", SwingKind.Growth, Ts(10), Ts(12), (long)(2.5 * Gb), SwingCloseReason.Downtime),
            ],
            actions);
    }

    [Fact]
    public void Restart_with_an_Open_Swing_closes_it_at_the_last_sample_before_the_restart()
    {
        var recent = Ramp(0, 12, 98, 97).ToList();
        var open = new OpenSwing(SwingKind.Growth, Ts(-1), 100 * Gb, Ts(-1), 100 * Gb);

        var (state, actions) = SwingDetector.Restore("C:", recent, open, Settings);

        Assert.Equal([new SwingClosed("C:", SwingKind.Growth, Ts(-1), Ts(12), 3 * Gb, SwingCloseReason.Restart)], actions);
        Assert.Null(state.Open);
        Assert.Equal(Ts(12), state.Last!.Ts);
    }

    [Fact]
    public void After_a_restart_the_gap_goes_through_the_Downtime_rule()
    {
        var (restored, _) = SwingDetector.Restore("C:", Ramp(0, 10, 100, 100).ToList(), null, Settings);

        var (_, actions) = Run([At(20, 97)], restored);

        Assert.Equal([new DowntimeSwing("C:", SwingKind.Growth, Ts(10), Ts(20), 3 * Gb)], actions);
    }

    [Fact]
    public void After_a_quick_restart_the_restored_window_still_detects_the_move()
    {
        var (restored, _) = SwingDetector.Restore("C:", Ramp(0, 10, 100, 100).ToList(), null, Settings);

        var (_, actions) = Run([At(10.5, 99.5), At(11, 98.5)], restored);

        Assert.Equal([new SwingOpened("C:", SwingKind.Growth, Ts(10))], actions);
    }

    [Fact]
    public void Out_of_order_and_duplicate_samples_are_ignored()
    {
        var (_, actions) = Run([At(0, 100), At(0.5, 100), At(0.5, 90), At(0.2, 90), At(1, 100)]);

        Assert.Empty(actions);
    }

    [Fact]
    public void Feeding_samples_one_at_a_time_matches_feeding_them_together()
    {
        var samples = new List<VolumeSample>([.. Ramp(0, 10, 100, 100), .. Ramp(10.5, 16, 98, 98), .. Ramp(16.5, 60, 100, 100), .. Ramp(90, 100, 97, 97)]);

        var together = Run(samples).Actions;
        var state = VolumeSwingState.Empty("C:");
        var oneByOne = new List<SwingAction>();
        foreach (var sample in samples)
        {
            (state, var actions) = Run([sample], state);
            oneByOne.AddRange(actions);
        }

        Assert.Equal(together, oneByOne);
    }
}
