using System.Globalization;
using System.Security.Authentication;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;

namespace Fanasa.HrReadCandidate.Http;

public interface IHrReadTrustedKeySource
{
    /// <summary>Resolve only the host-configured key/tier from a trusted source, never from a request-provided key URI.</summary>
    ValueTask<ReadOnlyMemory<byte>?> ResolveAsync(string keyId, string tier, CancellationToken cancellationToken);
}

public sealed class UnavailableHrReadTrustedKeySource : IHrReadTrustedKeySource
{
    public ValueTask<ReadOnlyMemory<byte>?> ResolveAsync(string keyId, string tier, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult<ReadOnlyMemory<byte>?>(null); }
}

public interface IHrReadHttpAuthoritySource
{
    /// <summary>
    /// Independently resolve authenticated, unique introspection consumer and current registry,
    /// contract, policy, grant and allocation evidence. Constructing evidence grants nothing.
    /// The returned admission must retain what the host's synchronous current guard needs.
    /// No async operation may remain pending when the Fabric transaction/snapshots start.
    /// </summary>
    ValueTask<HrReadAdmission?> ResolveAsync(HrReadHttpEvidence evidence, CancellationToken cancellationToken);
}

public sealed class UnavailableHrReadHttpAuthoritySource : IHrReadHttpAuthoritySource
{
    public ValueTask<HrReadAdmission?> ResolveAsync(HrReadHttpEvidence evidence, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult<HrReadAdmission?>(null); }
}

/// <summary>Verified transport/authentication evidence only. This is neither introspection nor authorization.</summary>
public sealed class HrReadHttpEvidence
{
    private readonly ClaimsPrincipal principal;
    private readonly byte[] certificateDer;
    internal HrReadHttpEvidence(HttpContext requestContext, VerifiedHrReadEnvelope envelope,
        ClaimsPrincipal authenticatedPrincipal, byte[] nativeCertificateDer, string certificateSha256,
        DateTimeOffset tokenValidUntil, DateTimeOffset observedAt)
    {
        RequestContext = requestContext; Envelope = envelope;
        principal = new ClaimsPrincipal(authenticatedPrincipal.Identities.Select(identity => new ClaimsIdentity(identity)));
        certificateDer = nativeCertificateDer.ToArray(); NativeCertificateSha256 = certificateSha256;
        TokenValidUntil = tokenValidUntil; ObservedAt = observedAt;
    }
    /// <summary>Retained original request for the host's final current guard; headers remain untrusted input.</summary>
    public HttpContext RequestContext { get; }
    public VerifiedHrReadEnvelope Envelope { get; }
    public ClaimsPrincipal AuthenticatedPrincipal => new(principal.Identities.Select(identity => new ClaimsIdentity(identity)));
    public byte[] GetNativeCertificateDer() => certificateDer.ToArray();
    public string NativeCertificateSha256 { get; }
    public DateTimeOffset TokenValidUntil { get; }
    public DateTimeOffset ObservedAt { get; }
}

public interface IHrReadNativeCertificateValidator
{
    ValueTask<bool> ValidateAsync(X509Certificate2 certificate, string expectedSha256,
        DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>
/// Actual native peer certificate validation with system trust and online revocation.
/// It never installs roots, accepts forwarded certificates or suppresses chain errors.
/// All potentially blocking chain retrieval finishes before any Fabric operation.
/// </summary>
public sealed class SystemHrReadNativeCertificateValidator : IHrReadNativeCertificateValidator
{
    private const string ClientAuthenticationOid = "1.3.6.1.5.5.7.3.2";
    public async ValueTask<bool> ValidateAsync(X509Certificate2 certificate, string expectedSha256,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(certificate);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (!HasCurrentIdentityAndPin(certificate, expectedSha256, now)) return false;
            // Own only the public certificate bytes while chain work is running.
            using var owned = X509CertificateLoader.LoadCertificate(certificate.RawData);
            var accepted = await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.System;
                chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
                chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
                chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
                chain.ChainPolicy.VerificationTime = now.UtcDateTime;
                chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(2);
                chain.ChainPolicy.ApplicationPolicy.Add(new Oid(ClientAuthenticationOid));
                var valid = chain.Build(owned) && chain.ChainStatus.Length == 0;
                cancellationToken.ThrowIfCancellationRequested();
                return valid;
            }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return accepted;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is CryptographicException or ArgumentException or InvalidOperationException)
        { return false; }
    }

