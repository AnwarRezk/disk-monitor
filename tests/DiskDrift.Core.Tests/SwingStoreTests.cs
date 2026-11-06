using DiskDrift.Core.Analysis;
using DiskDrift.Core.Data;

namespace DiskDrift.Core.Tests;

/// <summary>The Collector's writes to the <c>swings</c> table, read back the way the dashboard reads them.</summary>
public sealed class SwingStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void An_Open_Swing_moves_to_Closing_with_its_end_and_used_delta()
    {
        var store = new SwingStore(Db.OpenOrCreate(_temp.File("diskdrift.db")));

        var id = store.InsertOpen("C:", SwingKind.Growth, 1_000);
        Assert.Equal([new StoredOpenSwing(id, "C:", SwingKind.Growth, 1_000)], store.OpenSwings());
        Assert.Equal(new SwingRow(id, "C:", "growth", "open", false, 1_000, null, null, null, null), Assert.Single(store.Overlapping("C:", 0, 2_000)));

        store.MarkClosing(id, 5_000, 2 * Bytes.GB);

        Assert.Empty(store.OpenSwings());
        Assert.Equal(new SwingRow(id, "C:", "growth", "closing", false, 1_000, 5_000, 2 * Bytes.GB, null, null), Assert.Single(store.Overlapping("C:", 0, 9_000)));
    }

    [Fact]
    public void A_Swing_across_Downtime_goes_straight_to_Closing_and_is_flagged()
    {
        var store = new SwingStore(Db.OpenOrCreate(_temp.File("diskdrift.db")));

        var id = store.InsertAcrossDowntime("C:", SwingKind.Release, 1_000, 9_000, -3 * Bytes.GB);

        Assert.Empty(store.OpenSwings());
        Assert.Equal(new SwingRow(id, "C:", "release", "closing", true, 1_000, 9_000, -3 * Bytes.GB, null, null), Assert.Single(store.Overlapping("C:", 0, 9_000)));
    }

    [Fact]
    public void Marking_a_Swing_that_is_no_longer_Open_changes_nothing()
    {
        var store = new SwingStore(Db.OpenOrCreate(_temp.File("diskdrift.db")));
        var id = store.InsertAcrossDowntime("C:", SwingKind.Growth, 1_000, 2_000, Bytes.GB);

        store.MarkClosing(id, 7_000, 5);

        Assert.Equal(2_000, Assert.Single(store.Overlapping("C:", 0, 9_000)).TsEnd);
    }
}
