using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Neo.AgentOrchestration.Application.ExternalAgents;
using Neo.AgentOrchestration.Application.ExternalExecution;
using Neo.AgentOrchestration.Contracts;

namespace Neo.AgentOrchestration.Infrastructure.ExternalAgents;

// This is Fanasa's private trusted CLI bridge, not a fictitious Codex HTTP API.
// Task/model prose never supplies repository, revision, runtime, command or path.
public sealed record CodexBridgePolicy(string SandboxId, string RepositoryUrl, string Revision,
    string ImageDigest, string RuntimeVersion, string AcceptanceSha256)
{
    [JsonIgnore]
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this))));
    public static CodexBridgePolicy Read(IConfiguration configuration, string key)
    {
        var c = configuration.GetSection("AgentGateway:Bindings:" + key);
        var version = configuration["NativeAgents:Connections:" + key + ":CodexRuntimeVersion"] ?? "";
        var policy = new CodexBridgePolicy(c["SandboxId"] ?? "", c["RepositoryUrl"] ?? "", c["Revision"] ?? "", c["ImageDigest"] ?? "", version,
            c["CodexAcceptanceSha256"] ?? "");
        if (!configuration.GetValue<bool>("AgentGateway:CodexBridgeEnabled") ||
            !Regex.IsMatch(policy.SandboxId, @"\A[a-z0-9][a-z0-9_-]{0,99}\z") ||
            !Regex.IsMatch(policy.Revision, @"\A[0-9a-f]{40,64}\z") ||
            !Regex.IsMatch(policy.ImageDigest, @"\Asha256:[0-9a-f]{64}\z") ||
            !Regex.IsMatch(policy.AcceptanceSha256, @"\A[0-9a-f]{64}\z") ||
            !Regex.IsMatch(version, @"\A[0-9]+\.[0-9]+\.[0-9]+\z") ||
            !Uri.TryCreate(policy.RepositoryUrl, UriKind.Absolute, out var repo) || repo.Scheme != "https" ||
            repo.UserInfo.Length != 0 || repo.Query.Length != 0 || repo.Fragment.Length != 0)
            throw new ExternalAgentException("codex-bridge-policy-unavailable");
        return policy;
    }
}

public sealed record CodexBridgeUsage(string TurnId, string Provider, string Model,
    long? InputTokens, long? OutputTokens, long? CachedInputTokens, long? ReasoningTokens);
public sealed record CodexBridgeReceipt(string Protocol, ExternalAgentScope Scope, string NativeId,
    string BindingFingerprint, CodexBridgePolicy Policy, string State, bool ExecutorExited,
    int? ExitCode, bool ModelCompleted, string? ContainerId, CodexBridgeUsage? Usage,
    CodexBridgeValidation? Validation);
public sealed record CodexBridgeValidation(bool Passed, int TestCount, int ChangedFileCount,
    string ChangeManifestSha256, string TestOutputSha256, string SnapshotSha256);

