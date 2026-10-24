using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Authentication;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Fanasa.HrReadCandidate;
using Fanasa.HrReadCandidate.Http;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Fanasa.HrReadPdp;

/// <summary>Explicit service-caller intent. Structural validity creates no central client or delegation.</summary>
public sealed record HrReadPdpCallerSettings
{
    public required bool Enabled { get; init; }
    public required string Issuer { get; init; }
    public required string Audience { get; init; }
    public required string ClientId { get; init; }
    public required string SubjectId { get; init; }
    public required string RequiredScope { get; init; }
    public required string PeerCertificateSha256 { get; init; }

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!Enabled) errors.Add("enabled");
        if (!Uri.TryCreate(Issuer, UriKind.Absolute, out var issuer) || issuer.Scheme != "https"
            || issuer.AbsolutePath != "/realms/fanasa" || issuer.UserInfo.Length != 0 || issuer.Query.Length != 0
            || issuer.Fragment.Length != 0 || issuer.AbsoluteUri != Issuer) errors.Add("issuer");
        foreach (var (name, value) in new[] { ("audience", Audience), ("clientId", ClientId), ("requiredScope", RequiredScope) })
            if (value is not { Length: > 0 and <= 1024 } || value.Any(c => c < '!' || c > '~' || c is '"' or '\\' or ',')) errors.Add(name);
        if (!Guid.TryParseExact(SubjectId, "D", out var subject) || subject == Guid.Empty || subject.ToString("D") != SubjectId)
            errors.Add("subjectId");
        if (!HrReadPdpAuthentication.IsLowerHash(PeerCertificateSha256)) errors.Add("peerCertificateSha256");
        return errors.AsReadOnly();
    }
}

/// <summary>
/// Verified transport proof only. No PIP, introspection, central registration, grant or obligation fulfillment is implied.
/// Construction is limited to the named authentication boundary.
/// </summary>
public sealed class AuthenticatedHrReadPdpCaller
{
    private readonly ClaimsPrincipal caller;
    private readonly ClaimsPrincipal subject;
    private readonly string callerFingerprint;
    private readonly string subjectFingerprint;
    private readonly byte[] certificateDer;
    private readonly ITlsHandshakeFeature handshake;
    private readonly HrReadPdpCallerSettings callerSettings;
    private readonly HrReadReceiverSettings binding;
    private readonly HrReadPdpRequest request;
    private readonly TimeProvider time;

    internal AuthenticatedHrReadPdpCaller(HttpContext http, HrReadPdpCallerSettings settings, HrReadReceiverSettings binding,
        HrReadPdpRequest request, ClaimsPrincipal caller, ClaimsPrincipal subject, byte[] certificateDer,
        ITlsHandshakeFeature handshake, string authorization, string subjectAuthorization,
        DateTimeOffset callerExpiry, DateTimeOffset subjectExpiry, DateTimeOffset certificateExpiry, TimeProvider time)
    {
        RequestContext = http; callerSettings = settings with { }; this.binding = binding with { }; this.request = request;
        this.caller = caller; this.subject = subject; this.certificateDer = certificateDer.ToArray(); this.handshake = handshake;
        callerFingerprint = HrReadPdpAuthentication.Fingerprint(caller); subjectFingerprint = HrReadPdpAuthentication.Fingerprint(subject);
        HeaderSha256 = HrReadPdpAuthentication.Hash(authorization); SubjectHeaderSha256 = HrReadPdpAuthentication.Hash(subjectAuthorization);
        SubjectTokenSha256 = HrReadPdpAuthentication.Hash(subjectAuthorization[7..]);
        CallerValidUntil = callerExpiry; SubjectValidUntil = subjectExpiry; NativeCertificateValidUntil = certificateExpiry;
        ValidUntil = new[] { callerExpiry, subjectExpiry, certificateExpiry, request.ValidUntil }.Min();
        this.time = time; ObservedAt = time.GetUtcNow();
    }

