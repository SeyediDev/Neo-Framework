using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace Fanasa.AccessManagement.Web.Governance;

public sealed class PipOAuthProfile
{
    public Guid TenantId { get; set; }
    public string Reference { get; set; } = "";
    public string TokenEndpoint { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string ClientSecretReference { get; set; } = "";
    public string[] Scopes { get; set; } = [];
    public string[] AllowedPrivateAddresses { get; set; } = [];
    public string? CaFile { get; set; }
    public int TimeoutSeconds { get; set; } = 10;
    public PipEndpointBinding Binding() => new() { TenantId = TenantId,
        Endpoint = TokenEndpoint, SecretReference = Reference, AllowedPrivateAddresses = AllowedPrivateAddresses, CaFile = CaFile };
}

public sealed class PipOAuthOptions
{
    public PipOAuthProfile[] Profiles { get; set; } = [];
    public void Validate(PipTransportOptions transport)
    {
        Validate();
        foreach (var binding in transport.Bindings)
        {
            var profile = Profiles.SingleOrDefault(x => x.Reference == binding.SecretReference);
            if (profile is not null && profile.TenantId != binding.TenantId)
                throw new InvalidOperationException("PIP OAuth profile cannot be bound to another tenant.");
        }
    }
    public void Validate()
    {
        if (Profiles is null || Profiles.Length > 2000) throw new InvalidOperationException("Invalid PIP OAuth profiles.");
        var references = new HashSet<string>(StringComparer.Ordinal);
        foreach (var profile in Profiles)
        {
            if (profile is null || !Valid(profile.Reference) || !references.Add(profile.Reference)
                || !Valid(profile.ClientId) || !Valid(profile.ClientSecretReference) || profile.TimeoutSeconds is < 1 or > 120
                || profile.Scopes is not { Length: > 0 and <= 20 } || profile.Scopes.Any(x => !Valid(x) || x.Any(char.IsWhiteSpace))
                || profile.Scopes.Distinct(StringComparer.Ordinal).Count() != profile.Scopes.Length)
                throw new InvalidOperationException("Invalid PIP OAuth profile contract.");
            new PipTransportOptions { Bindings = [profile.Binding()] }.Validate();
        }
    }
    private static bool Valid(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 200 && !value.Any(char.IsControl);
}

/// <summary>Deployment-scoped client credentials, memory-only tokens and single-flight proactive renewal.</summary>
public sealed class OAuthPipSecretResolver(TimeProvider clock, PipOAuthOptions options,
    ConfigurationPipSecretResolver secrets, IPipHttpClientProvider clients) : IPipSecretResolver
{
    private sealed class Entry { public readonly SemaphoreSlim Gate = new(1, 1); public string? Token; public DateTimeOffset RefreshAt; }
    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);
    public async ValueTask<string?> ResolveAsync(string? reference, CancellationToken cancellationToken = default)
    {
        if (reference is null) return null;
        var profile = options.Profiles.SingleOrDefault(x => string.Equals(x.Reference, reference, StringComparison.Ordinal));
        if (profile is null) return await secrets.ResolveAsync(reference, cancellationToken).ConfigureAwait(false);
        var entry = entries.GetOrAdd(reference, _ => new());
        await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (entry.Token is not null && clock.GetUtcNow() < entry.RefreshAt) return entry.Token;
            var clientSecret = await secrets.ResolveAsync(profile.ClientSecretReference, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(clientSecret) || clientSecret.Length > 8192 || clientSecret.Any(char.IsControl))
                throw new InvalidOperationException("PIP OAuth client credential is unavailable.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(profile.TimeoutSeconds));
            var binding = profile.Binding();
            using var request = new HttpRequestMessage(HttpMethod.Post, PipTransportOptions.CanonicalEndpoint(profile.TokenEndpoint))
            {
                Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["grant_type"] = "client_credentials",
                    ["client_id"] = profile.ClientId, ["client_secret"] = clientSecret, ["scope"] = string.Join(' ', profile.Scopes) })
            };
            request.Options.Set(PipHttpTransport.BindingKey, binding);
            request.Headers.ConnectionClose = true;
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var started = clock.GetUtcNow();
            using var response = await clients.GetClient(binding).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentType?.MediaType != "application/json"
                || response.Content.Headers.ContentEncoding.Count != 0 || response.Content.Headers.ContentLength > 32768)
                throw new InvalidDataException("PIP OAuth token response was rejected.");
            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var buffer = new MemoryStream(); var bytes = new byte[4096]; int read;
            while ((read = await stream.ReadAsync(bytes, timeout.Token).ConfigureAwait(false)) > 0)
            { if (buffer.Length + read > 32768) throw new InvalidDataException("PIP OAuth response exceeded its limit."); buffer.Write(bytes, 0, read); }
            using var document = JsonDocument.Parse(buffer.ToArray(), new() { MaxDepth = 4 });
            var root = document.RootElement; var seen = new HashSet<string>(StringComparer.Ordinal);
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(x => !seen.Add(x.Name)))
                throw new InvalidDataException("Invalid PIP OAuth response.");
            if (!root.TryGetProperty("access_token", out var token) || token.ValueKind != JsonValueKind.String
                || token.GetString() is not { Length: > 0 and <= 8192 } value || value.Any(char.IsWhiteSpace) || value.Any(char.IsControl)
                || !root.TryGetProperty("token_type", out var type) || type.ValueKind != JsonValueKind.String || !string.Equals(type.GetString(), "Bearer", StringComparison.OrdinalIgnoreCase)
                || !root.TryGetProperty("expires_in", out var expires) || !expires.TryGetInt32(out var seconds) || seconds is < 10 or > 86400)
                throw new InvalidDataException("Invalid PIP OAuth token metadata.");
            if (root.TryGetProperty("scope", out var returnedScopes) && (returnedScopes.ValueKind != JsonValueKind.String
                || !profile.Scopes.ToHashSet(StringComparer.Ordinal).SetEquals(returnedScopes.GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries))))
                throw new InvalidDataException("PIP OAuth response scope differs from the deployment profile.");
            var refreshAt = started.AddSeconds(seconds - Math.Min(60, Math.Max(5, seconds / 10)));
            if (refreshAt <= clock.GetUtcNow()) throw new InvalidDataException("PIP OAuth token is already expired.");
            entry.Token = value; entry.RefreshAt = refreshAt;
            return value;
        }
        catch (OperationCanceledException) { throw; }
        catch { entry.Token = null; throw new InvalidOperationException("PIP OAuth credential could not be renewed."); }
        finally { entry.Gate.Release(); }
    }
}
