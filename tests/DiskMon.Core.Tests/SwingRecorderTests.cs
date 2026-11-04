using DiskMon.Core.Analysis;
using DiskMon.Core.Data;

namespace DiskMon.Core.Tests;

/// <summary>Detector plus <c>swings</c> table against a real temp SQLite DB, including a Collector restart.</summary>
public sealed class SwingRecorderTests : IDisposable
{
    private const long Gb = Bytes.GB;
    private const long Min = 60_000;
    private static readonly SwingSettings Settings = new(Gb, 15 * Min, 120 * Min, 30_000);

    private readonly TempDirectory _temp = new();
    private readonly Db _db;
    private readonly VolumeSampleStore _samples;
    private readonly SwingStore _swings;

    public SwingRecorderTests()
    {
        _db = Db.OpenOrCreate(_temp.File("diskmon.db"));
        _samples = new VolumeSampleStore(_db);
        _swings = new SwingStore(_db);
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>Stores the sample (as the VolumeSampler does), then hands it to the recorder.</summary>
    private void Feed(SwingRecorder recorder, double minute, double freeGb)
    {
        var sample = new VolumeSample((long)(minute * Min), "C:", 500 * Gb, (long)(freeGb * Gb));
        _samples.Insert([sample]);
        recorder.Add(sample);
    }

    private IReadOnlyList<SwingRow> All() => _swings.Overlapping("C:", long.MinValue, long.MaxValue).Reverse().ToList();

    [Fact]
    public void Create_then_delete_within_minutes_records_a_Growth_then_a_Release_Swing()
    {
        var recorder = new SwingRecorder(_swings, _samples, Settings);
        recorder.Restore(["C:"]);
        for (var m = 0.0; m <= 10; m += 0.5) Feed(recorder, m, 100);
        for (var m = 10.5; m <= 16; m += 0.5) Feed(recorder, m, 98);
        for (var m = 16.5; m <= 40; m += 0.5) Feed(recorder, m, 100);

        Assert.Equal(
            [
                new SwingRow(1, "C:", "growth", "closing", false, 10 * Min, 16 * Min, 2 * Gb, null, null),
                new SwingRow(2, "C:", "release", "closing", false, 16 * Min, 32 * Min, -2 * Gb, null, null),
            ],
            All());
    }

    [Fact]
    public void A_restart_closes_the_Open_Swing_at_the_last_sample_and_records_the_move_across_the_gap()
    {
        var before = new SwingRecorder(_swings, _samples, Settings);
        before.Restore(["C:"]);
        for (var m = 0.0; m <= 10; m += 0.5) Feed(before, m, 100);
        for (var m = 10.5; m <= 12; m += 0.5) Feed(before, m, 98);
        Assert.Equal("open", Assert.Single(All()).Stage);

        // The Collector stops at minute 12; a 2 GB file is created; it starts again at minute 30.
        var after = new SwingRecorder(_swings, _samples, Settings);
        var restored = after.Restore(["C:"]);
        Feed(after, 30, 96);

        Assert.Equal(SwingCloseReason.Restart, Assert.IsType<SwingClosed>(Assert.Single(restored)).Reason);
        Assert.Equal(
            [
                new SwingRow(1, "C:", "growth", "closing", false, 10 * Min, 12 * Min, 2 * Gb, null, null),
                new SwingRow(2, "C:", "growth", "closing", true, 12 * Min, 30 * Min, 2 * Gb, null, null),
            ],
            All());
    }

    [Fact]
    public void A_Volume_with_no_history_starts_empty()
    {
        var recorder = new SwingRecorder(_swings, _samples, Settings);

        Assert.Empty(recorder.Restore(["C:", "D:"]));
        Feed(recorder, 0, 100);
        Assert.Empty(All());
    }
}
