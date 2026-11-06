using DiskDrift.Core.Analysis;
using DiskDrift.Core.Probes;

namespace DiskDrift.Core.Tests;

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

    [Fact]
    public void Processes_of_one_program_are_summed_into_one_row()
    {
        Next(T0, Snap(1, 0, "claude.exe", 100 * MB, 10 * MB), Snap(2, 0, "claude.exe", 200 * MB, 20 * MB));

        var writer = Assert.Single(Next(T1,
            Snap(1, 0, "claude.exe", 105 * MB, 11 * MB),
            Snap(2, 0, "claude.exe", 207 * MB, 22 * MB),
            Snap(3, T0 + 1_000, "claude.exe", 4 * MB, 1 * MB)));   // new copy started during the interval

        Assert.Equal(new ProcessIoDelta("claude.exe", @"C:\Apps\claude.exe", 16 * MB, 4 * MB), writer);
    }

    [Fact]
    public void The_minimum_applies_to_the_sum_not_to_each_process()
    {
        const long sixTenthsMB = 6 * MB / 10;
        Next(T0, Snap(1, 0, "a.exe", 0), Snap(2, 0, "a.exe", 0), Snap(3, 0, "b.exe", 0));

        var writer = Assert.Single(Next(T1,
            Snap(1, 0, "a.exe", sixTenthsMB),
            Snap(2, 0, "a.exe", sixTenthsMB),     // 0.6 MB + 0.6 MB = 1.2 MB: kept
            Snap(3, 0, "b.exe", sixTenthsMB)));   // 0.6 MB alone: dropped

        Assert.Equal(("a.exe", 2 * sixTenthsMB), (writer.Name, writer.WriteBytes));
    }

    [Fact]
    public void Ranking_and_the_top_n_cut_use_the_sums()
    {
        // Twelve copies of a browser would have filled every slot when ranked per process.
        var browser = Enumerable.Range(1, 12).ToArray();
        Next(T0, [.. browser.Select(i => Snap(i, 0, "browser.exe", 0)), Snap(50, 0, "big.exe", 0), Snap(51, 0, "mid.exe", 0)]);

        var writers = _tracker.Next(T1,
        [
            .. browser.Select(i => Snap(i, 0, "browser.exe", 5 * MB)),   // 60 MB in total
            Snap(50, 0, "big.exe", 40 * MB),
            Snap(51, 0, "mid.exe", 30 * MB),
        ], count: 2, minWriteBytes: MB);

        Assert.Equal([("browser.exe", 60 * MB), ("big.exe", 40 * MB)], writers.Select(w => (w.Name, w.WriteBytes)));
    }

    [Fact]
    public void A_program_is_its_name_and_exe_path_ignoring_case()
    {
        ProcessIoSnapshot At(int pid, string name, string? exePath, long write) =>
            new(new ProcessKey(pid, 0), name, exePath, 0, (ulong)write);

        Next(T0, At(1, "app.exe", @"C:\A\app.exe", 0), At(2, "APP.EXE", @"c:\a\APP.exe", 0),
            At(3, "app.exe", @"C:\B\app.exe", 0), At(4, "System", null, 0), At(5, "System", null, 0));

        var writers = Next(T1, At(1, "app.exe", @"C:\A\app.exe", 2 * MB), At(2, "APP.EXE", @"c:\a\APP.exe", 3 * MB),
            At(3, "app.exe", @"C:\B\app.exe", 4 * MB), At(4, "System", null, 1 * MB), At(5, "System", null, 1 * MB));

        Assert.Equal(
            [(@"C:\A\app.exe", 5 * MB), (@"C:\B\app.exe", 4 * MB), (null, 2 * MB)],
            writers.Select(w => (w.ExePath, w.WriteBytes)));
    }

    [Fact]
    public void A_process_under_a_user_profile_uses_the_user_template_and_sums_across_profiles()
    {
        ProcessIoSnapshot At(int pid, string exePath, long write) =>
            new(new ProcessKey(pid, 0), "Code.exe", exePath, 0, (ulong)write);

        Next(T0,
            At(1, @"C:\Users\alice\AppData\Local\Programs\Microsoft VS Code\Code.exe", 0),
            At(2, @"C:\Users\bob\AppData\Local\Programs\Microsoft VS Code\Code.exe", 0));

        var writer = Assert.Single(Next(T1,
            At(1, @"C:\Users\alice\AppData\Local\Programs\Microsoft VS Code\Code.exe", 3 * MB),
            At(2, @"C:\Users\bob\AppData\Local\Programs\Microsoft VS Code\Code.exe", 4 * MB)));

        Assert.Equal(
            new ProcessIoDelta("Code.exe", ProbeExpander.NormalizeSeparators(@"{user}\AppData\Local\Programs\Microsoft VS Code\Code.exe"), 7 * MB, 0),
            writer);
    }
}
