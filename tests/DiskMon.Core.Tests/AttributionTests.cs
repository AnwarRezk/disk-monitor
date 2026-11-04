using DiskMon.Core.Analysis;

namespace DiskMon.Core.Tests;

/// <summary>The pure Attribution math (PLAN §5.4): Probe Window, 50 MB cut-off, ranking, Unaccounted, process items.</summary>
public sealed class AttributionTests
{
    private const long Gb = Bytes.GB;
    private const long Mb = Bytes.MB;
    private const long Min = 60_000;

    private static ProbeHistory Probe(string name, params (double Minute, long Size)[] samples) =>
        new(name, $@"C:\{name}", samples.Select(s => new ProbePoint((long)(s.Minute * Min), s.Size)).ToList());

    private static IReadOnlyList<AttributionItem> ProbeItems(AttributionResult r) => r.Items.Where(i => i.Source == "probe").ToList();

    [Fact]
    public void Compares_each_Probe_between_its_nearest_samples_outside_the_Swing_and_records_that_as_the_Probe_Window()
    {
        // Swing 20..30 min; Probe runs every 10 min at 0, 10, 20 (= start), 25 (mid-Swing), 30 (= end), 40.
        var temp = Probe("Temp", (0, 0), (10, 0), (20, 1 * Gb), (25, 99 * Gb), (30, 3 * Gb), (40, 50 * Gb));

        var result = Attribution.Attribute(20 * Min, 30 * Min, 2 * Gb, [temp], []);

        Assert.Equal(20 * Min, result.ProbeWindowStart);
        Assert.Equal(30 * Min, result.ProbeWindowEnd);
        Assert.Equal(new AttributionItem("probe", "Temp", @"C:\Temp", 2 * Gb, 1), Assert.Single(result.Items));
    }

    [Fact]
    public void Without_samples_on_the_boundaries_the_window_widens_to_the_nearest_ones()
    {
        // Swing 14..33: nearest at or before is 10, nearest at or after is 40. Samples are given out of order.
        var temp = Probe("Temp", (40, 5 * Gb), (0, 0), (20, 2 * Gb), (10, 1 * Gb), (30, 3 * Gb));

        var result = Attribution.Attribute(14 * Min, 33 * Min, 4 * Gb, [temp], []);

        Assert.Equal((10 * Min, 40 * Min), (result.ProbeWindowStart, result.ProbeWindowEnd));
        Assert.Equal(4 * Gb, Assert.Single(result.Items).DeltaBytes);
    }

    [Fact]
    public void The_Probe_Window_spans_the_widest_pair_when_Probes_were_sampled_at_different_times()
    {
        var a = Probe("A", (10, 0), (40, 1 * Gb));
        var b = Probe("B", (5, 0), (35, 1 * Gb));

        var result = Attribution.Attribute(12 * Min, 32 * Min, 2 * Gb, [a, b], []);

        Assert.Equal((5 * Min, 40 * Min), (result.ProbeWindowStart, result.ProbeWindowEnd));
    }

    [Fact]
    public void Probes_without_a_sample_on_both_sides_or_with_a_failed_one_are_left_out()
    {
        var newProbe = Probe("New", (25, 0), (30, 5 * Gb));             // nothing at or before the start
        var noAfter = Probe("No after", (10, 0), (25, 5 * Gb));         // nothing at or after the end
        var timedOut = new ProbeHistory("Timed out", @"C:\T", [new(10, 10 * Gb), new(30 * Min, 1 * Gb, Failed: true)]);

        var result = Attribution.Attribute(20 * Min, 30 * Min, 2 * Gb, [newProbe, noAfter, timedOut], []);

        Assert.Empty(result.Items);
        Assert.Null(result.ProbeWindowStart);
        Assert.Null(result.ProbeWindowEnd);
        Assert.Equal(0, result.AttributedBytes);
        Assert.Equal(2 * Gb, result.UnaccountedBytes);
    }

    [Fact]
    public void Keeps_Probe_items_of_50_MB_or_more_either_way_ranked_by_absolute_delta()
    {
        var probes = new[]
        {
            Probe("Small", (0, 0), (10, 49 * Mb)),
            Probe("Edge", (0, 100 * Mb), (10, 50 * Mb)),        // exactly −50 MB: kept
            Probe("Grew", (0, 0), (10, 2 * Gb)),
            Probe("Shrank", (0, 3 * Gb), (10, 0)),
            Probe("Same", (0, 7 * Gb), (10, 7 * Gb)),
        };

        var result = Attribution.Attribute(0, 10 * Min, 0, probes, []);

        Assert.Equal(
            [
                new AttributionItem("probe", "Shrank", @"C:\Shrank", -3 * Gb, 1),
                new AttributionItem("probe", "Grew", @"C:\Grew", 2 * Gb, 2),
                new AttributionItem("probe", "Edge", @"C:\Edge", -50 * Mb, 3),
            ],
            ProbeItems(result));
    }

    [Fact]
    public void Unaccounted_is_the_used_delta_minus_the_kept_Probe_deltas()
    {
        var probes = new[]
        {
            Probe("Temp", (0, 0), (10, 2 * Gb)),
            Probe("Cache", (0, 1 * Gb), (10, 1 * Gb - 300 * Mb)),
            Probe("Tiny", (0, 0), (10, 10 * Mb)),            // under the cut-off: part of Unaccounted
        };

        var result = Attribution.Attribute(0, 10 * Min, 5 * Gb, probes, []);

        Assert.Equal(2 * Gb - 300 * Mb, result.AttributedBytes);
        Assert.Equal(5 * Gb - (2 * Gb - 300 * Mb), result.UnaccountedBytes);
        Assert.Equal(result.AttributedBytes, ProbeItems(result).Sum(i => i.DeltaBytes));
    }

    [Fact]
    public void Release_Swings_are_attributed_with_negative_deltas()
    {
        var temp = Probe("Temp", (0, 2 * Gb), (10, 0));

        var result = Attribution.Attribute(0, 10 * Min, -2 * Gb, [temp], []);

        Assert.Equal(-2 * Gb, result.AttributedBytes);
        Assert.Equal(0, result.UnaccountedBytes);
    }

    [Fact]
    public void Process_items_are_the_top_10_writers_ranked_and_never_counted_in_Attributed()
    {
        var writers = Enumerable.Range(1, 12)
            .Select(i => new WriterDelta($"app{i}.exe", $@"C:\Apps\app{i}.exe", i * Gb))
            .Append(new WriterDelta("idle.exe", null, 0))
            .ToList();

        var result = Attribution.Attribute(0, 10 * Min, 3 * Gb, [Probe("Temp", (0, 0), (10, 1 * Gb))], writers);

        var processes = result.Items.Where(i => i.Source == "process").ToList();
        Assert.Equal(10, processes.Count);
        Assert.Equal(new AttributionItem("process", "app12.exe", @"C:\Apps\app12.exe", 12 * Gb, 1), processes[0]);
        Assert.Equal(Enumerable.Range(1, 10), processes.Select(p => p.Rank));
        Assert.DoesNotContain(processes, p => p.Label is "idle.exe" or "app1.exe" or "app2.exe");
        Assert.Equal(1 * Gb, result.AttributedBytes);
        Assert.Equal(2 * Gb, result.UnaccountedBytes);
        Assert.Equal("probe", result.Items[0].Source); // Probe items come first
    }
}