    internal static bool HasCurrentIdentityAndPin(X509Certificate2 certificate, string expectedSha256, DateTimeOffset now)
    {
        if (now.UtcDateTime < certificate.NotBefore.ToUniversalTime() || now.UtcDateTime >= certificate.NotAfter.ToUniversalTime()
            || expectedSha256 is not { Length: 64 }
            || expectedSha256.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            return false;
        var eku = certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>().ToArray();
        if (eku.Length != 1 || !eku[0].EnhancedKeyUsages.Cast<Oid>().Any(oid => oid.Value == ClientAuthenticationOid)) return false;
        return CryptographicOperations.FixedTimeEquals(SHA256.HashData(certificate.RawData), Convert.FromHexString(expectedSha256));
    }
}

/// <summary>Request-scoped opt-in HTTP adapter. Its missing host sources always deny.</summary>
public sealed class HrReadHttpAuthority : IHrReadAuthority
{
    public const string AuthenticationScheme = "OrganizationHrBearer";
    public const string ContextHeader = "X-Fanasa-Context";
    public const string SignatureHeader = "X-Fanasa-Context-Signature";
    private readonly HttpContext http;
    private readonly HrReadReceiverSettings settings;
    private readonly IHrReadTrustedKeySource keys;
    private readonly IHrReadHttpAuthoritySource source;
    private readonly IHrReadNativeCertificateValidator certificates;
    private readonly TimeProvider time;

    public HrReadHttpAuthority(HttpContext http, HrReadReceiverSettings settings,
        IHrReadTrustedKeySource? keys = null, IHrReadHttpAuthoritySource? source = null,
        IHrReadNativeCertificateValidator? certificates = null, TimeProvider? time = null)
    {
        this.http = http ?? throw new ArgumentNullException(nameof(http));
        this.settings = (settings ?? throw new ArgumentNullException(nameof(settings))) with { };
        this.keys = keys ?? new UnavailableHrReadTrustedKeySource();
        this.source = source ?? new UnavailableHrReadHttpAuthoritySource();
        this.certificates = certificates ?? new SystemHrReadNativeCertificateValidator(); this.time = time ?? TimeProvider.System;
    }

