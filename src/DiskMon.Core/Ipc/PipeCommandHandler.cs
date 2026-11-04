namespace DiskMon.Core.Ipc;

/// <summary>
/// Turns one request line into one response. Each command maps to a handler; an unknown command, a malformed line
/// or a failing handler becomes an error response, never an exception, so the server keeps serving.
/// </summary>
public sealed class PipeCommandHandler(IReadOnlyDictionary<string, Func<CancellationToken, Task<PipeResponse>>> commands)
{
    public async Task<PipeResponse> HandleAsync(string? line, CancellationToken token)
    {
        if (line is null)
            return PipeResponse.Fail("empty request");

        var request = PipeProtocol.ParseRequest(line, out var error);
        if (request is null)
            return PipeResponse.Fail(error!);

        if (!commands.TryGetValue(request.Cmd, out var command))
            return PipeResponse.Fail($"unknown command \"{request.Cmd}\"");

        try
        {
            return await command(token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return PipeResponse.Fail($"{request.Cmd} failed: {ex.Message}");
        }
    }
}
