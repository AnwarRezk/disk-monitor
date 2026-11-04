using DiskMon.Core.Analysis;
using DiskMon.Core.Ipc;

namespace DiskMon.Core.Tests;

public sealed class CollectorStatusTests
{
    private const long Now = 1_730_721_600_000;
    private const long Minute = 60_000;

    [Fact]
    public void Running_needs_a_successful_ping_with_a_sample_at_most_two_minutes_old()
    {
        Assert.True(CollectorStatus.IsRunning(new PipeResponse(true, "v", Now - 30_000), Now));
        Assert.True(CollectorStatus.IsRunning(new PipeResponse(true, "v", Now - 2 * Minute), Now));

        Assert.False(CollectorStatus.IsRunning(new PipeResponse(true, "v", Now - 2 * Minute - 1), Now));
        Assert.False(CollectorStatus.IsRunning(new PipeResponse(true, "v", null), Now));
        Assert.False(CollectorStatus.IsRunning(PipeResponse.Fail("x"), Now));
        Assert.False(CollectorStatus.IsRunning(null, Now));
    }
}