    public async ValueTask<HrReadAdmission?> AuthenticateAndResolveAsync(HrReadTarget target, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (!settings.Enabled || settings.Validate().Count != 0 || !http.Request.IsHttps
                || http.Features.Get<ITlsHandshakeFeature>()?.Protocol is not (SslProtocols.Tls12 or SslProtocols.Tls13)
                || http.Features.Get<ITlsConnectionFeature>() is null
                || http.Features.Get<IHttpRequestFeature>()?.RawTarget is not string rawTarget
                || !HrReadWire.TryParseTarget(http.Request.Method, rawTarget, out var parsedTarget) || parsedTarget != target
                || !SingleHeader(http, "Authorization", required: true, HrReadWire.MaximumAuthorizationBytes, out var authorization)
                || !CanonicalBearer(authorization)
                || !SingleHeader(http, ContextHeader, required: true, HrReadWire.MaximumEnvelopeBytes, out var encoded)
                || !SingleHeader(http, SignatureHeader, required: true, 64, out var signature)
                || !SingleHeader(http, "Idempotency-Key", required: false, 128, out var idempotency)) return null;

            // Never inspect http.User as an authentication fallback. Only this named bearer result is trusted.
            var authentication = await http.AuthenticateAsync(AuthenticationScheme).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (!authentication.Succeeded || authentication.Principal is null
                || authentication.Ticket?.AuthenticationScheme != AuthenticationScheme
                || !PrincipalMatches(authentication.Principal, settings, time.GetUtcNow(), out var tokenExpiry)) return null;

            // Connection's native certificate and handshake features, never a header-provided certificate.
            // The host must keep certificate-forwarding middleware off this direct native-TLS route.
            var native = await http.Connection.GetClientCertificateAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (native is null) return null;
            using var certificate = X509CertificateLoader.LoadCertificate(native.RawData);
            if (!SystemHrReadNativeCertificateValidator.HasCurrentIdentityAndPin(certificate, settings.PeerCertificateSha256, time.GetUtcNow())
                || !await certificates.ValidateAsync(certificate, settings.PeerCertificateSha256, time.GetUtcNow(), cancellationToken).ConfigureAwait(false)) return null;
            cancellationToken.ThrowIfCancellationRequested();

            var key = await keys.ResolveAsync(settings.KeyId, settings.Tier, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (key is null || key.Value.Length is < HrReadWire.MinimumKeyBytes or > HrReadWire.MaximumKeyBytes) return null;
            VerifiedHrReadEnvelope? envelope;
            var ownedKey = key.Value.ToArray();
            try { envelope = HrReadEnvelopeVerifier.Verify(http.Request.Method, rawTarget, authorization, idempotency,
                encoded, signature, ownedKey, settings, time.GetUtcNow()); }
            finally { CryptographicOperations.ZeroMemory(ownedKey); }
            if (envelope is null || envelope.Target != target || envelope.TokenExpiresAt != tokenExpiry.ToUnixTimeSeconds()) return null;

            var evidence = new HrReadHttpEvidence(http, envelope, authentication.Principal, certificate.RawData,
                Convert.ToHexStringLower(SHA256.HashData(certificate.RawData)), tokenExpiry, time.GetUtcNow());
            var admission = await source.ResolveAsync(evidence, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return admission is not null && admission.Target == target
                && admission.MaximumResponseBytes <= settings.MaximumResponseBytes
                && admission.ValidUntil <= envelope.ValidUntil && admission.ValidUntil <= tokenExpiry
                && admission.ValidUntil <= new DateTimeOffset(certificate.NotAfter.ToUniversalTime())
                && SystemHrReadNativeCertificateValidator.HasCurrentIdentityAndPin(certificate, settings.PeerCertificateSha256, time.GetUtcNow())
                && time.GetUtcNow() < admission.ValidUntil ? admission : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return null; } // No bearer, witness, certificate or business payload in failure output.
    }

    private static bool SingleHeader(HttpContext http, string name, bool required, int maximum, out string value)
    {
        value = "";
        if (!http.Request.Headers.TryGetValue(name, out var values)) return !required;
        if (values.Count != 1 || values[0] is not string text || text.Length is 0 || text.Length > maximum
            || text.Any(c => c < 32 || c > 126 || c == ',')) return false;
        value = text; return true;
    }
    private static bool CanonicalBearer(string authorization) => authorization.Length > 7
        && authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
        && authorization.AsSpan(7).IndexOf(' ') < 0;

    private static bool PrincipalMatches(ClaimsPrincipal principal, HrReadReceiverSettings settings, DateTimeOffset now,
        out DateTimeOffset tokenExpiry)
    {
        tokenExpiry = default;
        var authenticated = principal.Identities.Where(identity => identity.IsAuthenticated).Take(2).ToArray();
        if (authenticated.Length != 1) return false;
        var identity = authenticated[0];
        static string? Single(ClaimsIdentity identity, string claim)
        {
            var matches = identity.Claims.Where(item => item.Type == claim).Take(2).ToArray();
            return matches.Length == 1 ? matches[0].Value : null;
        }
        if (Single(identity, "iss") != settings.Issuer || Single(identity, "sub") != settings.SubjectId
            || Single(identity, "azp") != settings.ClientId) return false;
        var clientIds = identity.Claims.Where(item => item.Type == "client_id").Take(2).ToArray();
        if (clientIds.Length > 1 || clientIds.Length == 1 && clientIds[0].Value != settings.ClientId) return false;
        var audiences = identity.Claims.Where(item => item.Type == "aud").Select(item => item.Value).ToArray();
        if (audiences.Length is 0 or > 16 || audiences.Any(string.IsNullOrEmpty)
            || audiences.Distinct(StringComparer.Ordinal).Count() != audiences.Length
            || !audiences.Contains(settings.Audience, StringComparer.Ordinal)) return false;
        var scopeText = Single(identity, "scope");
        if (scopeText is null || scopeText.Length > 4096 || scopeText.Any(c => c < 32 || c > 126)) return false;
        var scopes = scopeText.Split(' ');
        if (scopes.Length > 64 || scopes.Any(string.IsNullOrEmpty) || scopes.Distinct(StringComparer.Ordinal).Count() != scopes.Length
            || !scopes.Contains(HrReadDesign.ReadScope, StringComparer.Ordinal)) return false;
        var expiry = Single(identity, "exp");
        if (expiry is null || !long.TryParse(expiry, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || seconds <= 0 || seconds > 253402300799L || expiry != seconds.ToString(CultureInfo.InvariantCulture)) return false;
        tokenExpiry = DateTimeOffset.FromUnixTimeSeconds(seconds);
        var notBefore = identity.Claims.Where(item => item.Type == "nbf").Take(2).ToArray();
        if (notBefore.Length > 1 || notBefore.Length == 1
            && (!long.TryParse(notBefore[0].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var notBeforeSeconds)
                || notBeforeSeconds < 0 || notBeforeSeconds > 253402300799L
                || notBefore[0].Value != notBeforeSeconds.ToString(CultureInfo.InvariantCulture)
                || now < DateTimeOffset.FromUnixTimeSeconds(notBeforeSeconds))) return false;
        return now < tokenExpiry;
    }
}