    public HttpContext RequestContext { get; }
    public ClaimsPrincipal CallerPrincipal => Clone(caller);
    public ClaimsPrincipal SubjectPrincipal => Clone(subject);
    public string HeaderSha256 { get; }
    public string SubjectHeaderSha256 { get; }
    public string SubjectTokenSha256 { get; }
    public DateTimeOffset CallerValidUntil { get; }
    public DateTimeOffset SubjectValidUntil { get; }
    public DateTimeOffset NativeCertificateValidUntil { get; }
    public DateTimeOffset ValidUntil { get; }
    public DateTimeOffset ObservedAt { get; }
    public byte[] GetNativeCertificateDer() => certificateDer.ToArray();
    /// <summary>This transport proof cannot be reused with another parsed target, tuple or request identifier.</summary>
    public bool IsFor(HrReadPdpRequest candidate) => ReferenceEquals(request, candidate);

    /// <summary>Local, synchronous final gate. The host separately checks current central authority/revocation.</summary>
    public bool RevalidateCurrent()
    {
        try
        {
            var now = time.GetUtcNow();
            return !RequestContext.RequestAborted.IsCancellationRequested && now < ValidUntil
                && HrReadPdpAuthentication.IsEndpoint(RequestContext)
                && ReferenceEquals(handshake, RequestContext.Features.Get<ITlsHandshakeFeature>())
                && HrReadPdpAuthentication.HasNativeTls(RequestContext)
                && HrReadPdpAuthentication.SingleBearer(RequestContext, "Authorization", out var authorization)
                && HrReadPdpAuthentication.Hash(authorization) == HeaderSha256
                && HrReadPdpAuthentication.SingleBearer(RequestContext, HrReadPdpAuthentication.SubjectHeader, out var subjectAuthorization)
                && HrReadPdpAuthentication.Hash(subjectAuthorization) == SubjectHeaderSha256
                && HrReadPdpAuthentication.Fingerprint(caller) == callerFingerprint
                && HrReadPdpAuthentication.Fingerprint(subject) == subjectFingerprint
                && HrReadPdpAuthentication.PrincipalMatches(caller, callerSettings.Issuer, callerSettings.Audience,
                    callerSettings.ClientId, callerSettings.SubjectId, callerSettings.RequiredScope, now, out var callerExpiry)
                && callerExpiry == CallerValidUntil
                && HrReadPdpAuthentication.PrincipalMatches(subject, binding.Issuer, binding.Audience, binding.ClientId,
                    binding.SubjectId, HrReadDesign.ReadScope, now, out var subjectExpiry) && subjectExpiry == SubjectValidUntil
                && subjectExpiry.ToUnixTimeSeconds() == request.TokenExpiresAt && SubjectTokenSha256 == request.TokenSha256
                && RequestContext.Connection.ClientCertificate is { } certificate
                && HrReadPdpAuthentication.CurrentCertificate(certificate, callerSettings.PeerCertificateSha256, now)
                && CryptographicOperations.FixedTimeEquals(certificate.RawData, certificateDer);
        }
        catch (Exception) { return false; }
    }

    private static ClaimsPrincipal Clone(ClaimsPrincipal source) => new(source.Identities.Select(identity => new ClaimsIdentity(identity)));
}

public static class HrReadPdpAuthentication
{
    public const string CallerScheme = "HrReadPdpGatewayBearer";
    public const string SubjectScheme = "OrganizationHrPdpSubjectBearer";
    public const string SubjectHeader = "X-Fanasa-Subject-Authorization";
    private const int MaximumBearerBytes = 16384;
    private const string ClientAuthOid = "1.3.6.1.5.5.7.3.2";

