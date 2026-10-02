using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Neo.AgentOrchestration.Application.Runs;
using Neo.AgentOrchestration.Domain.Projects;

namespace Neo.AgentOrchestration.Infrastructure.Runs;

public interface IHarnessSecrets { string Resolve(string reference); }
public sealed class EnvironmentHarnessSecrets : IHarnessSecrets
{
    public string Resolve(string reference)
    {
        if (!Regex.IsMatch(reference, @"\Aenv:[A-Z][A-Z0-9_]{0,100}\z", RegexOptions.CultureInvariant))
            throw new InvalidOperationException("Invalid harness secret reference.");
        var secret = Environment.GetEnvironmentVariable(reference[4..]);
        if (secret is null || secret.Length is < 32 or > 1024 || secret.Any(char.IsWhiteSpace) || secret.Any(char.IsControl))
            throw new InvalidOperationException("Harness secret is unavailable.");
        return secret;
    }
}
public sealed record HttpHarnessConnection(string Key, Uri Endpoint, Uri CallbackBaseUrl, string DispatchSecretRef, string CallbackSecretRef,
    bool CompactContextOptIn)
{
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        JsonSerializer.Serialize(new { Key, Endpoint, CallbackBaseUrl, DispatchSecretRef, CallbackSecretRef, CompactContextOptIn }))));
}
public sealed class ConfiguredRunProviders(IConfiguration configuration, IHostEnvironment environment, IHarnessSecrets? secrets = null) : IRunProviders
{
    public void RequireAvailable(string provider, WorkspaceScope scope)
    {
        if (provider == "fake")
        { if (!configuration.GetValue<bool>("Orchestration:SimulationEnabled")) throw new RunProviderUnavailableException(provider); }
        else
        {
            var connection = Resolve(provider, scope);
            try
            {
                var resolver = secrets ?? new EnvironmentHarnessSecrets();
                var outbound = resolver.Resolve(connection.DispatchSecretRef); var inbound = resolver.Resolve(connection.CallbackSecretRef);
                if (outbound == inbound) throw new InvalidOperationException("Use distinct directional credentials.");
            }
            catch (InvalidOperationException) { throw new RunProviderUnavailableException(provider); }
        }
    }
    public HarnessBinding Bind(string provider, WorkspaceScope scope, Guid runId)
    {
        var connection = Resolve(provider, scope);
        return new(connection.Fingerprint, new Uri(connection.CallbackBaseUrl,
            $"api/orchestration/v1/organizations/{scope.OrganizationId:D}/workspaces/{scope.WorkspaceId:D}/harness/{connection.Key}/runs/{runId:D}/result").AbsoluteUri,
            connection.CompactContextOptIn);
    }
    public HttpHarnessConnection Resolve(string provider, WorkspaceScope scope)
    {
        try
        {
            if (!configuration.GetValue<bool>("Harness:Enabled") || !provider.StartsWith("http.", StringComparison.Ordinal))
                throw new RunProviderUnavailableException(provider);
            var key = provider[5..];
            if (!Regex.IsMatch(key, @"\A[a-z0-9][a-z0-9_-]{0,39}\z", RegexOptions.CultureInvariant))
                throw new RunProviderUnavailableException(provider);
            var section = configuration.GetSection("Harness:Connections:" + key);
            if (!section.GetValue<bool>("Enabled") || section.GetValue<Guid>("OrganizationId") != scope.OrganizationId ||
                section.GetValue<Guid>("WorkspaceId") != scope.WorkspaceId)
                throw new RunProviderUnavailableException(provider);
            var local = configuration.GetValue<bool>("Harness:AllowLoopbackHttp") && (environment.IsDevelopment() || environment.IsEnvironment("Testing"));
            var endpoint = Address(section["Endpoint"], local);
            var callback = Address(section["CallbackBaseUrl"], local);
            if (!callback.AbsolutePath.EndsWith('/')) callback = new Uri(callback.AbsoluteUri + "/");
            var dispatchRef = section["DispatchSecretRef"] ?? ""; var callbackRef = section["CallbackSecretRef"] ?? "";
            foreach (var reference in new[] { dispatchRef, callbackRef })
                if (!Regex.IsMatch(reference, @"\Aenv:[A-Z][A-Z0-9_]{0,100}\z", RegexOptions.CultureInvariant))
                    throw new RunProviderUnavailableException(provider);
            if (dispatchRef == callbackRef) throw new RunProviderUnavailableException(provider);
            return new(key, endpoint, callback, dispatchRef, callbackRef, section.GetValue<bool>("CompactContextOptIn"));
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException)
        { throw new RunProviderUnavailableException(provider); }
    }
    private static Uri Address(string? address, bool local)
    {
        if (!Uri.TryCreate(address, UriKind.Absolute, out var uri) ||
            (uri.Scheme != "https" && !(local && uri.Scheme == "http" && uri.IsLoopback)) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("Invalid harness destination.");
        return uri;
    }
}
public static class HarnessRegistration
{
    public static IServiceCollection AddHttpHarness(this IServiceCollection services)
    {
        services.TryAddSingleton<ConfiguredRunProviders>();
        services.TryAddSingleton<IRunProviders>(sp => sp.GetRequiredService<ConfiguredRunProviders>());
        services.TryAddSingleton<IHarnessSecrets, EnvironmentHarnessSecrets>();
        services.AddHttpClient<HttpHarnessTransport>(client => client.Timeout = TimeSpan.FromSeconds(30))
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
            .RemoveAllLoggers();
        return services;
    }
}
