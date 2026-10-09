using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Neo.AgentOrchestration.Application.ExternalAgents;
using Neo.AgentOrchestration.Application.ExternalExecution;
using Neo.AgentOrchestration.Contracts;
using Neo.AgentOrchestration.Domain.ExternalExecution;
using Neo.AgentOrchestration.Infrastructure.ExternalAgents;
using Neo.AgentOrchestration.Infrastructure.Runs;

namespace Neo.AgentOrchestration.Infrastructure.ExternalExecution;

public sealed class ConfiguredGatewayBindings(IConfiguration configuration, IHostEnvironment environment,
    IHarnessSecrets secrets, NativeAgentAdapterFactory native) : IGatewayBindings
{
    public GatewayBinding Resolve(string key, ExternalAgentScope scope, Guid profileId, string callbackUrl)
    {
        try
        {
            if (!configuration.GetValue<bool>("AgentGateway:Enabled") ||
                !Regex.IsMatch(key, @"\A[a-z0-9][a-z0-9_-]{0,39}\z")) throw new InvalidOperationException();
            var c = configuration.GetSection("AgentGateway:Bindings:" + key);
            if (!c.GetValue<bool>("Enabled") || c.GetValue<Guid>("AgentProfileId") != profileId) throw new InvalidOperationException();
            // Native factory independently enforces org/workspace/project scope,
            // HTTPS, model route and transport credential availability.
            var adapter = native.Create(key, scope);
            var sandbox = c["SandboxId"] ?? "";
            var revision = c["Revision"] ?? "";
            var repository = c["RepositoryUrl"] ?? "";
            var image = c["ImageDigest"] ?? "";
            if (!Regex.IsMatch(sandbox, @"\A[a-z0-9][a-z0-9_-]{0,99}\z") ||
                !Regex.IsMatch(revision, @"\A[0-9a-fA-F]{40,64}\z") ||
                !Regex.IsMatch(image, @"\Asha256:[0-9a-f]{64}\z") ||
                !Uri.TryCreate(repository, UriKind.Absolute, out var repo) || repo.Scheme != "https" ||
                repo.UserInfo.Length != 0 || repo.Query.Length != 0 || repo.Fragment.Length != 0) throw new InvalidOperationException();
            var callbackBase = Address(c["CallbackBaseUrl"]);
            if (!callbackBase.AbsolutePath.EndsWith('/')) callbackBase = new Uri(callbackBase.AbsoluteUri + "/");
            var callback = new Uri(callbackBase, $"api/orchestration/v1/organizations/{scope.OrganizationId:D}/workspaces/{scope.WorkspaceId:D}/harness/{key}/runs/{scope.RunId:D}/result");
            if (callback.AbsoluteUri != callbackUrl) throw new InvalidOperationException();
            var dispatchRef = c["DispatchSecretRef"] ?? "";
            var callbackRef = c["CallbackSecretRef"] ?? "";
            var nativeRef = configuration["NativeAgents:Connections:" + key + ":SecretRef"] ?? "";
            var refs = new[] { dispatchRef, callbackRef, nativeRef };
            if (refs.Distinct(StringComparer.Ordinal).Count() != refs.Length) throw new InvalidOperationException();
            var values = refs.Select(secrets.Resolve).ToArray();
            if (values.Distinct(StringComparer.Ordinal).Count() != values.Length) throw new InvalidOperationException();
            var fingerprint = GatewayRun.Hash(JsonSerializer.Serialize(new {
                key, profileId, NativeFingerprint = adapter.BindingFingerprint, sandbox, revision, repository, image,
                callbackBase, dispatchRef, callbackRef
            }));
            return new(key, fingerprint, sandbox, scope, profileId, callback);
        }
        catch (ExternalAgentException) { throw; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
        { throw new ExternalAgentException("gateway-binding-unavailable"); }
    }
    public IExternalAgentAdapter Adapter(GatewayBinding binding) => native.Create(binding.Key, binding.Scope);
    public bool Authenticate(string key, string? bearer)
    {
        if (!configuration.GetValue<bool>("AgentGateway:Enabled") || !Regex.IsMatch(key, @"\A[a-z0-9][a-z0-9_-]{0,39}\z") ||
            !configuration.GetValue<bool>("AgentGateway:Bindings:" + key + ":Enabled") || bearer is not { Length: >= 32 and <= 1024 }) return false;
        try
        {
            var expected = secrets.Resolve(configuration["AgentGateway:Bindings:" + key + ":DispatchSecretRef"] ?? "");
            return CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(expected)), SHA256.HashData(Encoding.UTF8.GetBytes(bearer)));
        }
        catch (InvalidOperationException) { return false; }
    }
    public string CallbackSecret(GatewayBinding binding)
    {
        var current = Resolve(binding.Key, binding.Scope, binding.AgentProfileId, binding.CallbackUrl.AbsoluteUri);
        if (binding.Fingerprint != current.Fingerprint) throw new ExternalAgentException("gateway-binding-changed");
        return secrets.Resolve(configuration["AgentGateway:Bindings:" + binding.Key + ":CallbackSecretRef"]!);
    }
    private Uri Address(string? value)
    {
        var local = configuration.GetValue<bool>("AgentGateway:AllowLoopbackHttp") &&
            (environment.IsDevelopment() || environment.IsEnvironment("Testing"));
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.Scheme != "https" && !(local && uri.Scheme == "http" && uri.IsLoopback)) throw new InvalidOperationException();
        return uri;
    }
}

public sealed class HttpGatewayResultDelivery(HttpClient http, ConfiguredGatewayBindings bindings) : IGatewayResultDelivery
{
    public async Task<Guid> DeliverAsync(GatewayBinding binding, string frozenResult, CancellationToken ct)
    {
        try
        {
            var secret = bindings.CallbackSecret(binding);
            if (frozenResult.Contains(secret, StringComparison.Ordinal)) throw new ExternalAgentException("gateway-result-invalid");
            using var request = new HttpRequestMessage(HttpMethod.Post, binding.CallbackUrl);
            request.Headers.Add("X-Neo-Harness-Key", secret);
            request.Content = new StringContent(frozenResult, Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode != HttpStatusCode.OK) throw new ExternalAgentException("gateway-callback-unacknowledged", true);
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream(); var chunk = new byte[2048];
            for (int read; (read = await stream.ReadAsync(chunk, ct)) != 0;)
            { if (buffer.Length + read > 8192) throw new ExternalAgentException("gateway-callback-invalid", true); buffer.Write(chunk, 0, read); }
            var receipt = JsonSerializer.Deserialize<HarnessReceipt>(buffer.ToArray(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
            return receipt is { ReceiptId: var id } && id != Guid.Empty ? id : throw new ExternalAgentException("gateway-callback-invalid", true);
        }
        catch (ExternalAgentException) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException or IOException or InvalidOperationException)
        { throw new ExternalAgentException("gateway-callback-unavailable", true); }
    }
}

public sealed class DisabledGatewaySandbox : IGatewaySandbox
{
    public Task VerifyReadyAsync(GatewayBinding binding, CancellationToken ct) => throw new ExternalAgentException("gateway-sandbox-unavailable");
    public Task<GatewaySandboxEvidence> CollectAsync(GatewayBinding binding, ExternalAgentState nativeState, CancellationToken ct)
        => throw new ExternalAgentException("gateway-sandbox-unavailable");
}
