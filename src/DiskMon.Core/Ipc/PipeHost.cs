using System.IO.Pipes;

namespace DiskMon.Core.Ipc;

/// <summary>
/// The pipe's accept loop: keeps one instance listening, serves each connection on its own task (one request line,
/// one response line, then close) and survives misbehaving clients. The caller supplies the instances, so the
/// Collector can attach its ACL while tests use plain ones.
/// </summary>
public sealed class PipeHost(Func<bool, NamedPipeServerStream> createInstance, PipeCommandHandler handler, Action<Exception>? onClientError = null)
{
    /// <summary>How long a connected client may take to send its request line.</summary>
    public static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Serves until <paramref name="token"/> is cancelled. <c>createInstance(true)</c> is called for the very first
    /// instance and must fail if another process already owns the pipe name, so a squatter cannot impersonate the
    /// Collector; later instances are created while one of ours is still open.
    /// </summary>
    public async Task RunAsync(CancellationToken token)
    {
        var clients = new List<Task>();
        var first = true;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var server = createInstance(first);
                first = false;
                try
                {
                    await server.WaitForConnectionAsync(token);
                }
                catch
                {
                    await server.DisposeAsync();
                    throw;
                }

                clients.RemoveAll(t => t.IsCompleted);
                clients.Add(ServeAsync(server, token));
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            await Task.WhenAll(clients);
        }
    }

    private async Task ServeAsync(NamedPipeServerStream server, CancellationToken stopping)
    {
        await using var _ = server;
        try
        {
            string? line;
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping))
            {
                timeout.CancelAfter(RequestTimeout);
                line = await PipeLines.ReadLineAsync(server, timeout.Token);
            }

            if (line is null)
                return; // Connected and hung up without a request: nobody to answer.

            var response = await handler.HandleAsync(line, stopping);
            await PipeLines.WriteLineAsync(server, PipeProtocol.Serialize(response), stopping);
            await AwaitHangUpAsync(server, stopping);
        }
        catch (InvalidDataException ex)
        {
            await TryWriteAsync(server, PipeResponse.Fail(ex.Message), stopping);
        }
        catch (OperationCanceledException) when (stopping.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // A client that hung up or stalled: drop it and keep serving.
            onClientError?.Invoke(ex);
        }
    }

    /// <summary>
    /// Waits (briefly) for the client to close its end after reading the response. Closing ours first could discard
    /// a response the client has not read yet, which is how a successful ping would turn into "Collector not running".
    /// </summary>
    private static async Task AwaitHangUpAsync(NamedPipeServerStream server, CancellationToken stopping)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        timeout.CancelAfter(RequestTimeout);
        var buffer = new byte[64];
        try
        {
            while (await server.ReadAsync(buffer, timeout.Token) > 0)
            {
                // A client has nothing more to say after its request; ignore anything it sends.
            }
        }
        catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
        {
        }
        catch (IOException)
        {
            // Broken pipe: the client is gone, which is what we were waiting for.
        }
    }

    private async Task TryWriteAsync(NamedPipeServerStream server, PipeResponse response, CancellationToken token)
    {
        try
        {
            await PipeLines.WriteLineAsync(server, PipeProtocol.Serialize(response), token);
        }
        catch (Exception ex)
        {
            onClientError?.Invoke(ex);
        }
    }
}
