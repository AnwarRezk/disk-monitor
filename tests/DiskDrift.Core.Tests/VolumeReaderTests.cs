using DiskDrift.Core.Data;
using DiskDrift.Core.Sampling;

namespace DiskDrift.Core.Tests;

public sealed class VolumeReaderTests
{
    private sealed class FakeDrives : IDriveSource
    {
        public Dictionary<string, DriveState> Drives { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Exception? Throw { get; set; }

        public DriveState? Get(string volume)
        {
            if (Throw is not null)
                throw Throw;
            return Drives.GetValueOrDefault(volume);
        }
    }

    private static DriveState Fixed(long total, long free) => new(DriveType.Fixed, true, total, free);

    [Fact]
    public void Fixed_ready_drives_are_sampled_in_config_order_with_the_tick_timestamp()
    {
        var drives = new FakeDrives();
        drives.Drives["C:"] = Fixed(500, 120);
        drives.Drives["D:"] = Fixed(1000, 900);
        var reader = new VolumeReader(["D:", "C:"], drives);

        var result = reader.Read(1_700_000_000_000);

        Assert.Equal(
            [new VolumeSample(1_700_000_000_000, "D:", 1000, 900), new VolumeSample(1_700_000_000_000, "C:", 500, 120)],
            result.Samples);
        Assert.Empty(result.NewlySkipped);
        Assert.Empty(result.Recovered);
    }

    [Theory]
    [InlineData(DriveType.Removable, true, VolumeSkipReason.NotFixed)]
    [InlineData(DriveType.Network, true, VolumeSkipReason.NotFixed)]
    [InlineData(DriveType.CDRom, false, VolumeSkipReason.NotFixed)]
    [InlineData(DriveType.Fixed, false, VolumeSkipReason.NotReady)]
    public void Drives_that_are_not_fixed_or_not_ready_are_skipped(DriveType type, bool ready, VolumeSkipReason reason)
    {
        var drives = new FakeDrives();
        drives.Drives["C:"] = Fixed(500, 120);
        drives.Drives["E:"] = new DriveState(type, ready, 64, 32);
        var reader = new VolumeReader(["C:", "E:"], drives);

        var result = reader.Read(1);

        Assert.Equal(["C:"], result.Samples.Select(s => s.Volume));
        var skip = Assert.Single(result.NewlySkipped);
        Assert.Equal(("E:", reason), (skip.Volume, skip.Reason));
    }

    [Fact]
    public void A_missing_drive_is_skipped_as_not_found()
    {
        var reader = new VolumeReader(["Z:"], new FakeDrives());

        var result = reader.Read(1);

        Assert.Empty(result.Samples);
        Assert.Equal(VolumeSkipReason.NotFound, Assert.Single(result.NewlySkipped).Reason);
    }

    [Fact]
    public void A_drive_that_throws_is_skipped_as_failed_without_stopping_the_tick()
    {
        var drives = new FakeDrives { Throw = new IOException("The device is not ready.") };
        var reader = new VolumeReader(["C:"], drives);

        var result = reader.Read(1);

        var skip = Assert.Single(result.NewlySkipped);
        Assert.Equal(VolumeSkipReason.Failed, skip.Reason);
        Assert.Equal("The device is not ready.", skip.Detail);
    }

    [Fact]
    public void A_skip_is_reported_once_then_again_only_when_its_reason_changes()
    {
        var drives = new FakeDrives();
        var reader = new VolumeReader(["E:"], drives);

        Assert.Single(reader.Read(1).NewlySkipped);
        Assert.Empty(reader.Read(2).NewlySkipped);

        drives.Drives["E:"] = new DriveState(DriveType.Fixed, false, 0, 0);
        Assert.Equal(VolumeSkipReason.NotReady, Assert.Single(reader.Read(3).NewlySkipped).Reason);
        Assert.Empty(reader.Read(4).NewlySkipped);
    }

    [Fact]
    public void A_skipped_drive_that_comes_back_is_reported_as_recovered_once()
    {
        var drives = new FakeDrives();
        var reader = new VolumeReader(["E:"], drives);
        reader.Read(1);

        drives.Drives["E:"] = Fixed(64, 32);
        var back = reader.Read(2);
        var steady = reader.Read(3);

        Assert.Equal(["E:"], back.Recovered);
        Assert.Single(back.Samples);
        Assert.Empty(steady.Recovered);
    }
}
