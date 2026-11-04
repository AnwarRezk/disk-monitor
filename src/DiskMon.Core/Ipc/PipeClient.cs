using System.IO.Pipes;

namespace DiskMon.Core.Ipc;

/// <summary>The Tray's side of the pipe: connect, send one request line, read one response line.</summary>
public sealed class PipeClient(string pipeName = PipeProtocol.PipeName)
{
    /// <summary>The Collector answers <c>ping</c> at once; past this it is treated as not running.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

    public Task<PipeResponse> PingAsync(CancellationToken token = default) =>
        SendAsync(new PipeRequest(PipeProtocol.Ping), DefaultTimeout, token);

    /// <summary>
    /// Sends <paramref name="request"/> and returns the Collector's response. Throws <see cref="TimeoutException"/>
    /// when nothing connects or answers within <paramref name="timeout"/>, and <see cref="IOException"/> or
    /// <see cref="System.Text.Json.JsonException"/> when the exchange breaks.
    /// </summary>
    public async Task<PipeResponse> SendAsync(PipeRequest request, TimeSpan timeout, CancellationToken token = default)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        try
        {
            await using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(deadline.Token);
            await PipeLines.WriteLineAsync(pipe, PipeProtocol.Serialize(request), deadline.Token);
            var line = await PipeLines.ReadLineAsync(pipe, deadline.Token)
                ?? throw new IOException("the Collector closed the pipe without answering");
            return PipeProtocol.ParseResponse(line);
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested && !token.IsCancellationRequested)
        {
            throw new TimeoutException($"no answer on pipe {pipeName} within {timeout.TotalSeconds:0.#} s");
        }
    }
}
