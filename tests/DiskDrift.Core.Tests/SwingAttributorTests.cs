using Dapper;
using DiskDrift.Core.Analysis;
using DiskDrift.Core.Config;
using DiskDrift.Core.Data;
using DiskDrift.Core.Probes;

namespace DiskDrift.Core.Tests;

/// <summary>Closing → Closed against a real temp SQLite DB: waiting for the after-side run, storing items, restarts.</summary>
public sealed class SwingAttributorTests : IDisposable
{
    private const long Gb = Bytes.GB;
    private const long Min = 60_000;

    private readonly TempDirectory _temp = new();
    private readonly Db _db;
    private readonly SwingStore _swings;
    private readonly SwingAttributor _attributor;
    private readonly IReadOnlyList<long> _probeIds;

    public SwingAttributorTests()
    {
        _db = Db.OpenOrCreate(_temp.File("diskdrift.db"));
        _swings = new SwingStore(_db);
        _attributor = new SwingAttributor(_db);
        using var connection = _db.OpenConnection();
        _probeIds = ProbeStore.Upsert(connection,
        [
            new ExpandedProbe("User Temp (anwar)", ProbeKind.Dir, @"C:\Users\anwar\AppData\Local\Temp", "C:"),
            new ExpandedProbe("Windows Temp", ProbeKind.Dir, @"C:\Windows\Temp", "C:"),
            new ExpandedProbe("D: data", ProbeKind.Dir, @"D:\Data", "D:"),
        ]);
    }

    public void Dispose() => _temp.Dispose();

    /// <summary>One Probe run at <paramref name="minute"/>: user Temp, Windows Temp, D: data sizes in GB.</summary>
    private void ProbeRun(double minute, double userTempGb, double windowsTempGb = 1, double dGb = 0)
    {
        using var connection = _db.OpenConnection();
        ProbeStore.InsertSamples(connection, (long)(minute * Min),
        [
            (_probeIds[0], new ProbeMeasurement((long)(userTempGb * Gb), null, 10)),
            (_probeIds[1], new ProbeMeasurement((long)(windowsTempGb * Gb), null, 10)),
            (_probeIds[2], new ProbeMeasurement((long)(dGb * Gb), null, 10)),
        ]);
    }

    private long ClosingGrowth(double startMinute, double endMinute, double usedGb)
    {
        var id = _swings.InsertOpen("C:", SwingKind.Growth, (long)(startMinute * Min));
        _swings.MarkClosing(id, (long)(endMinute * Min), (long)(usedGb * Gb));
        return id;
    }

    private SwingRow Row(long id) => _swings.Overlapping("C:", long.MinValue, long.MaxValue).Single(r => r.Id == id);

    [Fact]
    public void A_Closing_Swing_waits_for_a_Probe_run_at_or_after_its_end_then_closes_with_its_items()
    {
        ProbeRun(0, userTempGb: 0);
        var id = ClosingGrowth(5, 12, usedGb: 3);
        ProbeRun(10, userTempGb: 2); // a run during the Swing is no after-side

        Assert.Empty(_attributor.AttributeReady());
        Assert.Equal("closing", Row(id).Stage);

        using (var connection = _db.OpenConnection())
            ProcessIoStore.Insert(connection, 8 * Min, [new ProcessIoDelta("fsutil.exe", @"C:\Windows\System32\fsutil.exe", 2 * Gb, 0)]);
        ProbeRun(13, userTempGb: 2, dGb: 9); // D: is another Volume: ignored

        var attributed = Assert.Single(_attributor.AttributeReady());

        Assert.Equal(id, attributed.Swing.Id);
        Assert.Equal(
            new SwingRow(id, "C:", "growth", "closed", false, 5 * Min, 12 * Min, 3 * Gb, 2 * Gb, 1 * Gb, 0, 13 * Min),
            Row(id));
        Assert.Equal(
            [
                new SwingItem("probe", "User Temp (anwar)", @"C:\Users\anwar\AppData\Local\Temp", 2 * Gb),
                new SwingItem("process", "fsutil.exe", @"C:\Windows\System32\fsutil.exe", 2 * Gb),
            ],
            _swings.Items(id));
        Assert.Empty(_attributor.AttributeReady()); // closed once: no duplicate items
        Assert.Equal(2, _swings.Items(id).Count);
    }

    [Fact]
    public void After_a_restart_a_Closing_Swing_is_attributed_with_whatever_samples_exist()
    {
        // The Collector stopped before the post-close Probe run finished: no run at or after minute 12.
        ProbeRun(0, userTempGb: 0);
        var id = ClosingGrowth(5, 12, usedGb: 2);

        var attributed = Assert.Single(_attributor.AttributeAfterRestart());

        Assert.Equal(id, attributed.Swing.Id);
        var row = Row(id);
        Assert.Equal("closed", row.Stage);
        Assert.Equal((0L, 2 * Gb), (row.AttributedBytes, row.UnaccountedBytes));
        Assert.Null(row.ProbeWindowStart);
        Assert.Empty(_swings.Items(id));
    }

    [Fact]
    public void After_a_restart_a_Swing_whose_after_side_run_was_stored_is_attributed_normally()
    {
        ProbeRun(0, userTempGb: 0);
        var id = ClosingGrowth(5, 12, usedGb: 2);
        ProbeRun(12, userTempGb: 2);

        Assert.Single(_attributor.AttributeAfterRestart());

        Assert.Equal((2 * Gb, 0L, 0L, 12 * Min), (Row(id).AttributedBytes, Row(id).UnaccountedBytes, Row(id).ProbeWindowStart, Row(id).ProbeWindowEnd));
    }

    [Fact]
    public void Attributing_by_end_time_leaves_later_Swings_waiting()
    {
        ProbeRun(0, userTempGb: 0);
        var cut = ClosingGrowth(5, 12, usedGb: 2);    // closed by Downtime at minute 12
        var across = ClosingGrowth(12, 40, usedGb: 2); // the move across the gap

        var attributed = _attributor.AttributeEndedBy(12 * Min);

        Assert.Equal([cut], attributed.Select(a => a.Swing.Id));
        Assert.Equal("closing", Row(across).Stage);
    }

    [Fact]
    public void Open_Swings_are_never_attributed()
    {
        ProbeRun(0, userTempGb: 0);
        var id = _swings.InsertOpen("C:", SwingKind.Growth, 5 * Min);
        ProbeRun(20, userTempGb: 2);

        Assert.Empty(_attributor.AttributeAfterRestart());
        using var connection = _db.OpenConnection();
        Assert.Equal("open", connection.ExecuteScalar<string>("SELECT stage FROM swings WHERE id = @id", new { id }));
    }
}
