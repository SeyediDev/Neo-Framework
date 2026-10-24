using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Fanasa.ContractPolicy;
using Fanasa.HrReadCandidate;
using Fanasa.HrReadPdp;

namespace Fanasa.HrReadHost;

public interface IHr3AuthenticationPreparer
{
    ValueTask<IHr3RetainedAuthenticationSource?> PrepareAsync(HrReadPdpRequest request,
        AuthenticatedHrReadPdpCaller caller, CancellationToken cancellationToken);
}

/// <summary>Explicitly enabled, bounded IdP observation. This does not assert instantaneous IdP push revocation.</summary>
public sealed record Hr3IntrospectionSettings
{
    public bool Enabled { get; init; }
    public string Issuer { get; init; } = "";
    public string GatewayAudience { get; init; } = "";
    public string GatewayRequiredScope { get; init; } = "";
    public int MaximumWitnessSeconds { get; init; } = HrReadDesign.MaximumWitnessSeconds;
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!Uri.TryCreate(Issuer, UriKind.Absolute, out var issuer) || issuer.Scheme != "https" ||
            issuer.UserInfo.Length != 0 || issuer.Query.Length != 0 || issuer.Fragment.Length != 0 ||
            issuer.AbsolutePath != "/realms/fanasa" || Issuer != issuer.AbsoluteUri) errors.Add("issuer");
        if (!Identifier(GatewayAudience)) errors.Add("gatewayAudience");
        if (!Identifier(GatewayRequiredScope)) errors.Add("gatewayRequiredScope");
        if (MaximumWitnessSeconds is < 1 or > HrReadDesign.MaximumWitnessSeconds) errors.Add("maximumWitnessSeconds");
        return errors.AsReadOnly();
    }
    private static bool Identifier(string? value) => value is { Length: > 0 and <= 64 } && value[0] is >= 'a' and <= 'z'
        && value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-');
}

/// <summary>Secret-bearing class deliberately has no generated record ToString/serialization surface.</summary>
public sealed class Hr3IntrospectionCredential
{
    public Hr3IntrospectionCredential(string clientId, string secret)
    { ClientId = clientId; Secret = secret; }
    public string ClientId { get; }
    internal string Secret { get; }
    internal bool Valid => ClientId is { Length: > 0 and <= 128 } && !ClientId.Any(c => char.IsControl(c) || c == ':')
        && Secret is { Length: > 0 and <= 4096 } && !Secret.Any(char.IsControl);
}
public interface IHr3IntrospectionCredentials
{
    ValueTask<Hr3IntrospectionCredential?> ReadAsync(CancellationToken cancellationToken);
}
public sealed class UnavailableHr3IntrospectionCredentials : IHr3IntrospectionCredentials
{
    public ValueTask<Hr3IntrospectionCredential?> ReadAsync(CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult<Hr3IntrospectionCredential?>(null); }
}

/// <summary>Independent server-owned mapping and CURRENT Gateway delegation; never a request/JWT tenant assertion.</summary>
public sealed record Hr3IntrospectionAuthoritySnapshot(Guid ConsumerTenantId, string GatewaySubjectId,
    string GatewayClientId, string SubjectId, string SubjectClientId, string Revision, DateTimeOffset ValidUntil);
public interface IHr3IntrospectionAuthority
{
    /// <summary>Read the unique consumer from actual central records, current authorized Gateway delegation,
    /// both current clients, membership and grant. Outside a DB snapshot this may open a short read snapshot;
    /// inside one it must reuse the SAME existing query-only transaction. No HTTP, write, blocking task,
    /// cached Permit, second store or tenant selected from request/claims. Revision excludes observation time.</summary>
    Hr3IntrospectionAuthoritySnapshot? ReadCurrent(HrReadPdpRequest request, AuthenticatedHrReadPdpCaller caller,
        CancellationToken cancellationToken);
}
public sealed class UnavailableHr3IntrospectionAuthority : IHr3IntrospectionAuthority
{
    public Hr3IntrospectionAuthoritySnapshot? ReadCurrent(HrReadPdpRequest request,
        AuthenticatedHrReadPdpCaller caller, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); return null; }
}

