using System.Globalization;
using System.Security.Authentication;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fanasa.HrReadCandidate;
using Fanasa.HrReadCandidate.Http;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.Primitives;

namespace Fanasa.HrReadCandidate.Http.Tests;

public static class HttpReceiverTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly byte[] Key = Encoding.UTF8.GetBytes("fixture-http-key-at-least-32-bytes");
    private const string Tenant = "11111111-1111-4111-8111-111111111111";
    private const string Chart = "/api/organization-hr/v1/tenants/" + Tenant + "/chart";
    private const string Authorization = "Bearer fixture.header.signature";
    private const string Nonce = "g-0123456789abcdef0123456789abcdef";

    public static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool valid, string label) { count++; if (!valid) throw new InvalidOperationException("HTTP receiver test failed: " + label); }
        using var certificate = Certificate(clientEku: true, Now.AddDays(-1), Now.AddDays(1));
        var settings = Settings(certificate);
        Check(settings.Validate().Count == 0, "fixture settings are structurally valid, not provisioned");

        async Task<Fixture> Run(Action<Fixture>? change = null, IHrReadTrustedKeySource? keys = null,
            IHrReadHttpAuthoritySource? authority = null, IHrReadNativeCertificateValidator? validator = null)
        {
            var f = new Fixture(settings, certificate); change?.Invoke(f);
            await HrReadHttpEndpoint.ExecuteAsync(f.Http, f.Settings, f.Fabric, keys ?? new FixtureKeys(),
                authority ?? f.Source, validator ?? new FixtureCertificateValidator(), new FixtureTime());
            return f;
        }
        async Task Denied(Action<Fixture> change, string name)
        {
            var f = await Run(change);
            Check(f.Http.Response.StatusCode == 403 && f.Output.Length == 0
                && f.Http.Response.Headers.CacheControl == "no-store", name);
        }

        var success = await Run(f => f.Fabric.BeforeFresh = () => Check(f.Output.Length == 0, "no bytes before fresh gate"));
        Check(success.Http.Response.StatusCode == 200 && success.Http.Response.ContentType == "application/json"
            && success.Http.Response.Headers.CacheControl == "no-store", "successful coordinator response headers");
        Check(Encoding.UTF8.GetString(success.Output.ToArray()) == "{\"tenantId\":\"" + Tenant + "\",\"revision\":1}", "detached payload passes only after gate");
        Check(success.Fabric.Order.SequenceEqual(new[] { "claim", "read", "fresh" }), "HTTP composes coordinator transaction/snapshot order");
        Check(success.Authentication.Schemes.SequenceEqual(new[] { HrReadHttpAuthority.AuthenticationScheme }), "only named OrganizationHrBearer authenticates");
        Check(success.Peer.ReadCount == 1 && success.Source.Calls == 1 && success.Source.LastEvidence?.Envelope.RequestId == Nonce,
            "native certificate retrieval completes before trusted source");
        Check(success.Source.LastEvidence?.TokenValidUntil == Now.AddSeconds(60)
            && success.Source.LastEvidence?.NativeCertificateSha256 == settings.PeerCertificateSha256,
            "evidence binds authenticated token deadline and exact native peer");
        var optional = await Run(f => f.SetIdempotency("opaque-request-42"));
        Check(optional.Http.Response.StatusCode == 200 && optional.Source.LastEvidence?.Envelope.IdempotencyKey == "opaque-request-42",
            "optional opaque idempotency value is accepted and signed without a UUID requirement");

        var missing = await Run(f => f.Fabric.Found = false);
        Check(missing.Http.Response.StatusCode == 404 && missing.Output.Length == 0 && missing.Fabric.Order.Last() == "fresh",
            "404 waits for fresh authority too");
        await Denied(f => { f.Authentication.Result = AuthenticateResult.NoResult(); f.Http.User = new(new ClaimsIdentity([new("sub", settings.SubjectId)], "cookie")); },
            "cookie principal cannot replace named bearer authentication");
        foreach (var name in new[] { "Authorization", HrReadHttpAuthority.ContextHeader, HrReadHttpAuthority.SignatureHeader, "Idempotency-Key" })
            await Denied(f => f.Http.Request.Headers[name] = new StringValues(["first", "second"]), "duplicate " + name + " denied");
        await Denied(f => f.Http.Request.Headers[HrReadHttpAuthority.ContextHeader] =
            f.Http.Request.Headers[HrReadHttpAuthority.ContextHeader].ToString() + ",extra", "comma-joined context denied");
        await Denied(f => f.Http.Request.Headers.Authorization = "Bearer  fixture.header.signature", "ambiguous Bearer spacing denied");
        await Denied(f => f.Http.Request.Headers.Authorization = "Bearer fixture.header.signature ", "trailing Bearer whitespace denied");
        await Denied(f => f.Http.Request.Headers["Idempotency-Key"] = "invalid,key", "invalid optional idempotency bytes denied");
        await Denied(f => f.Http.Request.Headers[HrReadHttpAuthority.SignatureHeader] = new string('0', 64), "wrong signature denied");
        await Denied(f => f.Http.Request.Headers[HrReadHttpAuthority.SignatureHeader] = f.Http.Request.Headers[HrReadHttpAuthority.SignatureHeader].ToString().ToUpperInvariant(),
            "signature uppercase representation denied");
        await Denied(f => f.Http.Features.Get<IHttpRequestFeature>()!.RawTarget = Chart + "?x=1", "raw target is exact and has no query fallback");
        await Denied(f => f.Http.Features.Get<IHttpRequestFeature>()!.RawTarget = Chart.Replace("/chart", "/history/1"), "changed canonical target invalidates signed witness");
        await Denied(f => f.Http.Request.Method = "POST", "command method not admitted");
        var body = await Run(f => f.Http.Request.Body = new MemoryStream([1]));
        Check(body.Http.Response.StatusCode == 403 && body.Authentication.Schemes.Count == 0 && body.Fabric.Order.Count == 0,
            "actual nonempty GET body rejected before authentication and Fabric");

        await Denied(f => f.Http.Features.Set<ITlsHandshakeFeature>(null), "HTTPS string plus certificate feature is not a native handshake");
        await Denied(f => f.Http.Features.Set<ITlsHandshakeFeature>(new FixtureHandshake((SslProtocols)768)), "native TLS below 1.2 is denied");
        await Denied(f => { f.Peer.ClientCertificate = null; f.Http.Request.Headers["X-Forwarded-Client-Cert"] = Convert.ToBase64String(certificate.RawData); },
            "forwarded certificate header cannot supply native peer");
        await Denied(f => { f.Http.Request.Scheme = "http"; f.Http.Request.Headers["X-Forwarded-Proto"] = "https"; }, "forwarded scheme cannot supply native HTTPS");
        await Denied(f => f.Settings = settings with { PeerCertificateSha256 = new string('0', 64) }, "same peer must match exact DER SHA256 pin");
        using var expired = Certificate(clientEku: true, Now.AddDays(-2), Now.AddDays(-1));
        await Denied(f => { f.Peer.ClientCertificate = expired; f.Settings = settings with { PeerCertificateSha256 = Pin(expired) }; }, "expired native certificate denied despite permissive fixture chain seam");
        using var noEku = Certificate(clientEku: false, Now.AddDays(-1), Now.AddDays(1));
        await Denied(f => { f.Peer.ClientCertificate = noEku; f.Settings = settings with { PeerCertificateSha256 = Pin(noEku) }; }, "native certificate needs explicit clientAuth EKU");
        Check(!await new SystemHrReadNativeCertificateValidator().ValidateAsync(certificate, Pin(certificate), Now, CancellationToken.None),
            "production system trust rejects untrusted self-signed fixture without installing it");

        foreach (var claim in new[] { "iss", "sub", "azp", "aud", "scope", "exp" })
            await Denied(f => f.Authentication.Result = Auth(Principal(settings, identity => { foreach (var item in identity.FindAll(claim).ToArray()) identity.RemoveClaim(item); })),
                "named principal requires canonical " + claim);
        await Denied(f => f.Authentication.Result = Auth(Principal(settings, identity => identity.AddClaim(new("exp", Now.AddSeconds(60).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))))),
            "duplicate token expiry denied");
        await Denied(f => f.Authentication.Result = Auth(Principal(settings, identity => { identity.RemoveClaim(identity.FindFirst("exp")!); identity.AddClaim(new("exp", Now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))); })),
            "authenticated expiry boundary remains closed");
        await Denied(f => f.Authentication.Result = Auth(Principal(settings, identity => { identity.RemoveClaim(identity.FindFirst("exp")!); identity.AddClaim(new("exp", Now.AddSeconds(61).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))); })),
            "signed token expiry must equal authenticated token expiry");
        await Denied(f => f.Authentication.Result = Auth(Principal(settings, identity => identity.AddClaim(new("nbf", Now.AddSeconds(1).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))))),
            "future authenticated not-before denied");
        await Denied(f => f.Source.Resolve = e => new FixtureAdmission(e.Envelope.Target, settings.MaximumResponseBytes, Now.AddSeconds(4)), "source cannot extend signed witness deadline");
        await Denied(f => f.Source.Resolve = e => new FixtureAdmission(e.Envelope.Target, settings.MaximumResponseBytes + 1, Now.AddSeconds(2)), "source cannot enlarge configured response cap");
        await Denied(f => f.Source.Resolve = e => new FixtureAdmission(e.Envelope.Target with { TenantId = Guid.NewGuid() }, settings.MaximumResponseBytes, Now.AddSeconds(2)), "source cannot change signed target");

        var unavailableKeys = await Run(keys: new UnavailableHrReadTrustedKeySource());
        Check(unavailableKeys.Http.Response.StatusCode == 403 && unavailableKeys.Source.Calls == 0 && unavailableKeys.Fabric.Order.Count == 0,
            "missing trusted key source closes before authority and Fabric");
        var unavailableSource = await Run(authority: new UnavailableHrReadHttpAuthoritySource());
        Check(unavailableSource.Http.Response.StatusCode == 403 && unavailableSource.Fabric.Order.Count == 0, "missing authority source always denies");
        var defaultDependencies = new Fixture(settings, certificate);
        await HrReadHttpEndpoint.ExecuteAsync(defaultDependencies.Http, settings, time: new FixtureTime());
        Check(defaultDependencies.Http.Response.StatusCode == 403 && defaultDependencies.Output.Length == 0, "unconfigured receiver default is closed");
        var noFabric = new Fixture(settings, certificate);
        await HrReadHttpEndpoint.ExecuteAsync(noFabric.Http, settings, keys: new FixtureKeys(), authority: noFabric.Source,
            certificates: new FixtureCertificateValidator(), time: new FixtureTime());
        Check(noFabric.Http.Response.StatusCode == 403 && noFabric.Output.Length == 0, "missing Fabric dependency is closed");
        var veto = await Run(f => f.Fabric.FreshAllowed = false);
        Check(veto.Http.Response.StatusCode == 403 && veto.Output.Length == 0 && veto.Fabric.Order.Last() == "fresh", "fresh gate veto emits no bytes");

        var sending = new Fixture(settings, certificate); sending.Http.Response.Body = new ThrowingWriteStream();
        var propagated = false;
        try { await HrReadHttpEndpoint.ExecuteAsync(sending.Http, settings, sending.Fabric, new FixtureKeys(), sending.Source, new FixtureCertificateValidator(), new FixtureTime()); }
        catch (IOException) { propagated = true; }
        Check(propagated && sending.Http.Response.StatusCode == 200, "send failure propagates and is never relabeled denial");
        return count;
    }

    private sealed class Fixture
    {
        public Fixture(HrReadReceiverSettings settings, X509Certificate2 certificate)
        {
            Settings = settings; Authentication = new(Auth(Principal(settings)));
            Http = new DefaultHttpContext(); Http.RequestServices = new AuthenticationProvider(Authentication);
            Http.Request.Scheme = "https"; Http.Request.Method = "GET"; Http.Request.Path = Chart;
            Http.Features.Get<IHttpRequestFeature>()!.RawTarget = Chart;
            Http.Request.Body = new MemoryStream(); Output = new MemoryStream(); Http.Response.Body = Output;
            Peer = new(certificate); Http.Features.Set<ITlsConnectionFeature>(Peer); Http.Features.Set<ITlsHandshakeFeature>(new FixtureHandshake());
            var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(Envelope(settings).ToJsonString()));
            Http.Request.Headers.Authorization = Authorization; Http.Request.Headers[HrReadHttpAuthority.ContextHeader] = encoded;
            Http.Request.Headers[HrReadHttpAuthority.SignatureHeader] = Convert.ToHexStringLower(HrReadWire.Sign(HrReadWire.BuildFrame("GET", Chart, Authorization, encoded, "", []), Key));
            Source = new(settings); Fabric = new();
        }
        public HrReadReceiverSettings Settings { get; set; }
        public DefaultHttpContext Http { get; }
        public MemoryStream Output { get; }
        public FixtureAuthentication Authentication { get; }
        public FixturePeer Peer { get; }
        public FixtureAuthority Source { get; }
        public FixtureFabric Fabric { get; }
        public void SetIdempotency(string value)
        {
            Http.Request.Headers["Idempotency-Key"] = value;
            var encoded = Http.Request.Headers[HrReadHttpAuthority.ContextHeader].ToString();
            Http.Request.Headers[HrReadHttpAuthority.SignatureHeader] = Convert.ToHexStringLower(HrReadWire.Sign(HrReadWire.BuildFrame("GET", Chart, Authorization, encoded, value, []), Key));
        }
    }
    private sealed class FixtureAdmission(HrReadTarget target, int maximum, DateTimeOffset expiry) : HrReadAdmission(target, maximum, expiry);
    private sealed class FixtureAuthority(HrReadReceiverSettings settings) : IHrReadHttpAuthoritySource
    {
        public int Calls { get; private set; }
        public HrReadHttpEvidence? LastEvidence { get; private set; }
        public Func<HrReadHttpEvidence, HrReadAdmission?>? Resolve { get; set; }
        public ValueTask<HrReadAdmission?> ResolveAsync(HrReadHttpEvidence evidence, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Calls++; LastEvidence = evidence;
            return ValueTask.FromResult(Resolve is null ? new FixtureAdmission(evidence.Envelope.Target, settings.MaximumResponseBytes, Now.AddSeconds(2)) : Resolve(evidence));
        }
    }
    private sealed class FixtureKeys : IHrReadTrustedKeySource
    {
        public ValueTask<ReadOnlyMemory<byte>?> ResolveAsync(string keyId, string tier, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult<ReadOnlyMemory<byte>?>(Key); }
    }
    private sealed class FixtureCertificateValidator : IHrReadNativeCertificateValidator
    {
        public ValueTask<bool> ValidateAsync(X509Certificate2 certificate, string expectedSha256, DateTimeOffset now, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(true); }
    }
    private sealed class FixtureFabric : IHrReadFabricAdapter
    {
        public List<string> Order { get; } = [];
        public Action? BeforeFresh { get; set; }
        public bool FreshAllowed { get; set; } = true;
        public bool Found { get; set; } = true;
        public bool TryClaimNonceInTransaction(HrReadAdmission admission, CancellationToken cancellationToken) { Order.Add("claim"); return true; }
        public MaterializedHrRead ReadInSnapshot(HrReadAdmission admission, CancellationToken cancellationToken)
        { Order.Add("read"); return Found ? MaterializedHrRead.Existing(JsonSerializer.SerializeToElement(new { tenantId = Tenant, revision = 1 })) : MaterializedHrRead.NotFound(); }
        public bool RevalidateInNewSnapshot(HrReadAdmission admission, CancellationToken cancellationToken) { Order.Add("fresh"); BeforeFresh?.Invoke(); return FreshAllowed; }
    }
    private sealed class FixtureTime : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class FixturePeer(X509Certificate2 certificate) : ITlsConnectionFeature
    {
        public X509Certificate2? ClientCertificate { get; set; } = certificate;
        public int ReadCount { get; private set; }
        public Task<X509Certificate2?> GetClientCertificateAsync(CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); ReadCount++; return Task.FromResult(ClientCertificate); }
    }
    private sealed class FixtureHandshake(SslProtocols protocol = SslProtocols.Tls13) : ITlsHandshakeFeature
    {
        public SslProtocols Protocol => protocol;
        // ITlsHandshakeFeature still requires these legacy members in .NET 10.
        // This isolated mock implements them without using them for validation.
#pragma warning disable SYSLIB0058
        public CipherAlgorithmType CipherAlgorithm => CipherAlgorithmType.Aes256;
        public int CipherStrength => 256;
        public HashAlgorithmType HashAlgorithm => HashAlgorithmType.Sha256;
        public int HashStrength => 256;
        public ExchangeAlgorithmType KeyExchangeAlgorithm => ExchangeAlgorithmType.DiffieHellman;
        public int KeyExchangeStrength => 256;
#pragma warning restore SYSLIB0058
    }
    private sealed class FixtureAuthentication(AuthenticateResult result) : IAuthenticationService
    {
        public AuthenticateResult Result { get; set; } = result;
        public List<string?> Schemes { get; } = [];
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) { Schemes.Add(scheme); return Task.FromResult(Result); }
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) => throw new NotSupportedException();
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();
    }
    private sealed class AuthenticationProvider(IAuthenticationService authentication) : IServiceProvider
    { public object? GetService(Type type) => type == typeof(IAuthenticationService) ? authentication : null; }
    private sealed class ThrowingWriteStream : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException(new IOException("Fixture transport send failed."));
    }
    private static AuthenticateResult Auth(ClaimsPrincipal principal) => AuthenticateResult.Success(new AuthenticationTicket(principal, HrReadHttpAuthority.AuthenticationScheme));
    private static ClaimsPrincipal Principal(HrReadReceiverSettings s, Action<ClaimsIdentity>? change = null)
    {
        var identity = new ClaimsIdentity([new("iss", s.Issuer), new("sub", s.SubjectId), new("azp", s.ClientId), new("aud", s.Audience),
            new("scope", HrReadDesign.ReadScope), new("exp", Now.AddSeconds(60).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture))], "fixture-named-bearer");
        change?.Invoke(identity); return new(identity);
    }
    private static X509Certificate2 Certificate(bool clientEku, DateTimeOffset from, DateTimeOffset to)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=isolated-hr-fixture", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        if (clientEku) request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.2") }, true));
        using var generated = request.CreateSelfSigned(from, to);
        return X509CertificateLoader.LoadCertificate(generated.RawData);
    }
    private static string Pin(X509Certificate2 certificate) => Convert.ToHexStringLower(SHA256.HashData(certificate.RawData));
    private static HrReadReceiverSettings Settings(X509Certificate2 certificate) => new()
    {
        Enabled = true, SchemaVersion = HrReadDesign.SchemaVersion, ProfileId = HrReadDesign.ProfileId,
        RegisteredProductId = "22222222-2222-4222-8222-222222222222", ProductKey = HrReadDesign.ProductKey,
        Owner = "platform", OwnerTenantId = null, ConsumerTenantId = Tenant,
        Issuer = "https://sso.example.test/realms/fanasa", Audience = HrReadDesign.Audience,
        ClientId = "fixture-client", SubjectId = "33333333-3333-4333-8333-333333333333",
        Environment = "development", Zone = "fixture-zone", ContractId = "fixture-contract", ContractVersion = 1,
        PolicyId = "fixture-policy", PolicyVersion = 1, AllocationId = "fixture-allocation", ConfigRevision = 1,
        CanonicalScope = "tenant=" + Tenant + ";products=organization-fabric;environments=development;groups=organization.hr.read",
        KeyId = "fixture-key", Tier = "internal", PeerCertificateSha256 = Pin(certificate), MaximumResponseBytes = 1024, MaximumWitnessSeconds = 5
    };
    private static JsonObject Envelope(HrReadReceiverSettings s) => new()
    {
        ["envelopeVersion"] = HrReadEnvelopeVerifier.EnvelopeVersion, ["schemaVersion"] = HrReadDesign.SchemaVersion,
        ["profileId"] = HrReadDesign.ProfileId, ["keyId"] = s.KeyId, ["tier"] = s.Tier,
        ["issuedAt"] = Now.ToUnixTimeSeconds(), ["expiresAt"] = Now.ToUnixTimeSeconds() + 3,
        ["tokenSha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Authorization[7..]))),
        ["context"] = new JsonObject {
            ["requestId"] = Nonce, ["decisionId"] = Nonce, ["admissionId"] = "admission:" + Nonce,
            ["method"] = "GET", ["rawTarget"] = Chart, ["subjectId"] = s.SubjectId, ["clientId"] = s.ClientId,
            ["consumerTenantId"] = s.ConsumerTenantId, ["ownerTenantId"] = null,
            ["ownership"] = new JsonObject { ["kind"] = "Platform", ["owner"] = s.Owner, ["registeredProductId"] = s.RegisteredProductId },
            ["productId"] = s.ProductKey, ["audience"] = s.Audience, ["issuer"] = s.Issuer, ["scope"] = HrReadDesign.ReadScope,
            ["environment"] = s.Environment, ["zone"] = s.Zone, ["action"] = HrReadDesign.Action(HrReadEndpointKind.Chart),
            ["operationGroup"] = HrReadDesign.Group, ["contractId"] = s.ContractId, ["contractVersion"] = s.ContractVersion,
            ["policyId"] = s.PolicyId, ["policyVersion"] = s.PolicyVersion, ["allocationId"] = s.AllocationId,
            ["allocationScope"] = s.CanonicalScope, ["configRevision"] = s.ConfigRevision, ["allocationPeriodId"] = "2026-10",
            ["periodStartMs"] = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            ["periodEndMs"] = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds(),
            ["decisionValidUntil"] = Now.ToUnixTimeSeconds() + 4, ["tokenExpiresAt"] = Now.ToUnixTimeSeconds() + 60,
            ["allowed"] = true, ["confirmed"] = true }
    };
}
