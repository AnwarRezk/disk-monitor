using System.Text;
using System.Text.Json;
using DiskMon.Core.Ipc;

namespace DiskMon.Core.Tests;

public sealed class PipeProtocolTests
{
    [Fact]
    public void Requests_and_responses_serialize_to_the_planned_json_lines()
    {
        Assert.Equal("""{"cmd":"ping"}""", PipeProtocol.Serialize(new PipeRequest("ping")));
        Assert.Equal(
            """{"ok":true,"version":"1.0.0+abc","lastSampleTs":1730721600000}""",
            PipeProtocol.Serialize(new PipeResponse(true, "1.0.0+abc", 1_730_721_600_000)));
        Assert.Equal("""{"ok":false,"error":"boom"}""", PipeProtocol.Serialize(PipeResponse.Fail("boom")));
        Assert.Equal("""{"cmd":"probe_now"}""", PipeProtocol.Serialize(new PipeRequest(PipeProtocol.ProbeNow)));
        Assert.Equal(
            """{"ok":true,"probes":52,"errors":1,"durationMs":14200}""",
            PipeProtocol.Serialize(new PipeResponse(true, Probes: 52, Errors: 1, DurationMs: 14_200)));
    }

    [Fact]
    public void Lines_parse_back_to_the_same_records()
    {
        var response = new PipeResponse(true, "1.0.0", null);
        Assert.Equal(response, PipeProtocol.ParseResponse(PipeProtocol.Serialize(response)));
        Assert.Equal(new PipeRequest("ping"), PipeProtocol.ParseRequest("""{"cmd":"ping","extra":1}""", out var error));
        Assert.Null(error);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("""{"cmd":""}""")]
    [InlineData("null")]
    [InlineData("[1]")]
    public void Malformed_requests_are_rejected_with_a_reason(string line)
    {
        Assert.Null(PipeProtocol.ParseRequest(line, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void A_malformed_response_throws_json_exception() =>
        Assert.ThrowsAny<JsonException>(() => PipeProtocol.ParseResponse("garbage"));

    [Fact]
    public async Task Handler_runs_known_commands_and_answers_errors_for_everything_else()
    {
        var pings = 0;
        var handler = new PipeCommandHandler(new Dictionary<string, Func<CancellationToken, Task<PipeResponse>>>
        {
            ["ping"] = _ => Task.FromResult(new PipeResponse(true, "v", ++pings)),
            ["explode"] = _ => throw new InvalidOperationException("kaboom"),
        });
        var token = TestContext.Current.CancellationToken;

        Assert.Equal(new PipeResponse(true, "v", 1), await handler.HandleAsync("""{"cmd":"ping"}""", token));

        var unknown = await handler.HandleAsync("""{"cmd":"format_c"}""", token);
        Assert.False(unknown.Ok);
        Assert.Contains("unknown command", unknown.Error);

        Assert.False((await handler.HandleAsync("{", token)).Ok);
        Assert.False((await handler.HandleAsync(null, token)).Ok);
        Assert.Contains("kaboom", (await handler.HandleAsync("""{"cmd":"explode"}""", token)).Error);

        // Still serving after all of that.
        Assert.Equal(new PipeResponse(true, "v", 2), await handler.HandleAsync("""{"cmd":"ping"}""", token));
    }

    [Fact]
    public async Task Lines_are_read_up_to_the_newline_and_oversized_ones_are_refused()
    {
        var token = TestContext.Current.CancellationToken;
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("{\"cmd\":\"ping\"}\r\nsecond"));

        Assert.Equal("""{"cmd":"ping"}""", await PipeLines.ReadLineAsync(stream, token));
        Assert.Equal("second", await PipeLines.ReadLineAsync(stream, token));
        Assert.Null(await PipeLines.ReadLineAsync(stream, token));

        using var huge = new MemoryStream(new byte[100]);
        await Assert.ThrowsAsync<InvalidDataException>(() => PipeLines.ReadLineAsync(huge, token, maxBytes: 10));
    }
}