/// <summary>Trusted transport substitution point for tests/host integration; production default below disables redirects.</summary>
public interface IHr3IntrospectionTransport
{
    Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken);
}
public sealed class Hr3KeycloakHttpsTransport : IHr3IntrospectionTransport, IDisposable
{
    private readonly HttpClient client = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false, UseCookies = false, AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromSeconds(HrReadDesign.MaximumWitnessSeconds),
        SslOptions = new() { EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
            CertificateRevocationCheckMode = X509RevocationMode.Online }
    }) { Timeout = Timeout.InfiniteTimeSpan };
    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    public void Dispose() => client.Dispose();
}

/// <summary>
/// Introspects both exact original bearers asynchronously AFTER signed JWT/native TLS authentication,
/// then retains a request-local, at-most-five-second observation. Synchronous snapshot reads only
/// recheck the retained immutable caller and independently read current central mapping/delegation.
/// IdP revocation can remain unseen until the exclusive observation deadline; there is no Permit cache,
/// instant revocation assertion, background refresh, sync-over-async or network call inside Fabric reads.
/// </summary>
public sealed class Hr3KeycloakIntrospectionPreparer : IHr3AuthenticationPreparer, IDisposable
{
    public const int MaximumResponseBytes = 32768;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly Hr3IntrospectionSettings settings;
    private readonly IHr3IntrospectionCredentials credentials;
    private readonly IHr3IntrospectionAuthority authority;
    private readonly IHr3IntrospectionTransport transport;
    private readonly bool ownsTransport;
    private readonly TimeProvider time;
    public Hr3KeycloakIntrospectionPreparer(Hr3IntrospectionSettings settings,
        IHr3IntrospectionCredentials? credentials = null, IHr3IntrospectionAuthority? authority = null,
        IHr3IntrospectionTransport? transport = null, TimeProvider? time = null)
    {
        this.settings = (settings ?? throw new ArgumentNullException(nameof(settings))) with { };
        this.credentials = credentials ?? new UnavailableHr3IntrospectionCredentials();
        this.authority = authority ?? new UnavailableHr3IntrospectionAuthority();
        this.transport = transport ?? new Hr3KeycloakHttpsTransport(); ownsTransport = transport is null;
        this.time = time ?? TimeProvider.System;
    }

    public async ValueTask<IHr3RetainedAuthenticationSource?> PrepareAsync(HrReadPdpRequest request,
        AuthenticatedHrReadPdpCaller caller, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(caller);
        cancellationToken.ThrowIfCancellationRequested();
        if (!settings.Enabled || settings.Validate().Count != 0 || settings.Issuer != request.Binding.Issuer ||
            !caller.IsFor(request) || !caller.RevalidateCurrent()) return null;
        var startedAt = time.GetUtcNow().ToUniversalTime();
        var deadline = new[] { request.ValidUntil, caller.ValidUntil, startedAt.AddSeconds(settings.MaximumWitnessSeconds) }.Min();
        if (deadline <= startedAt) return null;
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, caller.RequestContext.RequestAborted);
        budget.CancelAfter(deadline - startedAt);
        var ct = budget.Token;
        try
        {
            var secret = await credentials.ReadAsync(ct).AsTask().WaitAsync(ct).ConfigureAwait(false);
            if (secret is null || !secret.Valid) return null;
            var first = authority.ReadCurrent(request, caller, ct);
            if (!Matches(first, request, caller, startedAt) || !Bearers(caller, out var gatewayToken, out var subjectToken)) return null;
            var gateway = caller.CallerPrincipal; var subject = caller.SubjectPrincipal;
            var gatewayBytes = await Introspect(gatewayToken, secret, gateway, settings.GatewayAudience,
                settings.GatewayRequiredScope, caller.CallerValidUntil, null, ct).ConfigureAwait(false);
            if (gatewayBytes is null || !caller.RevalidateCurrent()) return null;
            var subjectBytes = await Introspect(subjectToken, secret, subject, request.Audience,
                HrReadDesign.ReadScope, caller.SubjectValidUntil, first!.ConsumerTenantId, ct).ConfigureAwait(false);
            if (subjectBytes is null || !caller.RevalidateCurrent()) return null;
            var completedAt = time.GetUtcNow().ToUniversalTime();
            var last = authority.ReadCurrent(request, caller, ct);
            if (!Matches(last, request, caller, completedAt) || !Stable(first!, last!)) return null;
            var validUntil = new[] { deadline, first!.ValidUntil, last!.ValidUntil }.Min();
            // Start the IdP witness before BOTH requests, not after a slow HTTP exchange.
            if (completedAt >= validUntil || !caller.RevalidateCurrent()) return null;
            ct.ThrowIfCancellationRequested();
            var revision = Hash(string.Join('\n', first.Revision, Hash(gatewayBytes), Hash(subjectBytes),
                caller.HeaderSha256, caller.SubjectTokenSha256, request.RequestId));
            return new Retained(request, caller, authority, settings.GatewayRequiredScope, first, startedAt,
                validUntil, revision, time);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { cancellationToken.ThrowIfCancellationRequested(); return null; }
    }

