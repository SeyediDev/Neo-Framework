using System.Text;
using System.Text.Json;

namespace Neo.AgentOrchestration.Infrastructure.AgentProtocols;

public sealed record AcpCorrelation(Guid OrganizationId, Guid WorkspaceId, Guid RunId, Guid WorkItemId, string TraceId)
{
    public void Validate()
    {
        if (OrganizationId == Guid.Empty || WorkspaceId == Guid.Empty || RunId == Guid.Empty || WorkItemId == Guid.Empty ||
            string.IsNullOrWhiteSpace(TraceId) || TraceId.Length > 128 || TraceId.Any(char.IsControl))
            throw new ArgumentException("Invalid execution correlation.");
    }
}
public sealed record AcpTrace(AcpCorrelation Correlation, string SessionId, string Kind);
public sealed record AcpTurn(AcpCorrelation Correlation, string SessionId, string StopReason, string Text, int DeniedPermissions);

// Opt-in protocol building block, not a registered run provider or an agent launcher.
// One session/turn per instance. Caller owns streams, identity, credentials and process isolation.
public sealed class AcpSession(Stream input, Stream output, AcpCorrelation correlation,
    Action<AcpTrace>? trace = null) : IAsyncDisposable
{
    private readonly CancellationTokenSource lifetime = new();
    private bool attempted, initialized, consumed, closed;
    private int sequence, denied;
    private string? sessionId;
    private readonly StringBuilder text = new();
    public JsonElement AgentCapabilities { get; private set; }

    public async Task Initialize(CancellationToken ct)
    {
        correlation.Validate();
        if (attempted || closed) throw new InvalidOperationException("Initialization is single-use.");
        attempted = true;
        var response = await Request("initialize", new
        {
            protocolVersion = 1,
            clientCapabilities = new { fs = new { readTextFile = false, writeTextFile = false }, terminal = false },
            clientInfo = new { name = "Neo.AgentOrchestration", version = "0.1" }
        }, ct);
        if (response.GetProperty("protocolVersion").GetInt32() != 1)
        { closed = true; throw new NotSupportedException("ACP protocol version unavailable."); }
        AgentCapabilities = response.GetProperty("agentCapabilities").Clone();
        initialized = true;
    }

    public async Task<string> NewSession(string approvedWorkspace, CancellationToken ct)
    {
        if (!initialized || sessionId is not null || closed) throw new InvalidOperationException("Session cannot be created.");
        var root = LanguageTools.WorkspacePath.Root(approvedWorkspace);
        var response = await Request("session/new", new
        {
            cwd = root, mcpServers = Array.Empty<object>(),
            _meta = new Dictionary<string, object>
            {
                ["neo-framework.dev/correlation"] = correlation
            }
        }, ct);
        sessionId = response.GetProperty("sessionId").GetString();
        if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > 512 || sessionId.Any(char.IsControl))
        { closed = true; throw new InvalidDataException("Invalid ACP session identifier."); }
        Emit("session-created"); return sessionId;
    }

    public async Task<AcpTurn> Prompt(string message, bool allowExternalExecution, CancellationToken ct)
    {
        if (!allowExternalExecution) throw new UnauthorizedAccessException("Explicit external execution consent required.");
        if (sessionId is null || consumed || closed) throw new InvalidOperationException("Session not ready for a turn.");
        if (string.IsNullOrWhiteSpace(message) || message.Length > 65536) throw new ArgumentException("Invalid prompt.");
        consumed = true;
        var result = await Request("session/prompt", new { sessionId, prompt = new[] { new { type = "text", text = message } } }, ct);
        var reason = result.GetProperty("stopReason").GetString();
        if (reason is not ("end_turn" or "max_tokens" or "max_turn_requests" or "refusal" or "cancelled"))
            throw new InvalidDataException("Unknown ACP stop reason.");
        Emit("turn-" + reason);
        // end_turn means only that a prompt ended. It is never task completion or passing evidence.
        return new(correlation, sessionId, reason, text.ToString(), denied);
    }

    private void Emit(string kind)
    { if (sessionId is not null) trace?.Invoke(new(correlation, sessionId, kind)); }
    private async Task<JsonElement> Request(string method, object parameters, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var id = ++sequence;
        using var cancellationGrace = new CancellationTokenSource();
        var cancelling = false;
        try
        {
            await AcpLines.Write(output, new { jsonrpc = "2.0", id, method, @params = parameters }, ct);
            while (true)
            {
                // Keep the same read alive when caller cancellation arrives; do not lose partial lines.
                var pending = AcpLines.Read(input, lifetime.Token);
                JsonElement message;
                try { message = await pending.WaitAsync(cancelling ? cancellationGrace.Token : ct); }
                catch (OperationCanceledException) when (!cancelling && ct.IsCancellationRequested && method == "session/prompt")
                {
                    cancelling = true;
                    cancellationGrace.CancelAfter(TimeSpan.FromSeconds(5));
                    await AcpLines.Write(output, new { jsonrpc = "2.0", method = "session/cancel", @params = new { sessionId } }, cancellationGrace.Token);
                    Emit("cancel-requested");
                    message = await pending.WaitAsync(cancellationGrace.Token);
                }
                var active = cancelling ? cancellationGrace.Token : ct;
                if (!message.TryGetProperty("method", out _) && message.TryGetProperty("id", out var responseId) &&
                    responseId.ValueKind == JsonValueKind.Number && responseId.GetInt32() == id)
                {
                    if (message.TryGetProperty("error", out _)) throw new InvalidDataException("ACP request failed.");
                    var result = message.GetProperty("result").Clone();
                    if (cancelling && result.GetProperty("stopReason").GetString() != "cancelled")
                        throw new InvalidDataException("Agent did not confirm cancellation.");
                    return result;
                }
                await Handle(message, active);
            }
        }
        catch
        {
            closed = true;
            await lifetime.CancelAsync();
            throw; // No receipt: caller must reconcile, not release a task/run as successfully cancelled.
        }
    }

    private async Task Handle(JsonElement message, CancellationToken ct)
    {
        if (!message.TryGetProperty("method", out var method)) return;
        if (message.TryGetProperty("id", out var id))
        {
            if (method.GetString() == "session/request_permission")
            {
                var data = message.GetProperty("params");
                if (sessionId is null || data.GetProperty("sessionId").GetString() != sessionId)
                    throw new InvalidDataException("Foreign ACP permission request.");
                denied++; Emit("permission-denied");
                await AcpLines.Write(output, new { jsonrpc = "2.0", id, result = new { outcome = new { outcome = "cancelled" } } }, ct);
            }
            else
                await AcpLines.Write(output, new { jsonrpc = "2.0", id, error = new { code = -32601, message = "Client capability not available." } }, ct);
            return;
        }
        if (method.GetString() != "session/update") return;
        var parameters = message.GetProperty("params");
        if (sessionId is null || parameters.GetProperty("sessionId").GetString() != sessionId)
            throw new InvalidDataException("Foreign ACP update.");
        var update = parameters.GetProperty("update");
        var kind = update.GetProperty("sessionUpdate").GetString();
        // Do not expose or retain reasoning, raw tool payloads, file diffs or credentials in trace.
        if (kind == "agent_message_chunk")
        {
            var content = update.GetProperty("content");
            if (content.GetProperty("type").GetString() != "text") throw new NotSupportedException("Only text output supported.");
            var chunk = content.GetProperty("text").GetString() ?? "";
            if (text.Length + chunk.Length > 65536) throw new InvalidDataException("ACP output limit exceeded.");
            text.Append(chunk); Emit("message-chunk");
        }
        else if (kind is "tool_call" or "tool_call_update" or "plan" or "usage_update")
            Emit(kind);
    }

    public async ValueTask DisposeAsync()
    {
        closed = true;
        await lifetime.CancelAsync();
        lifetime.Dispose();
        // ACP has no invented LSP shutdown/exit method. Caller closes its owned transport/process.
    }
}

public static class AcpLines
{
    public const int MaximumBytes = 1024 * 1024;
    public static async Task Write(Stream stream, object value, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("ACP message too large.");
        await stream.WriteAsync(bytes, ct); await stream.WriteAsync(new byte[] { 10 }, ct); await stream.FlushAsync(ct);
    }
    public static async Task<JsonElement> Read(Stream stream, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var one = new byte[1];
        while (true)
        {
            await stream.ReadExactlyAsync(one, ct);
            if (one[0] == 10) break;
            if (buffer.Length >= MaximumBytes) throw new InvalidDataException("ACP message too large.");
            buffer.WriteByte(one[0]);
        }
        using var json = JsonDocument.Parse(buffer.ToArray());
        if (json.RootElement.GetProperty("jsonrpc").GetString() != "2.0") throw new InvalidDataException("Invalid ACP JSON-RPC version.");
        return json.RootElement.Clone();
    }
}
