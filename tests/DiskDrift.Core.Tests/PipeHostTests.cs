using System.IO.Pipes;
using DiskDrift.Core.Ipc;

namespace DiskDrift.Core.Tests;

/// <summary>Client and accept loop over a real (unsecured) named pipe; the Collector's ACL is checked on the PC.</summary>
public sealed class PipeHostTests
{
    private readonly string _name = "DiskDriftTest-" + Guid.NewGuid().ToString("N");

    private NamedPipeServerStream Create(bool first) => new(
        _name,
        PipeDirection.InOut,
        NamedPipeServerStream.MaxAllowedServerInstances,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None));

    private static PipeCommandHandler PingHandler() => new(new Dictionary<string, Func<CancellationToken, Task<PipeResponse>>>
    {
        ["ping"] = _ => Task.FromResult(new PipeResponse(true, "test", 42)),
    });

    [Fact]
    public async Task Ping_round_trips_and_the_server_keeps_serving_after_bad_requests()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var server = new PipeHost(Create, PingHandler()).RunAsync(stop.Token);
        var client = new PipeClient(_name);

        Assert.Equal(new PipeResponse(true, "test", 42), await client.PingAsync(stop.Token));

        var unknown = await client.SendAsync(new PipeRequest("nope"), PipeClient.DefaultTimeout, stop.Token);
        Assert.False(unknown.Ok);
        Assert.Contains("unknown command", unknown.Error);

        for (var i = 0; i < 3; i++)
            Assert.True((await client.PingAsync(stop.Token)).Ok);

        stop.Cancel();
        await server;
    }

    [Fact]
    public async Task Hung_up_and_concurrent_clients_do_not_stop_the_server()
    {
        // .NET's Unix emulation of named pipes (sockets) resets clients queued behind one that hung up or ran
        // concurrently; the Collector only runs on Windows, where real named pipes queue them properly.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Needs real Windows named pipes");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var server = new PipeHost(Create, PingHandler()).RunAsync(stop.Token);
        var client = new PipeClient(_name);

        // A client that connects and hangs up without a request.
        await using (var rude = new NamedPipeClientStream(".", _name, PipeDirection.InOut, PipeOptions.Asynchronous))
            await rude.ConnectAsync(stop.Token);

        Assert.True((await client.PingAsync(stop.Token)).Ok);
        var pings = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => client.PingAsync(stop.Token)));
        Assert.All(pings, p => Assert.True(p.Ok));

        stop.Cancel();
        await server;
    }

    [Fact]
    public async Task A_client_that_hangs_up_before_the_server_waits_does_not_stop_the_server()
    {
        // The race from CI: a client connects to a fresh instance and hangs up before WaitForConnectionAsync, which
        // then fails with ERROR_NO_DATA. Reproduced deterministically by hanging up inside the instance factory.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Needs real Windows named pipes");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var calls = 0;
        NamedPipeServerStream CreateWithEarlyHangUp(bool first)
        {
            var instance = Create(first);
            if (Interlocked.Increment(ref calls) == 1)
            {
                using var rude = new NamedPipeClientStream(".", _name, PipeDirection.InOut);
                rude.Connect(1000);
            }
            return instance;
        }

        var errors = 0;
        var server = new PipeHost(CreateWithEarlyHangUp, PingHandler(), _ => Interlocked.Increment(ref errors)).RunAsync(stop.Token);

        Assert.True((await new PipeClient(_name).PingAsync(stop.Token)).Ok);
        Assert.False(server.IsCompleted);
        Assert.Equal(1, errors);

        stop.Cancel();
        await server;
    }

    [Fact]
    public async Task Ping_times_out_when_nothing_serves_the_pipe()
    {
        var client = new PipeClient(_name);
        await Assert.ThrowsAsync<TimeoutException>(() =>
            client.SendAsync(new PipeRequest("ping"), TimeSpan.FromMilliseconds(300), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task The_first_instance_fails_when_another_process_owns_the_name()
    {
        // Windows only: elsewhere .NET emulates named pipes with Unix sockets and FirstPipeInstance is not enforced.
        Assert.SkipUnless(OperatingSystem.IsWindows(), "FirstPipeInstance is a Windows named-pipe feature");
        await using var squatter = Create(first: true);

        await Assert.ThrowsAnyAsync<Exception>(() => new PipeHost(Create, PingHandler()).RunAsync(TestContext.Current.CancellationToken));
    }
}