public sealed class CodexBridgeAdapter(NativeAgentBinding binding, CodexBridgePolicy policy,
    string transportSecret, HttpClient http) : IExternalAgentAdapter
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { MaxDepth = 24, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };
    public ExternalAgentEngine Engine => ExternalAgentEngine.Codex;
    public string BindingFingerprint => binding.Fingerprint;
    private static string NativeId(ExternalAgentScope scope) => "codex_" + scope.RunId.ToString("N");

    public Task<ExternalAgentHandle> PrepareAsync(ExternalAgentInput input, CancellationToken ct)
    {
        RequireInput(input);
        // Deterministic preparation has no side effect; persisted by the journal
        // BEFORE submit. A failed/uncertain submit is never replayed here.
        return Task.FromResult(new ExternalAgentHandle(input.Scope, Engine, BindingFingerprint,
            "msg_" + input.Scope.RunId.ToString("N"), NativeId(input.Scope)));
    }
    public async Task<ExternalAgentHandle> SubmitAsync(ExternalAgentInput input, ExternalAgentHandle prepared, CancellationToken ct)
    {
        RequireInput(input); RequireHandle(prepared);
        if (input.Scope != prepared.Scope) throw new ExternalAgentException("native-agent-scope");
        var receipt = await SendAsync(HttpMethod.Post, "v1/codex/runs", new
        {
            protocol = "fanasa-codex/v1", scope = input.Scope, nativeId = prepared.NativeId,
            bindingFingerprint = BindingFingerprint, policy, prompt = input.Prompt,
            instructions = input.Instructions, provider = binding.ModelProvider, model = binding.ModelId
        }, prepared, ct);
        if (receipt.State is not ("Running" or "Completed" or "Failed" or "Cancelled"))
            throw new ExternalAgentException("codex-submit-unconfirmed", true);
        return prepared;
    }
    public async Task<ExternalAgentObservation> ObserveAsync(ExternalAgentHandle handle, CancellationToken ct)
    {
        RequireHandle(handle);
        var receipt = await SendAsync(HttpMethod.Get, "v1/codex/runs/" + handle.NativeId, null, handle, ct);
        var state = receipt.State switch
        {
            "Running" => ExternalAgentState.Running,
            "StopRequested" => ExternalAgentState.StopRequested,
            "Completed" when receipt.ExecutorExited && receipt.ExitCode == 0 && receipt.ModelCompleted => ExternalAgentState.Completed,
            "Failed" when receipt.ExecutorExited => ExternalAgentState.Failed,
            "Cancelled" when receipt.ExecutorExited => ExternalAgentState.Cancelled,
            _ => ExternalAgentState.Unknown
        };
        // Only the trusted CLI JSONL collector's immutable terminal report.
        // Counters are inclusive; cache/reasoning are subsets, never added again.
        if (!receipt.ExecutorExited || receipt.Usage is null) return new(state, []);
        var u = receipt.Usage;
        RequireUsage(u);
        return new(state, [new("codex:" + handle.NativeId + ":" + u.TurnId, u.Provider, u.Model,
            u.InputTokens, u.OutputTokens, u.CachedInputTokens, u.ReasoningTokens)]);
    }
    public async Task RequestStopAsync(ExternalAgentHandle handle, CancellationToken ct)
    {
        RequireHandle(handle);
        _ = await SendAsync(HttpMethod.Post, "v1/codex/runs/" + handle.NativeId + "/stop", new { }, handle, ct);
        // Stop response is not executor-exit or cancellation proof.
    }
    public async Task VerifyReadyAsync(GatewayBinding gateway, CancellationToken ct)
    {
        RequireGateway(gateway);
        var handle = new ExternalAgentHandle(gateway.Scope, Engine, BindingFingerprint,
            "msg_" + gateway.Scope.RunId.ToString("N"), NativeId(gateway.Scope));
        // Readiness is authenticated, exact-scope, pinned-policy and read-only.
        _ = await SendAsync(HttpMethod.Get, "v1/codex/ready/" + handle.NativeId, null, handle, ct, ready: true);
    }
    public async Task<GatewaySandboxEvidence> CollectAsync(GatewayBinding gateway, ExternalAgentState nativeState, CancellationToken ct)
    {
        RequireGateway(gateway);
        var handle = new ExternalAgentHandle(gateway.Scope, Engine, BindingFingerprint,
            "msg_" + gateway.Scope.RunId.ToString("N"), NativeId(gateway.Scope));
        var r = await SendAsync(HttpMethod.Get, "v1/codex/runs/" + handle.NativeId + "/evidence", null, handle, ct);
        if (!r.ExecutorExited) return new(false, null);
        if (r.State == "Cancelled") return new(true, new("NeedsInput", "Execution stopped; review is required."));
        if (r.State == "Failed" || r.ExitCode != 0 || !r.ModelCompleted)
            return new(true, new("Failed", "The isolated executor did not complete successfully."));
        var v = r.Validation;
        if (r.State != "Completed" || nativeState != ExternalAgentState.Completed || v is null ||
            v.TestCount is < 1 or > 100000 || v.ChangedFileCount is < 1 or > 10000 ||
            !Hash(v.ChangeManifestSha256) || !Hash(v.TestOutputSha256) || !Hash(v.SnapshotSha256))
            throw new ExternalAgentException("codex-evidence-unverified");
        var prefix = "fanasa-evidence:" + gateway.Scope.RunId.ToString("N") + ":";
        return new(true, new(v.Passed ? "Succeeded" : "Failed",
            v.Passed ? "Isolated changes and independent offline tests are ready for review." : "Independent offline validation failed.",
            [new("Artifact", prefix + v.ChangeManifestSha256, "NotApplicable", "Change manifest SHA-256; private content retained by the trusted collector."),
             new("Artifact", prefix + v.SnapshotSha256, "NotApplicable", "Frozen workspace snapshot SHA-256."),
             new("Test", prefix + v.TestOutputSha256, v.Passed ? "Passed" : "Failed", $"Independent offline test cases: {v.TestCount}.")]));
    }
    private void RequireGateway(GatewayBinding gateway)
    {
        NativeAgentAdapterFactory.RequireScope(binding, gateway.Scope);
        if (gateway.Key != binding.Key || gateway.SandboxId != policy.SandboxId)
            throw new ExternalAgentException("codex-gateway-binding-changed");
    }
    private async Task<CodexBridgeReceipt> SendAsync(HttpMethod method, string path, object? body,
        ExternalAgentHandle handle, CancellationToken ct, bool ready = false)
    {
        var write = method != HttpMethod.Get;
        try
        {
            using var request = new HttpRequestMessage(method, new Uri(binding.Endpoint, path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", transportSecret);
            request.Headers.Add("X-Fanasa-Binding", BindingFingerprint);
            request.Headers.Add("X-Fanasa-Run", handle.Scope.RunId.ToString("D"));
            if (write) request.Headers.Add("Idempotency-Key", handle.Scope.RunId.ToString("N"));
            if (body is not null) request.Content = JsonContent.Create(body, options: Json);
            using var deadline = HttpRequestDeadline.Start(http, ct);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode != (write ? HttpStatusCode.Accepted : HttpStatusCode.OK))
                throw new ExternalAgentException("codex-bridge-unacknowledged", write);
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var bytes = new MemoryStream(); var chunk = new byte[4096];
            for (int n; (n = await stream.ReadAsync(chunk, deadline.Token)) != 0;)
            {
                if (bytes.Length + n > 32 * 1024) throw new ExternalAgentException("codex-receipt-limit", write);
                bytes.Write(chunk, 0, n);
            }
            using var doc = JsonDocument.Parse(bytes.ToArray(), new() { MaxDepth = 24 });
            Unique(doc.RootElement);
            // Secret-bearing/native raw text must never reach journal evidence.
            if (Encoding.UTF8.GetString(bytes.ToArray()).Contains(transportSecret, StringComparison.Ordinal))
                throw new ExternalAgentException("codex-receipt-invalid", write);
            var receipt = doc.RootElement.Deserialize<CodexBridgeReceipt>(Json);
            if (receipt is null || receipt.Protocol != "fanasa-codex/v1" || receipt.Scope != handle.Scope ||
                receipt.NativeId != handle.NativeId || receipt.BindingFingerprint != BindingFingerprint || receipt.Policy != policy ||
                receipt.State is not ("Ready" or "Running" or "Completed" or "Failed" or "Cancelled" or "StopRequested" or "Uncertain") ||
                ready && receipt.State != "Ready" || !ready && (receipt.ContainerId is null || !Hash(receipt.ContainerId)) ||
                receipt.ExecutorExited && receipt.ExitCode is not (>= 0 and <= 255) ||
                !receipt.ExecutorExited && receipt.ExitCode.HasValue)
                throw new ExternalAgentException("codex-receipt-invalid", write);
            return receipt;
        }
        catch (ExternalAgentException) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException or JsonException or InvalidOperationException)
        { throw new ExternalAgentException("codex-bridge-transport", write); }
    }
    private void RequireInput(ExternalAgentInput input)
    {
        NativeAgentAdapterFactory.RequireScope(binding, input.Scope);
        if (string.IsNullOrWhiteSpace(input.Prompt) || Encoding.UTF8.GetByteCount(input.Prompt) > 65536 ||
            Encoding.UTF8.GetByteCount(input.Instructions ?? "") > 16384)
            throw new ExternalAgentException("native-agent-input-limit");
    }
    private void RequireHandle(ExternalAgentHandle h)
    {
        NativeAgentAdapterFactory.RequireScope(binding, h.Scope);
        if (h.Engine != Engine || h.BindingFingerprint != BindingFingerprint || h.NativeId != NativeId(h.Scope) ||
            h.PromptId != "msg_" + h.Scope.RunId.ToString("N")) throw new ExternalAgentException("native-agent-binding-changed");
    }
    private static bool Hash(string? value) => value is not null && Regex.IsMatch(value, @"\A[0-9a-f]{64}\z");
    private static void RequireUsage(CodexBridgeUsage u)
    {
        static bool Id(string x) => x is { Length: > 0 and <= 100 } && Regex.IsMatch(x, @"\A[A-Za-z0-9_.:-]+\z");
        if (!Id(u.TurnId) || !Id(u.Provider) || !Id(u.Model) ||
            new[] { u.InputTokens, u.OutputTokens, u.CachedInputTokens, u.ReasoningTokens }.Any(x => x < 0) ||
            u.InputTokens.HasValue && u.CachedInputTokens > u.InputTokens || u.OutputTokens.HasValue && u.ReasoningTokens > u.OutputTokens ||
            u.InputTokens.HasValue && u.OutputTokens.HasValue && u.InputTokens > long.MaxValue - u.OutputTokens)
            throw new ExternalAgentException("native-agent-usage-invalid");
    }
    private static void Unique(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in e.EnumerateObject()) { if (!keys.Add(p.Name)) throw new JsonException(); Unique(p.Value); }
        }
        else if (e.ValueKind == JsonValueKind.Array) foreach (var row in e.EnumerateArray()) Unique(row);
    }
}
