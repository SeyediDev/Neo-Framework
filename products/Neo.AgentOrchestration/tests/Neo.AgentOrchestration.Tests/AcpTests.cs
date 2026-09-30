using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Neo.AgentOrchestration.Infrastructure.AgentProtocols;
using Xunit;

namespace Neo.AgentOrchestration.Tests;
public sealed class AcpTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static AcpCorrelation Correlation => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "test-trace");
    private static Task Reply(Stream stream, int id, object result) => AcpLines.Write(stream, new { jsonrpc = "2.0", id, result }, Ct);
    private static Task Update(Stream stream, string session, object update) => AcpLines.Write(stream,
        new { jsonrpc = "2.0", method = "session/update", @params = new { sessionId = session, update } }, Ct);
    private static async Task Prepare(Stream input)
    {
        await Reply(input, 1, new { protocolVersion = 1, agentCapabilities = new { } });
        await Reply(input, 2, new { sessionId = "session-1" });
    }
    [Fact]
    public async Task Lines_preserve_utf8_and_escape_newlines_without_LSP_headers()
    {
        using var bytes = new MemoryStream();
        await AcpLines.Write(bytes, new { jsonrpc = "2.0", method = "test", @params = "سلام\nجهان" }, Ct);
        Assert.Equal(1, bytes.ToArray().Count(x => x == 10));
        Assert.DoesNotContain("Content-Length", Encoding.UTF8.GetString(bytes.ToArray()));
        bytes.Position = 0;
        Assert.Equal("سلام\nجهان", (await AcpLines.Read(bytes, Ct)).GetProperty("params").GetString());
        using var invalid = new MemoryStream(Encoding.UTF8.GetBytes("{\"jsonrpc\":\"1.0\"}\n"));
        await Assert.ThrowsAsync<InvalidDataException>(() => AcpLines.Read(invalid, Ct));
    }
    [Fact]
    public async Task Version_mismatch_fails_before_session_or_prompt()
    {
        using var input = new MemoryStream(); using var output = new MemoryStream();
        await Reply(input, 1, new { protocolVersion = 2, agentCapabilities = new { } }); input.Position = 0;
        await using var session = new AcpSession(input, output, Correlation);
        await Assert.ThrowsAsync<NotSupportedException>(() => session.Initialize(Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.NewSession(Path.GetTempPath(), Ct));
        Assert.DoesNotContain("session/new", Encoding.UTF8.GetString(output.ToArray()));
    }
    [Fact]
    public async Task Text_turn_correlates_trace_denies_permissions_and_drops_reasoning_and_raw_tools()
    {
        using var input = new MemoryStream(); using var output = new MemoryStream();
        await Prepare(input);
        await Update(input, "session-1", new { sessionUpdate = "agent_thought_chunk", content = new { type = "text", text = "PRIVATE_REASONING" } });
        await Update(input, "session-1", new { sessionUpdate = "tool_call", rawInput = "PRIVATE_TOOL_DATA" });
        await AcpLines.Write(input, new { jsonrpc = "2.0", id = 900, method = "session/request_permission", @params = new { sessionId = "session-1" } }, Ct);
        await AcpLines.Write(input, new { jsonrpc = "2.0", id = 901, method = "fs/write_text_file", @params = new { path = "/unauthorized", content = "NEVER_WRITE" } }, Ct);
        await Update(input, "session-1", new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "Hello " } });
        await Update(input, "session-1", new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "world" } });
        await Reply(input, 3, new { stopReason = "end_turn" }); input.Position = 0;
        var correlation = Correlation; var events = new List<AcpTrace>();
        await using var session = new AcpSession(input, output, correlation, events.Add);
        await session.Initialize(Ct); await session.NewSession(Path.GetTempPath(), Ct);
        var turn = await session.Prompt("Explicitly authorized fixture prompt", true, Ct);
        Assert.Equal("Hello world", turn.Text); Assert.Equal("end_turn", turn.StopReason);
        Assert.Equal(1, turn.DeniedPermissions); Assert.Equal(correlation, turn.Correlation);
        Assert.All(events, e => Assert.Equal(correlation, e.Correlation));
        var trace = JsonSerializer.Serialize(events);
        Assert.DoesNotContain("PRIVATE_", trace); Assert.DoesNotContain("Hello", trace);
        var wire = Encoding.UTF8.GetString(output.ToArray());
        Assert.Contains("\"outcome\":\"cancelled\"", wire); Assert.Contains("-32601", wire);
        Assert.DoesNotContain("NEVER_WRITE", wire);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.Prompt("Again", true, Ct));
    }
    [Fact]
    public async Task No_external_consent_means_no_prompt_sent_and_foreign_updates_fail_closed()
    {
        using var input = new MemoryStream(); using var output = new MemoryStream();
        await Prepare(input);
        await Update(input, "foreign-session", new { sessionUpdate = "agent_message_chunk", content = new { type = "text", text = "foreign" } });
        input.Position = 0;
        await using var session = new AcpSession(input, output, Correlation);
        await session.Initialize(Ct); await session.NewSession(Path.GetTempPath(), Ct);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => session.Prompt("not approved", false, Ct));
        Assert.DoesNotContain("session/prompt", Encoding.UTF8.GetString(output.ToArray()));
        await Assert.ThrowsAsync<InvalidDataException>(() => session.Prompt("approved", true, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.Prompt("retry", true, Ct));
    }
    [Fact]
    public async Task Cancellation_preserves_pending_read_waits_for_ack_and_refuses_late_permissions()
    {
        var inbound = new Pipe(); var outbound = new Pipe();
        var serverWrite = inbound.Writer.AsStream(); var serverRead = outbound.Reader.AsStream();
        await Prepare(serverWrite);
        await using var session = new AcpSession(inbound.Reader.AsStream(), outbound.Writer.AsStream(), Correlation);
        await session.Initialize(Ct); await session.NewSession(Path.GetTempPath(), Ct);
        using var cancel = new CancellationTokenSource();
        var turn = session.Prompt("test cancellation", true, cancel.Token);
        Assert.Equal("initialize", (await AcpLines.Read(serverRead, Ct)).GetProperty("method").GetString());
        Assert.Equal("session/new", (await AcpLines.Read(serverRead, Ct)).GetProperty("method").GetString());
        Assert.Equal("session/prompt", (await AcpLines.Read(serverRead, Ct)).GetProperty("method").GetString());
        cancel.Cancel();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.Equal("session/cancel", (await AcpLines.Read(serverRead, deadline.Token)).GetProperty("method").GetString());
        await AcpLines.Write(serverWrite, new { jsonrpc = "2.0", id = 99, method = "session/request_permission", @params = new { sessionId = "session-1" } }, Ct);
        Assert.Equal("cancelled", (await AcpLines.Read(serverRead, deadline.Token)).GetProperty("result").GetProperty("outcome").GetProperty("outcome").GetString());
        await Reply(serverWrite, 3, new { stopReason = "cancelled" });
        Assert.Equal("cancelled", (await turn).StopReason);
        await inbound.Writer.CompleteAsync(); await outbound.Writer.CompleteAsync();
        await inbound.Reader.CompleteAsync(); await outbound.Reader.CompleteAsync();
    }
    [Theory]
    [InlineData("max_tokens")]
    [InlineData("refusal")]
    public async Task Noncompletion_stop_reasons_are_preserved_not_converted_to_success(string reason)
    {
        using var input = new MemoryStream(); using var output = new MemoryStream();
        await Prepare(input); await Reply(input, 3, new { stopReason = reason }); input.Position = 0;
        await using var session = new AcpSession(input, output, Correlation);
        await session.Initialize(Ct); await session.NewSession(Path.GetTempPath(), Ct);
        Assert.Equal(reason, (await session.Prompt("test", true, Ct)).StopReason);
    }
}