    /// <summary>Opt-in JWT handlers; changes no default scheme and maps no endpoint.</summary>
    public static IServiceCollection AddHrReadPdpAuthentication(this IServiceCollection services,
        HrReadPdpCallerSettings caller, HrReadReceiverSettings subject)
    {
        ArgumentNullException.ThrowIfNull(services); ArgumentNullException.ThrowIfNull(caller); ArgumentNullException.ThrowIfNull(subject);
        var callerSnapshot = caller with { }; var subjectSnapshot = subject with { };
        var configured = Compatible(callerSnapshot, subjectSnapshot);
        Add(CallerScheme, callerSnapshot.Issuer, callerSnapshot.Audience, callerSnapshot.ClientId, callerSnapshot.SubjectId,
            callerSnapshot.RequiredScope, "Authorization");
        Add(SubjectScheme, subjectSnapshot.Issuer, subjectSnapshot.Audience, subjectSnapshot.ClientId, subjectSnapshot.SubjectId,
            HrReadDesign.ReadScope, SubjectHeader);
        return services;

        void Add(string scheme, string issuer, string audience, string client, string sub, string scope, string header)
        {
            services.AddAuthentication().AddJwtBearer(scheme, options =>
            {
                options.MapInboundClaims = false; options.RequireHttpsMetadata = true; options.SaveToken = false;
                if (configured) options.Authority = issuer;
                options.Audience = audience;
                options.TokenValidationParameters = new()
                {
                    ValidateIssuer = true, ValidIssuer = issuer, ValidateAudience = true, ValidAudience = audience,
                    ValidateIssuerSigningKey = true, RequireSignedTokens = true, ValidateLifetime = true,
                    RequireExpirationTime = true, ClockSkew = TimeSpan.Zero, ValidTypes = ["JWT", "at+jwt"], NameClaimType = "sub"
                };
                options.Events.OnMessageReceived = context =>
                {
                    // In particular, the subject handler can never fall back to the Gateway Authorization bearer.
                    if (!configured || !IsEndpoint(context.HttpContext) || !SingleBearer(context.HttpContext, header, out var authorization))
                        context.NoResult();
                    else context.Token = authorization[7..];
                    return Task.CompletedTask;
                };
                options.Events.OnTokenValidated = context =>
                {
                    if (!configured || !CanonicalPayload(context.SecurityToken, issuer, audience, client, sub, scope))
                        context.Fail("Invalid HR PDP token.");
                    return Task.CompletedTask;
                };
            });
        }
    }

