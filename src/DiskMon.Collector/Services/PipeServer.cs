using System.IO.Pipes;
using DiskMon.Core.Ipc;
using DiskMon.Core.Security;

namespace DiskMon.Collector.Services;

/// <summary>
/// Serves the named pipe <c>DiskMon</c> (PLAN §5.5, ADR 0002): one JSON request line, one JSON response line. Only
/// <c>ping</c> for now; <c>probe_now</c> arrives with the Probes.
/// </summary>
public sealed class PipeServer(CollectorHeartbeat heartbeat, ILogger<PipeServer> logger) : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var handler = new PipeCommandHandler(new Dictionary<string, Func<CancellationToken, Task<PipeResponse>>>
        {
            [PipeProtocol.Ping] = _ => Task.FromResult(new PipeResponse(true, CollectorHeartbeat.Version, heartbeat.LastSampleTs)),
        });
        var host = new PipeHost(CreateInstance, handler, ex => logger.LogDebug(ex, "Pipe client dropped"));

        logger.LogInformation("Serving pipe \\\\.\\pipe\\{PipeName}", PipeProtocol.PipeName);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await host.RunAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                // Typically the name is already taken (another Collector, or a squatter). The Tray then shows
                // "Collector not running", which is the safe answer; keep trying.
                logger.LogError(ex, "Pipe server failed; retrying in {Delay}", RetryDelay);
                try
                {
                    await Task.Delay(RetryDelay, stoppingToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private static NamedPipeServerStream CreateInstance(bool first) =>
        NamedPipeServerStreamAcl.Create(
            PipeProtocol.PipeName,
            PipeDirection.InOut,
            NamedPipeServerStream.MaxAllowedServerInstances,
            PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | (first ? PipeOptions.FirstPipeInstance : PipeOptions.None),
            inBufferSize: 0,
            outBufferSize: 0,
            PipeAcl.Create());
}
