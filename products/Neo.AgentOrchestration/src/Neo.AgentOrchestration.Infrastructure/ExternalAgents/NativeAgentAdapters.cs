using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Neo.AgentOrchestration.Application.ExternalAgents;
using Neo.AgentOrchestration.Infrastructure.Runs;

namespace Neo.AgentOrchestration.Infrastructure.ExternalAgents;

// One server-owned binding per project/sandbox lane. A task cannot provide an
// endpoint, credential, arbitrary agent, model route or host directory.
public sealed record NativeAgentBinding(string Key, ExternalAgentEngine Engine, Guid OrganizationId,
    Guid WorkspaceId, Guid ProjectId, Uri Endpoint, string SecretRef, string ModelProvider,
    string ModelId, string? Username, bool OpenCodeDisjointTokenAccounting, bool OpenCodeOmittedStatusIsIdle,
    bool OpenCodeCursorPagination = false)
{
    // Keep existing opt-out fingerprints stable. A reviewed cursor contract
    // changes interpretation and must invalidate old opt-in reservations.
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
    {
        Key, Engine, OrganizationId, WorkspaceId, ProjectId, Endpoint, SecretRef,
        ModelProvider, ModelId, Username, OpenCodeDisjointTokenAccounting, OpenCodeOmittedStatusIsIdle
    }) + (OpenCodeCursorPagination ? "|opencode-cursor-v1" : ""))));
}

