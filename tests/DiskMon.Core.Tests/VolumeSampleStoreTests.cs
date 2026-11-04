using Dapper;
using DiskMon.Core.Data;
using DiskMon.Core.Sampling;

namespace DiskMon.Core.Tests;

public sealed class VolumeSampleStoreTests : IDisposable
{
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    [Fact]
    public void Inserted_samples_are_stored_and_the_latest_per_volume_is_returned()
    {
        var db = Db.OpenOrCreate(_temp.File("diskmon.db"));
        var store = new VolumeSampleStore(db);

        store.Insert([new VolumeSample(1_000, "C:", 500, 200), new VolumeSample(1_000, "D:", 900, 800)]);
        store.Insert([new VolumeSample(31_000, "C:", 500, 190)]);

        using var connection = db.OpenConnection();
        Assert.Equal(3, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM volume_samples"));
        Assert.Equal(new VolumeSample(31_000, "C:", 500, 190), store.Latest("C:"));
        Assert.Equal(new VolumeSample(1_000, "D:", 900, 800), store.Latest("D:"));
        Assert.Null(store.Latest("E:"));
    }

    [Fact]
    public void Finds_the_newest_sample_at_or_before_a_time_and_the_oldest_sample()
    {
        var store = new VolumeSampleStore(Db.OpenOrCreate(_temp.File("diskmon.db")));
        store.Insert([new VolumeSample(1_000, "C:", 500, 200), new VolumeSample(1_000, "D:", 900, 800)]);
        store.Insert([new VolumeSample(31_000, "C:", 500, 190)]);
        store.Insert([new VolumeSample(61_000, "C:", 500, 180)]);

        Assert.Equal(190, store.AtOrBefore("C:", 31_000)!.FreeBytes);
        Assert.Equal(190, store.AtOrBefore("C:", 60_999)!.FreeBytes);
        Assert.Null(store.AtOrBefore("C:", 999));
        Assert.Equal(200, store.Oldest("C:")!.FreeBytes);
        Assert.Null(store.Oldest("E:"));
    }

    [Fact]
    public void Inserting_nothing_writes_nothing()
    {
        var db = Db.OpenOrCreate(_temp.File("diskmon.db"));

        new VolumeSampleStore(db).Insert([]);

        using var connection = db.OpenConnection();
        Assert.Equal(0, connection.ExecuteScalar<int>("SELECT COUNT(*) FROM volume_samples"));
    }

    [Fact]
    public void The_channel_drops_the_oldest_sample_when_nobody_reads_it()
    {
        var channel = new VolumeSampleChannel();

        for (var i = 0; i <= VolumeSampleChannel.Capacity; i++)
            Assert.True(channel.Writer.TryWrite(new VolumeSample(i, "C:", 1, 1)));

        Assert.True(channel.Reader.TryRead(out var first));
        Assert.Equal(1, first.Ts);
        Assert.Equal(VolumeSampleChannel.Capacity, channel.Reader.Count + 1);
    }
}
