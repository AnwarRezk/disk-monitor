using System.IO.Pipes;
using DiskMon.Collector.Samplers;
using DiskMon.Core.Ipc;
using DiskMon.Core.Security;

namespace DiskMon.Collector.Services;

/// <summary>
/// Serves the named pipe <c>DiskMon</c> (PLAN §5.5, ADR 0002): one JSON request line, one JSON response line.
/// <c>ping</c> answers at once; <c>probe_now</c> answers when the Probe run (started or joined) has been written.
/// </summary>
public sealed class PipeServer(CollectorHeartbeat heartbeat, ProbeSampler probes, ILogger<PipeServer> logger) : BackgroundService
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var handler = new PipeCommandHandler(new Dictionary<string, Func<CancellationToken, Task<PipeResponse>>>
        {
            [PipeProtocol.Ping] = _ => Task.FromResult(new PipeResponse(true, CollectorHeartbeat.Version, heartbeat.LastSampleTs)),
            [PipeProtocol.ProbeNow] = ProbeNowAsync,
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

    private async Task<PipeResponse> ProbeNowAsync(CancellationToken token)
    {
        logger.LogInformation("Probe run requested over the pipe");
        var run = await probes.RunNowAsync(token);
        return new PipeResponse(true, Probes: run.Probes, Errors: run.Errors, DurationMs: run.DurationMs);
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
