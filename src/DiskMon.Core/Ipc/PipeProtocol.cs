using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiskMon.Core.Ipc;

/// <summary>A Tray-to-Collector request: one JSON line such as <c>{"cmd":"ping"}</c> (PLAN §5.5).</summary>
public sealed record PipeRequest(string Cmd);

/// <summary>
/// The Collector's answer: one JSON line. <c>ping</c> fills <see cref="Version"/> and <see cref="LastSampleTs"/>
/// (UTC epoch ms of the newest Volume Sample, null before the first). <c>probe_now</c> fills <see cref="Probes"/>
/// (Probes measured), <see cref="Errors"/> (how many recorded an error) and <see cref="DurationMs"/>. A failure sets
/// <see cref="Ok"/> false and <see cref="Error"/>.
/// </summary>
public sealed record PipeResponse(
    bool Ok,
    string? Version = null,
    long? LastSampleTs = null,
    string? Error = null,
    int? Probes = null,
    int? Errors = null,
    long? DurationMs = null)
{
    public static PipeResponse Fail(string error) => new(false, Error: error);
}

/// <summary>The pipe's name, command names and JSON-line encoding, shared by the Collector and the Tray.</summary>
public static class PipeProtocol
{
    /// <summary>The named pipe <c>DiskMon</c> (<c>\\.\pipe\DiskMon</c>).</summary>
    public const string PipeName = "DiskMon";

    public const string Ping = "ping";

    /// <summary>Measure every Probe now; answered when the run's Probe Samples are written.</summary>
    public const string ProbeNow = "probe_now";

    /// <summary>Longest request or response line accepted, in bytes. Real messages are well under 200.</summary>
    public const int MaxLineBytes = 4096;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Lines never reach HTML, so keep "1.0.0+abc" readable instead of escaping '+'.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string Serialize(PipeRequest request) => JsonSerializer.Serialize(request, Json);

    public static string Serialize(PipeResponse response) => JsonSerializer.Serialize(response, Json);

    /// <summary>Parses a request line, or returns null with <paramref name="error"/> set when it is not one.</summary>
    public static PipeRequest? ParseRequest(string line, out string? error)
    {
        try
        {
            var request = JsonSerializer.Deserialize<PipeRequest>(line, Json);
            if (string.IsNullOrWhiteSpace(request?.Cmd))
            {
                error = "missing \"cmd\"";
                return null;
            }

            error = null;
            return request;
        }
        catch (JsonException)
        {
            error = "request is not valid JSON";
            return null;
        }
    }

    /// <summary>Parses a response line. Throws <see cref="JsonException"/> when it is not one.</summary>
    public static PipeResponse ParseResponse(string line) =>
        JsonSerializer.Deserialize<PipeResponse>(line, Json) ?? throw new JsonException("empty response");
}