public sealed class NativeAgentAdapterFactory(IConfiguration configuration, IHostEnvironment environment,
    IHarnessSecrets secrets, IHttpClientFactory clients)
{
    public IExternalAgentAdapter Create(string key, ExternalAgentScope scope)
    {
        try
        {
            if (!configuration.GetValue<bool>("NativeAgents:Enabled") ||
                !Regex.IsMatch(key, @"\A[a-z0-9][a-z0-9_-]{0,39}\z", RegexOptions.CultureInvariant))
                throw new ExternalAgentException("native-agent-disabled");
            var section = configuration.GetSection("NativeAgents:Connections:" + key);
            if (!section.GetValue<bool>("Enabled")) throw new ExternalAgentException("native-agent-disabled");
            var engine = section["Engine"] switch
            {
                "hermes" => ExternalAgentEngine.Hermes,
                "opencode" => ExternalAgentEngine.OpenCode,
                _ => throw new ExternalAgentException("native-agent-configuration")
            };
            var local = configuration.GetValue<bool>("NativeAgents:AllowLoopbackHttp") &&
                (environment.IsDevelopment() || environment.IsEnvironment("Testing"));
            if (!Uri.TryCreate(section["Endpoint"], UriKind.Absolute, out var endpoint) ||
                (endpoint.Scheme != "https" && !(local && endpoint.Scheme == "http" && endpoint.IsLoopback)) ||
                endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0)
                throw new ExternalAgentException("native-agent-configuration");
            if (!endpoint.AbsolutePath.EndsWith('/')) endpoint = new Uri(endpoint.AbsoluteUri + "/");
            var username = engine == ExternalAgentEngine.OpenCode ? Value(section["Username"] ?? "opencode") : null;
            if (username?.Contains(':') == true) throw new ExternalAgentException("native-agent-configuration");
            var binding = new NativeAgentBinding(key, engine, section.GetValue<Guid>("OrganizationId"),
                section.GetValue<Guid>("WorkspaceId"), section.GetValue<Guid>("ProjectId"), endpoint,
                section["SecretRef"] ?? "", Value(section["ModelProvider"]), Value(section["ModelId"]), username,
                section.GetValue<bool>("OpenCodeDisjointTokenAccounting"), section.GetValue<bool>("OpenCodeOmittedStatusIsIdle"),
                section.GetValue<bool>("OpenCodeCursorPagination"));
            RequireScope(binding, scope);
            if (!Regex.IsMatch(binding.SecretRef, @"\Aenv:[A-Z][A-Z0-9_]{0,100}\z", RegexOptions.CultureInvariant))
                throw new ExternalAgentException("native-agent-configuration");
            var secret = secrets.Resolve(binding.SecretRef);
            if (secret.Length is < 32 or > 1024 || secret.Any(char.IsWhiteSpace) || secret.Any(char.IsControl))
                throw new ExternalAgentException("native-agent-configuration");
            return new NativeAgentAdapter(binding, secret, clients.CreateClient("fanasa-native-agent"));
        }
        catch (ExternalAgentException) { throw; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
        { throw new ExternalAgentException("native-agent-configuration"); }
    }
    private static string Value(string? value) => string.IsNullOrWhiteSpace(value) || value.Length > 200 ||
        value.Any(char.IsControl) ? throw new ExternalAgentException("native-agent-configuration") : value;
    internal static void RequireScope(NativeAgentBinding binding, ExternalAgentScope scope)
    {
        if (scope.RunId == Guid.Empty || scope.ProjectId == Guid.Empty || scope.OrganizationId == Guid.Empty ||
            scope.WorkspaceId == Guid.Empty || scope.OrganizationId != binding.OrganizationId ||
            scope.WorkspaceId != binding.WorkspaceId || scope.ProjectId != binding.ProjectId)
            throw new ExternalAgentException("native-agent-scope");
    }
}

public static class NativeAgentRegistration
{
    // Explicit gateway composition; private ingress/opted-in Worker only.
    public static IServiceCollection AddNativeAgentAdapters(this IServiceCollection services)
    {
        services.AddSingleton<NativeAgentAdapterFactory>();
        services.AddHttpClient("fanasa-native-agent", c => c.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
            .RemoveAllLoggers();
        return services;
    }
}

internal sealed class NativeAgentAdapter(NativeAgentBinding binding, string transportSecret, HttpClient http) : IExternalAgentAdapter
{
    public ExternalAgentEngine Engine => binding.Engine;
    public string BindingFingerprint => binding.Fingerprint;
    private const int MaxBytes = 1024 * 1024;

    public async Task<ExternalAgentHandle> PrepareAsync(ExternalAgentInput input, CancellationToken ct)
    {
        RequireInput(input);
        var handle = new ExternalAgentHandle(input.Scope, Engine, binding.Fingerprint, "msg_" + input.Scope.RunId.ToString("N"));
        if (Engine == ExternalAgentEngine.Hermes) return handle;
        // No documented session-create idempotency. An uncertain reply requires
        // reconciliation, not blind recreation. This does not start inference.
        using var session = await SendAsync(HttpMethod.Post, "session", new { title = "fanasa-run-" + input.Scope.RunId.ToString("N") }, ct);
        return handle with { NativeId = Identifier(Text(session.RootElement, "id")) };
    }

    public async Task<ExternalAgentHandle> SubmitAsync(ExternalAgentInput input, ExternalAgentHandle prepared, CancellationToken ct)
    {
        RequireInput(input); RequireHandle(prepared, requireNative: Engine == ExternalAgentEngine.OpenCode);
        if (input.Scope != prepared.Scope || Engine == ExternalAgentEngine.Hermes && prepared.NativeId is not null)
            throw new ExternalAgentException("native-agent-scope");
        if (Engine == ExternalAgentEngine.Hermes)
        {
            using var result = await SendAsync(HttpMethod.Post, "v1/runs", new
            {
                input = input.Prompt, instructions = input.Instructions, model = binding.ModelId,
                provider = binding.ModelProvider, session_id = "fanasa-run-" + input.Scope.RunId.ToString("N")
            }, ct, idempotencyKey: input.Scope.RunId.ToString("N"), memoryScope: MemoryScope(input.Scope));
            return prepared with { NativeId = Identifier(Text(result.RootElement, "run_id")) };
        }
        using var ignored = await SendAsync(HttpMethod.Post, $"session/{prepared.NativeId}/prompt_async", new
        {
            messageID = prepared.PromptId, model = new { providerID = binding.ModelProvider, modelID = binding.ModelId },
            system = input.Instructions, parts = new[] { new { type = "text", text = input.Prompt } }
        }, ct, expectEmpty: true);
        return prepared;
    }

    public async Task<ExternalAgentObservation> ObserveAsync(ExternalAgentHandle handle, CancellationToken ct)
    {
        RequireHandle(handle, requireNative: true);
        if (Engine == ExternalAgentEngine.Hermes)
        {
            using var doc = await SendAsync(HttpMethod.Get, "v1/runs/" + handle.NativeId, null, ct);
            var root = doc.RootElement;
            if (Text(root, "run_id") != handle.NativeId) throw new ExternalAgentException("native-agent-response-scope");
            var state = Text(root, "status") switch
            {
                "queued" => ExternalAgentState.Queued,
                "started" or "running" => ExternalAgentState.Running,
                "waiting_for_approval" => ExternalAgentState.AwaitingApproval,
                "stopping" => ExternalAgentState.StopRequested,
                "completed" => ExternalAgentState.Completed,
                "failed" => ExternalAgentState.Failed,
                "cancelled" => ExternalAgentState.Cancelled,
                "interrupted" => ExternalAgentState.Interrupted,
                _ => ExternalAgentState.Unknown
            };
            if (state is not (ExternalAgentState.Completed or ExternalAgentState.Failed or ExternalAgentState.Cancelled or ExternalAgentState.Interrupted) ||
                !root.TryGetProperty("usage", out var usage)) return new(state, []);
            var runtime = Property(root, "runtime");
            var provider = Bounded(Text(runtime, "provider")); var model = Bounded(Text(runtime, "model"));
            // Requested model is not an accounting identity: require actual runtime.
            if (provider is null || model is null) return new(state, []);
            var input = Count(usage, "input_tokens"); var output = Count(usage, "output_tokens");
            var cached = Count(usage, "cache_read_tokens"); var reasoning = Count(usage, "reasoning_tokens");
            RequireSubsets(input, output, cached, reasoning);
            _ = Sum(input, output);
            return new(state, [new("hermes:" + handle.NativeId, provider, model, input, output, cached, reasoning)]);
        }

        var messages = await ReadOpenCodeMessagesAsync(handle, ct);
        if (messages is null) return new(ExternalAgentState.Unknown, []);
        // Sample native status after the complete bounded transcript, not before
        // potentially multiple pages. Idle still isn't executor-exit evidence.
        using var statuses = await SendAsync(HttpMethod.Get, "session/status", null, ct);
        if (statuses.RootElement.ValueKind != JsonValueKind.Object) throw new ExternalAgentException("native-agent-protocol");
        var status = Text(Property(statuses.RootElement, handle.NativeId!), "type");
        // Reviewed source removes idle sessions from its status map. Only use
        // that interpretation with explicit installed-version opt-in, and still
        // require a correlated completed final message below.
        if (binding.OpenCodeOmittedStatusIsIdle && !statuses.RootElement.TryGetProperty(handle.NativeId!, out _)) status = "idle";
        var reports = new List<ExternalAgentUsage>(); var failed = false; var aborted = false; var finished = false; var pending = false;
        var messageIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in messages)
        {
            var info = Property(row, "info");
            if (Text(info, "parentID") != handle.PromptId || Text(info, "role") != "assistant") continue;
            if (Text(info, "sessionID") != handle.NativeId) throw new ExternalAgentException("native-agent-response-scope");
            if (!messageIds.Add(Identifier(Text(info, "id")))) throw new ExternalAgentException("native-agent-duplicate-message");
            if (Count(Property(info, "time"), "completed") is null) { pending = true; continue; }
            var error = Property(info, "error");
            if (error.ValueKind == JsonValueKind.Object)
            { failed = true; aborted |= Text(error, "name") == "MessageAbortedError"; }
            finished |= Text(info, "finish") is "stop" or "end_turn";
            // Source-reviewed opt-in. OpenCode categories are DISJOINT in the
            // reviewed revision: reconstruct inclusive input/output once only.
            if (!binding.OpenCodeDisjointTokenAccounting) continue;
            var tokens = Property(info, "tokens"); var cache = Property(tokens, "cache");
            var rawInput = Count(tokens, "input"); var read = Count(cache, "read"); var write = Count(cache, "write");
            var rawOutput = Count(tokens, "output"); var reasoning = Count(tokens, "reasoning");
            var input = Sum(rawInput, read, write); var output = Sum(rawOutput, reasoning);
            _ = Sum(input, output);
            var provider = Bounded(Text(info, "providerID")); var model = Bounded(Text(info, "modelID"));
            if (provider is null || model is null) continue;
            reports.Add(new("opencode:" + Identifier(Text(info, "id")), provider, model, input, output, read, reasoning));
        }
        // Idle alone is not success, nor proof that executor-backed tools stopped.
        var observed = status is "busy" or "retry" ? ExternalAgentState.Running
            : status != "idle" || pending ? ExternalAgentState.Unknown
            : aborted ? ExternalAgentState.Cancelled : failed ? ExternalAgentState.Failed
            : finished ? ExternalAgentState.Completed : ExternalAgentState.Unknown;
        return new(observed, reports);
    }

    public async Task RequestStopAsync(ExternalAgentHandle handle, CancellationToken ct)
    {
        RequireHandle(handle, requireNative: true);
        using var ignored = await SendAsync(HttpMethod.Post, Engine == ExternalAgentEngine.Hermes
            ? $"v1/runs/{handle.NativeId}/stop" : $"session/{handle.NativeId}/abort", new { }, ct);
        // Never manufacture a terminal state from acknowledgement. Reconcile next.
    }

    private async Task<IReadOnlyList<JsonElement>?> ReadOpenCodeMessagesAsync(ExternalAgentHandle handle, CancellationToken ct)
    {
        const int pageSize = 100, maxPages = 10, maxTotalBytes = 4 * MaxBytes;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        var rows = new List<JsonElement>(); var bytes = 0L; string? cursor = null;
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 0; page < maxPages; page++)
        {
            string? next = null; var hasCursor = false;
            var path = $"session/{handle.NativeId}/message?limit={pageSize}";
            if (cursor is not null) path += "&before=" + Uri.EscapeDataString(cursor);
            using var document = await SendAsync(HttpMethod.Get, path, null, deadline.Token,
                inspect: (headers, length) =>
                {
                    bytes += length;
                    hasCursor = headers.Contains("X-Next-Cursor");
                    if (!binding.OpenCodeCursorPagination || !headers.TryGetValues("X-Next-Cursor", out var values)) return;
                    var tokens = values.ToArray();
                    if (tokens.Length != 1 || string.IsNullOrWhiteSpace(tokens[0]) || tokens[0].Length > 4096 ||
                        tokens[0].Any(char.IsWhiteSpace) || tokens[0].Any(char.IsControl) ||
                        tokens[0].Contains(transportSecret, StringComparison.Ordinal))
                        throw new ExternalAgentException("native-agent-cursor-invalid");
                    next = tokens[0];
                });
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new ExternalAgentException("native-agent-protocol");
            var count = document.RootElement.GetArrayLength();
            if (!binding.OpenCodeCursorPagination)
                return count >= pageSize || hasCursor ? null : document.RootElement.EnumerateArray().Select(x => x.Clone()).ToArray();
            if (count > pageSize || next is not null && count == 0)
                throw new ExternalAgentException("native-agent-cursor-invalid");
            if (bytes > maxTotalBytes) return null;
            foreach (var row in document.RootElement.EnumerateArray())
            {
                var info = Property(row, "info");
                if (Text(info, "sessionID") != handle.NativeId)
                    throw new ExternalAgentException("native-agent-response-scope");
                if (!ids.Add(Identifier(Text(info, "id"))))
                    throw new ExternalAgentException("native-agent-duplicate-message");
                rows.Add(row.Clone());
            }
            if (next is null) return rows;
            if (!cursors.Add(next)) throw new ExternalAgentException("native-agent-cursor-cycle");
            cursor = next; // Never follow native Link URLs or change configured authority.
        }
        // No partial usage or fabricated terminal state at a traversal budget.
        return null;
    }

    private async Task<JsonDocument> SendAsync(HttpMethod method, string path, object? body, CancellationToken ct,
        bool expectEmpty = false, string? idempotencyKey = null, string? memoryScope = null,
        Action<HttpResponseHeaders, long>? inspect = null)
    {
        var write = method != HttpMethod.Get;
        using var request = new HttpRequestMessage(method, new Uri(binding.Endpoint, path));
        request.Headers.Authorization = Engine == ExternalAgentEngine.Hermes
            ? new AuthenticationHeaderValue("Bearer", transportSecret)
            : new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(binding.Username + ":" + transportSecret)));
        if (idempotencyKey is not null) request.Headers.Add("Idempotency-Key", idempotencyKey);
        if (memoryScope is not null) request.Headers.Add("X-Hermes-Session-Key", memoryScope);
        if (body is not null) request.Content = JsonContent.Create(body);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) throw new ExternalAgentException("native-agent-http-" + (int)response.StatusCode, write);
            if (Engine == ExternalAgentEngine.Hermes && path == "v1/runs" && response.StatusCode != HttpStatusCode.Accepted ||
                expectEmpty && response.StatusCode != HttpStatusCode.NoContent)
                throw new ExternalAgentException("native-agent-protocol", write);
            if (expectEmpty) return JsonDocument.Parse("{}");
            if (response.Content.Headers.ContentLength is > MaxBytes) throw new ExternalAgentException("native-agent-response-limit", write);
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream(); var chunk = new byte[8192];
            for (int read; (read = await stream.ReadAsync(chunk, ct)) != 0;)
            {
                if (buffer.Length + read > MaxBytes) throw new ExternalAgentException("native-agent-response-limit", write);
                buffer.Write(chunk, 0, read);
            }
            inspect?.Invoke(response.Headers, buffer.Length);
            return JsonDocument.Parse(buffer.ToArray());
        }
        catch (ExternalAgentException) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or IOException)
        { throw new ExternalAgentException("native-agent-transport-or-protocol", write); }
    }

    private void RequireInput(ExternalAgentInput input)
    {
        NativeAgentAdapterFactory.RequireScope(binding, input.Scope);
        if (string.IsNullOrWhiteSpace(input.Prompt) || Encoding.UTF8.GetByteCount(input.Prompt) > 64 * 1024 ||
            Encoding.UTF8.GetByteCount(input.Instructions ?? "") > 16 * 1024)
            throw new ExternalAgentException("native-agent-input-limit");
    }
    private void RequireHandle(ExternalAgentHandle handle, bool requireNative)
    {
        NativeAgentAdapterFactory.RequireScope(binding, handle.Scope);
        if (handle.Engine != Engine || handle.BindingFingerprint != binding.Fingerprint ||
            handle.PromptId != "msg_" + handle.Scope.RunId.ToString("N")) throw new ExternalAgentException("native-agent-binding-changed");
        if (requireNative) Identifier(handle.NativeId);
    }
    private static string MemoryScope(ExternalAgentScope scope) =>
        $"fanasa:{scope.OrganizationId:N}:{scope.WorkspaceId:N}:{scope.ProjectId:N}:{scope.RunId:N}";
    private string Identifier(string? value) => value is not null && !value.Contains(transportSecret, StringComparison.Ordinal) &&
        Regex.IsMatch(value, @"\A[A-Za-z0-9_-]{1,100}\z", RegexOptions.CultureInvariant)
            ? value : throw new ExternalAgentException("native-agent-identifier", true);
    private string? Bounded(string? value) => value is not null && value.Length is > 0 and <= 200 &&
        !value.Any(char.IsControl) && !value.Contains(transportSecret, StringComparison.Ordinal) ? value : null;
    private static JsonElement Property(JsonElement parent, string name) => parent.ValueKind == JsonValueKind.Object &&
        parent.TryGetProperty(name, out var child) ? child : default;
    private static string? Text(JsonElement parent, string name) => Property(parent, name) is var child &&
        child.ValueKind == JsonValueKind.String ? child.GetString() : null;
    private static long? Count(JsonElement parent, string name)
    {
        var value = Property(parent, name);
        if (value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var count) || count < 0)
            throw new ExternalAgentException("native-agent-usage-invalid");
        return count;
    }
    private static long? Sum(params long?[] counts)
    {
        if (counts.Any(c => c is null)) return null;
        try { return counts.Aggregate(0L, (a, c) => checked(a + c!.Value)); }
        catch (OverflowException) { throw new ExternalAgentException("native-agent-usage-invalid"); }
    }
    private static void RequireSubsets(long? input, long? output, long? cached, long? reasoning)
    {
        if (input.HasValue && cached > input || output.HasValue && reasoning > output)
            throw new ExternalAgentException("native-agent-usage-invalid");
    }
}
