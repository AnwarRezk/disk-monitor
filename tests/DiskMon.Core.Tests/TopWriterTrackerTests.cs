using DiskMon.Core.Analysis;

namespace DiskMon.Core.Tests;

public sealed class TopWriterTrackerTests
{
    private const long MB = Bytes.MB;
    private const long T0 = 1_000_000;
    private const long T1 = T0 + 60_000;
    private const long T2 = T1 + 60_000;

    private readonly TopWriterTracker _tracker = new();

    private static ProcessIoSnapshot Snap(int pid, long startMs, string name, long writeBytes, long readBytes = 0) =>
        new(new ProcessKey(pid, startMs), name, $@"C:\Apps\{name}", (ulong)readBytes, (ulong)writeBytes);

    private IReadOnlyList<ProcessIoDelta> Next(long ts, params ProcessIoSnapshot[] snapshots) =>
        _tracker.Next(ts, snapshots, count: 10, minWriteBytes: MB);

    [Fact]
    public void First_snapshot_is_only_a_baseline()
    {
        Assert.Empty(Next(T0, Snap(100, 0, "a.exe", 500 * MB)));
    }

    [Fact]
    public void Delta_is_the_growth_since_the_previous_snapshot()
    {
        Next(T0, Snap(100, 0, "a.exe", 500 * MB, 10 * MB));

        var writer = Assert.Single(Next(T1, Snap(100, 0, "a.exe", 520 * MB, 13 * MB)));

        Assert.Equal(new ProcessIoDelta("a.exe", @"C:\Apps\a.exe", 20 * MB, 3 * MB), writer);
    }

    [Fact]
    public void Keeps_the_top_writers_by_bytes_written_and_drops_small_or_zero_ones()
    {
        var baseline = Enumerable.Range(1, 15).Select(i => Snap(i, 0, $"p{i:00}.exe", 0)).ToArray();
        Next(T0, [.. baseline, Snap(99, 0, "idle.exe", 7 * MB), Snap(98, 0, "tiny.exe", 0)]);

        var writers = Next(T1,
        [
            .. Enumerable.Range(1, 15).Select(i => Snap(i, 0, $"p{i:00}.exe", i * 2 * MB)),
            Snap(99, 0, "idle.exe", 7 * MB),            // no change: zero delta
            Snap(98, 0, "tiny.exe", MB - 1),            // below 1 MB
        ]);

        Assert.Equal(10, writers.Count);
        Assert.Equal(Enumerable.Range(6, 10).Reverse().Select(i => $"p{i:00}.exe"), writers.Select(w => w.Name));
        Assert.All(writers, w => Assert.True(w.WriteBytes >= MB));
    }

    [Fact]
    public void A_reused_pid_never_gets_a_negative_or_inflated_delta()
    {
        Next(T0, Snap(100, startMs: 0, "old.exe", 900 * MB));

        // The old process exited and a new one got PID 100 before the previous snapshot: baseline only.
        Assert.Empty(Next(T1, Snap(100, startMs: T0 - 1, "new.exe", 5 * MB)));

        var writer = Assert.Single(Next(T2, Snap(100, startMs: T0 - 1, "new.exe", 8 * MB)));
        Assert.Equal(("new.exe", 3 * MB), (writer.Name, writer.WriteBytes));
    }

    [Fact]
    public void A_process_started_during_the_interval_counts_in_full()
    {
        Next(T0, Snap(100, 0, "old.exe", 900 * MB));

        // PID 100 reused by a process that started after the previous snapshot: all of its I/O is in this interval.
        var writer = Assert.Single(Next(T1, Snap(100, startMs: T0 + 5_000, "setup.exe", 40 * MB)));

        Assert.Equal(("setup.exe", 40 * MB), (writer.Name, writer.WriteBytes));
    }

    [Fact]
    public void Counters_that_go_backwards_are_rebaselined_not_negative()
    {
        Next(T0, Snap(100, 0, "a.exe", 500 * MB));

        Assert.Empty(Next(T1, Snap(100, 0, "a.exe", 100 * MB)));
        Assert.Equal(10 * MB, Assert.Single(Next(T2, Snap(100, 0, "a.exe", 110 * MB))).WriteBytes);
    }

    [Fact]
    public void Exited_processes_are_forgotten()
    {
        Next(T0, Snap(100, 0, "a.exe", 500 * MB));
        Next(T1);  // a.exe gone

        // Same key again (cannot really happen, but proves the old counters were dropped): baseline only.
        Assert.Empty(Next(T2, Snap(100, 0, "a.exe", 600 * MB)));
    }
}
