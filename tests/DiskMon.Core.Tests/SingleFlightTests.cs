using DiskMon.Core.Probes;

namespace DiskMon.Core.Tests;

public sealed class SingleFlightTests
{
    [Fact]
    public async Task Callers_during_a_run_join_it_and_a_later_caller_starts_a_new_one()
    {
        var flight = new SingleFlight<int>();
        var gate = new TaskCompletionSource();
        var starts = 0;
        Task<int> Run() => flight.RunAsync(async () =>
        {
            var n = Interlocked.Increment(ref starts);
            await gate.Task;
            return n;
        });

        var first = Run();
        var second = Run();
        Assert.Same(first, second);
        Assert.Same(first, flight.Current);

        gate.SetResult();
        Assert.Equal(1, await first);
        Assert.Equal(1, await second);
        Assert.Null(flight.Current);

        Assert.Equal(2, await Run());
        Assert.Equal(2, starts);
    }

    [Fact]
    public async Task A_failed_run_does_not_block_the_next_one()
    {
        var flight = new SingleFlight<int>();

        await Assert.ThrowsAsync<InvalidOperationException>(() => flight.RunAsync(() => throw new InvalidOperationException()));
        Assert.Equal(7, await flight.RunAsync(() => Task.FromResult(7)));
    }
}