    private async Task<byte[]?> Introspect(string token, Hr3IntrospectionCredential secret,
        ClaimsPrincipal principal, string audience, string requiredScope, DateTimeOffset expectedExpiry,
        Guid? expectedConsumer, CancellationToken ct)
    {
        var endpoint = new Uri(settings.Issuer + "/protocol/openid-connect/token/introspect", UriKind.Absolute);
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(
            WebUtility.UrlEncode(secret.ClientId) + ":" + WebUtility.UrlEncode(secret.Secret))));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
        request.Headers.Pragma.Add(new NameValueHeaderValue("no-cache"));
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string>
            { ["token"] = token, ["token_type_hint"] = "access_token" });
        using var response = await transport.SendAsync(request, ct).WaitAsync(ct).ConfigureAwait(false);
        var content = response.Content;
        var contentType = content?.Headers.ContentType;
        if (response.StatusCode != HttpStatusCode.OK || content is null || contentType is null ||
            response.RequestMessage?.RequestUri is { } actual && actual != endpoint ||
            contentType.MediaType != "application/json" ||
            contentType.CharSet is { } charset && !string.Equals(charset.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase) ||
            content.Headers.ContentEncoding.Count != 0 ||
            content.Headers.ContentLength is < 1 or > MaximumResponseBytes) return null;
        using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream(); var chunk = new byte[4096];
        while (true)
        {
            var read = await stream.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, MaximumResponseBytes + 1 - (int)buffer.Length)), ct).ConfigureAwait(false);
            if (read == 0) break;
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaximumResponseBytes) return null;
        }
        var bytes = buffer.ToArray();
        if (bytes.Length == 0 || content.Headers.ContentLength is { } length && length != bytes.Length) return null;
        // Decode explicitly so malformed UTF-8 can never become replacement-character claims.
        using var json = JsonDocument.Parse(StrictUtf8.GetString(bytes), new JsonDocumentOptions { MaxDepth = 16 });
        RejectDuplicates(json.RootElement);
        return ClaimsMatch(json.RootElement, principal, audience, requiredScope, expectedExpiry, expectedConsumer, time.GetUtcNow()) ? bytes : null;
    }

    private bool ClaimsMatch(JsonElement root, ClaimsPrincipal principal, string audience,
        string requiredScope, DateTimeOffset expectedExpiry, Guid? expectedConsumer, DateTimeOffset now)
    {
        var identities = principal.Identities.Where(x => x.IsAuthenticated).Take(2).ToArray();
        if (identities.Length != 1 || root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("active", out var active) || active.ValueKind != JsonValueKind.True) return false;
        var identity = identities[0];
        foreach (var name in new[] { "iss", "sub", "azp", "scope" })
            if (!String(root, name, out var text) || Single(identity, name) != text) return false;
        if (Single(identity, "iss") != settings.Issuer || !String(root, "client_id", out var client) ||
            client != Single(identity, "azp") || !String(root, "scope", out var scope) ||
            !scope.Split(' ').Contains(requiredScope, StringComparer.Ordinal) ||
            !Number(root, "exp", out var exp) || exp != expectedExpiry || now >= exp) return false;
        if (expectedConsumer is { } consumer && (consumer == Guid.Empty ||
            !String(root, "consumerTenantId", out var mapped) || !Guid.TryParseExact(mapped, "D", out var mappedId) ||
            mappedId == Guid.Empty || mappedId.ToString("D") != mapped || mappedId != consumer)) return false;
        var signedIssued = identity.FindAll("iat").Take(2).ToArray();
        if (signedIssued.Length > 1 || signedIssued.Length == 1 &&
            (!Numeric(signedIssued[0].Value, out var signedTime) || signedTime > now || signedTime >= exp)) return false;
        if (root.TryGetProperty("iat", out _))
        {
            if (!Number(root, "iat", out var issued) || issued > now || issued >= exp ||
                signedIssued.Length == 1 && signedIssued[0].Value != issued.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)) return false;
        }
        else if (signedIssued.Length != 0) return false;
        if (root.TryGetProperty("nbf", out _))
        {
            if (!Number(root, "nbf", out var nbf) || now < nbf ||
                Single(identity, "nbf") is { } original && original != nbf.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)) return false;
        }
        else if (Single(identity, "nbf") is not null) return false;
        if (!root.TryGetProperty("aud", out var aud)) return false;
        var values = aud.ValueKind == JsonValueKind.Array ? aud.EnumerateArray().ToArray() : [aud];
        if (values.Length is 0 or > 16 || values.Any(x => x.ValueKind != JsonValueKind.String)) return false;
        var actual = values.Select(x => x.GetString()!).ToArray();
        var expected = identity.FindAll("aud").Select(x => x.Value).ToArray();
        return actual.All(x => !string.IsNullOrEmpty(x)) && actual.Distinct(StringComparer.Ordinal).Count() == actual.Length &&
            actual.Contains(audience, StringComparer.Ordinal) && actual.Order(StringComparer.Ordinal).SequenceEqual(expected.Order(StringComparer.Ordinal), StringComparer.Ordinal);
    }

    private static bool Matches(Hr3IntrospectionAuthoritySnapshot? a, HrReadPdpRequest r,
        AuthenticatedHrReadPdpCaller caller, DateTimeOffset now)
    {
        var gateway = caller.CallerPrincipal.Identities.Where(x => x.IsAuthenticated).Take(2).ToArray();
        var subject = caller.SubjectPrincipal.Identities.Where(x => x.IsAuthenticated).Take(2).ToArray();
        return a is not null && gateway.Length == 1 && subject.Length == 1 && a.ConsumerTenantId != Guid.Empty &&
            a.ConsumerTenantId.ToString("D") == r.ConsumerTenantId && a.GatewaySubjectId == Single(gateway[0], "sub") &&
            a.GatewayClientId == Single(gateway[0], "azp") && a.SubjectId == r.SubjectId && a.SubjectClientId == r.ClientId &&
            a.SubjectId == Single(subject[0], "sub") && a.SubjectClientId == Single(subject[0], "azp") &&
            a.ValidUntil.Offset == TimeSpan.Zero && now < a.ValidUntil && a.Revision is { Length: > 0 and <= 256 } &&
            a.Revision.All(c => c >= '!' && c <= '~') && caller.IsFor(r) && caller.RevalidateCurrent();
    }
    private static bool Stable(Hr3IntrospectionAuthoritySnapshot first, Hr3IntrospectionAuthoritySnapshot last) =>
        (first with { ValidUntil = last.ValidUntil }) == last;
    private static bool Bearers(AuthenticatedHrReadPdpCaller caller, out string gateway, out string subject)
    {
        gateway = ""; subject = "";
        bool Read(string name, string hash, out string token)
        {
            token = "";
            var values = caller.RequestContext.Request.Headers[name];
            if (values.Count != 1 || values[0] is not { Length: > 7 and <= 16384 } text ||
                !text.StartsWith("Bearer ", StringComparison.Ordinal) || Hash(text) != hash ||
                text.AsSpan(7).ContainsAnyExcept("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._~+/=")) return false;
            token = text[7..]; return true;
        }
        return Read("Authorization", caller.HeaderSha256, out gateway) &&
            Read(HrReadPdpAuthentication.SubjectHeader, caller.SubjectHeaderSha256, out subject) &&
            Hash(subject) == caller.SubjectTokenSha256;
    }
    private static bool String(JsonElement root, string name, out string text)
    { text = ""; if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String) return false; text = value.GetString()!; return true; }
    private static bool Number(JsonElement root, string name, out DateTimeOffset value)
    {
        value = default;
        return root.TryGetProperty(name, out var number) && number.ValueKind == JsonValueKind.Number && Numeric(number.GetRawText(), out value);
    }
    private static bool Numeric(string text, out DateTimeOffset value)
    {
        value = default;
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
            seconds is < 0 or > 253402300799L || text != seconds.ToString(CultureInfo.InvariantCulture)) return false;
        value = DateTimeOffset.FromUnixTimeSeconds(seconds); return true;
    }
    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            { if (!names.Add(property.Name)) throw new JsonException("Duplicate introspection property."); RejectDuplicates(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
    }
    private static string? Single(ClaimsIdentity identity, string name)
    { var values = identity.FindAll(name).Take(2).ToArray(); return values.Length == 1 ? values[0].Value : null; }
    private static string Hash(string value) => Hash(Encoding.UTF8.GetBytes(value));
    private static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
    public void Dispose() { if (ownsTransport && transport is IDisposable disposable) disposable.Dispose(); }

    private sealed class Retained(HrReadPdpRequest request, AuthenticatedHrReadPdpCaller caller,
        IHr3IntrospectionAuthority authority, string gatewayScope, Hr3IntrospectionAuthoritySnapshot observed,
        DateTimeOffset observedAt, DateTimeOffset validUntil, string revision, TimeProvider time) : IHr3RetainedAuthenticationSource
    {
        private readonly string queryFingerprint = JsonSerializer.Serialize(HrReadPdpService.Query(request, caller), ContractJson.Options);
        public Hr3RetainedAuthenticationProof? ReadCurrent(TrustedAccessSnapshotQuery query,
            AuthenticatedHrReadPdpCaller currentCaller, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var now = time.GetUtcNow();
                if (!ReferenceEquals(caller, currentCaller) || now < observedAt || now >= validUntil || !caller.RevalidateCurrent() ||
                    JsonSerializer.Serialize(query, ContractJson.Options) != queryFingerprint) return null;
                var current = authority.ReadCurrent(request, caller, cancellationToken);
                if (!Matches(current, request, caller, now) || !Stable(observed, current!)) return null;
                var until = new[] { validUntil, current!.ValidUntil, caller.ValidUntil }.Min();
                if (time.GetUtcNow() >= until || !caller.RevalidateCurrent()) return null;
                cancellationToken.ThrowIfCancellationRequested();
                return new Hr3RetainedAuthenticationProof(observed.GatewaySubjectId, observed.GatewayClientId,
                    caller.HeaderSha256, observed.SubjectId, observed.SubjectClientId, caller.SubjectTokenSha256,
                    caller.SubjectValidUntil, observed.ConsumerTenantId, query.Selector!, query.Action,
                    query.AuthenticatedSubject.Scopes, revision, observedAt, until, gatewayScope, request.RequestId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception) { cancellationToken.ThrowIfCancellationRequested(); return null; }
        }
    }
}