    public static async ValueTask<AuthenticatedHrReadPdpCaller?> AuthenticateAsync(HttpContext http,
        HrReadPdpCallerSettings callerSettings, HrReadReceiverSettings binding, HrReadPdpRequest request,
        IHrReadNativeCertificateValidator? certificates = null, TimeProvider? time = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http); ArgumentNullException.ThrowIfNull(callerSettings);
        ArgumentNullException.ThrowIfNull(binding); ArgumentNullException.ThrowIfNull(request);
        time ??= TimeProvider.System; certificates ??= new SystemHrReadNativeCertificateValidator();
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (!Compatible(callerSettings, binding) || request.Binding != binding || !IsEndpoint(http) || !HasNativeTls(http)
                || !SingleBearer(http, "Authorization", out var authorization)
                || !SingleBearer(http, SubjectHeader, out var subjectAuthorization)
                || Hash(subjectAuthorization[7..]) != request.TokenSha256 || time.GetUtcNow() >= request.ValidUntil) return null;
            var caller = await http.AuthenticateAsync(CallerScheme).WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var subject = await http.AuthenticateAsync(SubjectScheme).WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!caller.Succeeded || caller.Principal is null || caller.Ticket?.AuthenticationScheme != CallerScheme
                || !subject.Succeeded || subject.Principal is null || subject.Ticket?.AuthenticationScheme != SubjectScheme
                || !PrincipalMatches(caller.Principal, callerSettings.Issuer, callerSettings.Audience, callerSettings.ClientId,
                    callerSettings.SubjectId, callerSettings.RequiredScope, time.GetUtcNow(), out var callerExpiry)
                || !PrincipalMatches(subject.Principal, binding.Issuer, binding.Audience, binding.ClientId,
                    binding.SubjectId, HrReadDesign.ReadScope, time.GetUtcNow(), out var subjectExpiry)
                || subjectExpiry.ToUnixTimeSeconds() != request.TokenExpiresAt) return null;
            var native = await http.Connection.GetClientCertificateAsync(cancellationToken).WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (native is null) return null;
            using var certificate = X509CertificateLoader.LoadCertificate(native.RawData);
            if (!CurrentCertificate(certificate, callerSettings.PeerCertificateSha256, time.GetUtcNow())
                || !await certificates.ValidateAsync(certificate, callerSettings.PeerCertificateSha256, time.GetUtcNow(), cancellationToken)
                    .AsTask().WaitAsync(cancellationToken).ConfigureAwait(false)) return null;
            cancellationToken.ThrowIfCancellationRequested();
            var proof = new AuthenticatedHrReadPdpCaller(http, callerSettings, binding, request, caller.Principal, subject.Principal,
                certificate.RawData, http.Features.Get<ITlsHandshakeFeature>()!, authorization, subjectAuthorization,
                callerExpiry, subjectExpiry, new DateTimeOffset(certificate.NotAfter.ToUniversalTime()), time);
            return proof.RevalidateCurrent() ? proof : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return null; }
    }

    internal static bool Compatible(HrReadPdpCallerSettings caller, HrReadReceiverSettings subject) => caller.Validate().Count == 0
        && subject.Enabled && subject.Validate().Count == 0 && caller.Issuer == subject.Issuer
        && caller.PeerCertificateSha256 == subject.PeerCertificateSha256;
    internal static bool IsEndpoint(HttpContext http) => http.Request.Method == "POST"
        && http.Features.Get<IHttpRequestFeature>()?.RawTarget == HrReadPdpWire.EndpointPath;
    internal static bool HasNativeTls(HttpContext http) => http.Request.IsHttps
        && http.Features.Get<ITlsHandshakeFeature>() is { Protocol: SslProtocols.Tls12 or SslProtocols.Tls13 } handshake
        && ReferenceEquals(handshake, http.Features.Get<ITlsConnectionFeature>());
    internal static bool SingleBearer(HttpContext http, string header, out string value)
    {
        value = "";
        if (!http.Request.Headers.TryGetValue(header, out var values) || values.Count != 1 || values[0] is not { } candidate
            || candidate.Length is <= 7 or > MaximumBearerBytes || !candidate.StartsWith("Bearer ", StringComparison.Ordinal)
            || candidate.AsSpan(7).ContainsAnyExcept("abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-._~+/=")) return false;
        value = candidate; return true;
    }
    internal static bool CurrentCertificate(X509Certificate2 certificate, string pin, DateTimeOffset now)
    {
        if (!IsLowerHash(pin) || now.UtcDateTime < certificate.NotBefore.ToUniversalTime()
            || now.UtcDateTime >= certificate.NotAfter.ToUniversalTime()) return false;
        var eku = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().ToArray();
        return eku.Length == 1 && eku[0].EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value == ClientAuthOid)
            && CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.RawData), Convert.FromHexString(pin));
    }
    internal static bool IsLowerHash(string? value) => value is { Length: 64 }
        && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    internal static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    internal static string Fingerprint(ClaimsPrincipal principal) => Hash(JsonSerializer.Serialize(principal.Identities.Select(identity => new
    {
        identity.AuthenticationType, identity.NameClaimType, identity.RoleClaimType,
        Claims = identity.Claims.Select(claim => new { claim.Type, claim.Value, claim.ValueType, claim.Issuer, claim.OriginalIssuer }).ToArray()
    }).ToArray()));

    internal static bool PrincipalMatches(ClaimsPrincipal principal, string issuer, string audience, string client,
        string subject, string requiredScope, DateTimeOffset now, out DateTimeOffset expires)
    {
        expires = default;
        var identities = principal.Identities.Where(identity => identity.IsAuthenticated).Take(2).ToArray();
        if (identities.Length != 1) return false;
        var identity = identities[0];
        string? Single(string name)
        {
            var values = identity.Claims.Where(claim => claim.Type == name).Take(2).ToArray();
            return values.Length == 1 ? values[0].Value : null;
        }
        if (Single("iss") != issuer || Single("sub") != subject || Single("azp") != client) return false;
        var clients = identity.FindAll("client_id").ToArray(); var active = identity.FindAll("active").ToArray();
        if (clients.Length > 1 || clients.Length == 1 && clients[0].Value != client
            || active.Length > 1 || active.Length == 1 && active[0].Value != "true") return false;
        var audiences = identity.FindAll("aud").Select(claim => claim.Value).ToArray();
        if (audiences.Length is 0 or > 16 || audiences.Any(string.IsNullOrEmpty)
            || audiences.Distinct(StringComparer.Ordinal).Count() != audiences.Length || !audiences.Contains(audience, StringComparer.Ordinal)) return false;
        var scopes = Single("scope")?.Split(' ');
        if (scopes is null || scopes.Length is 0 or > 64 || scopes.Any(scope => scope.Length == 0
                || scope.Any(c => c < '!' || c > '~' || c is '"' or '\\' or ','))
            || scopes.Distinct(StringComparer.Ordinal).Count() != scopes.Length || !scopes.Contains(requiredScope, StringComparer.Ordinal)) return false;
        if (!NumericDate(Single("exp"), out expires) || now >= expires) return false;
        var nbf = identity.FindAll("nbf").Take(2).ToArray();
        return nbf.Length == 0 || nbf.Length == 1 && NumericDate(nbf[0].Value, out var start) && now >= start;
    }

    private static bool NumericDate(string? text, out DateTimeOffset value)
    {
        value = default;
        if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || seconds is < 0 or > 253402300799L || text != seconds.ToString(CultureInfo.InvariantCulture)) return false;
        value = DateTimeOffset.FromUnixTimeSeconds(seconds); return true;
    }
    private static bool CanonicalPayload(SecurityToken token, string issuer, string audience, string client, string subject, string scope)
    {
        var payload = token switch { JsonWebToken jwt => jwt.EncodedPayload, JwtSecurityToken jwt => jwt.RawPayload, _ => null };
        if (payload is null) return false;
        try
        {
            using var document = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(payload), new JsonDocumentOptions { MaxDepth = 8 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var seen = new HashSet<string>(StringComparer.Ordinal);
            if (root.EnumerateObject().Any(property => !seen.Add(property.Name))) return false;
            var claims = new List<Claim>();
            foreach (var name in new[] { "iss", "sub", "azp", "scope" })
            {
                if (!root.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String || property.GetString() is not { } text) return false;
                claims.Add(new(name, text));
            }
            foreach (var name in new[] { "client_id", "active", "exp", "nbf" })
            {
                if (!root.TryGetProperty(name, out var property)) { if (name == "exp") return false; continue; }
                if (name == "active") { if (property.ValueKind != JsonValueKind.True) return false; claims.Add(new(name, "true")); }
                else if (name == "client_id") { if (property.ValueKind != JsonValueKind.String || property.GetString() is not { } text) return false; claims.Add(new(name, text)); }
                else { if (property.ValueKind != JsonValueKind.Number || !NumericDate(property.GetRawText(), out _)) return false; claims.Add(new(name, property.GetRawText())); }
            }
            if (!root.TryGetProperty("aud", out var aud)) return false;
            var values = aud.ValueKind == JsonValueKind.Array ? aud.EnumerateArray().ToArray() : [aud];
            if (values.Length is 0 or > 16) return false;
            foreach (var item in values) { if (item.ValueKind != JsonValueKind.String || item.GetString() is not { } text) return false; claims.Add(new("aud", text)); }
            // JwtBearer already enforced signed lifetime with zero skew. Use the signed nbf only for this shape check.
            var start = claims.FirstOrDefault(claim => claim.Type == "nbf")?.Value;
            var shapeTime = DateTimeOffset.UnixEpoch;
            if (start is not null && !NumericDate(start, out shapeTime)) return false;
            return PrincipalMatches(new ClaimsPrincipal(new ClaimsIdentity(claims, "validated-jwt")), issuer, audience,
                client, subject, scope, shapeTime, out _);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or FormatException or InvalidOperationException) { return false; }
    }
}
