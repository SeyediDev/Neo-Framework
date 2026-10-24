using System.Globalization;
using System.Security.Authentication;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using Fanasa.HrReadCandidate;
using Fanasa.HrReadCandidate.Http;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Fanasa.HrReadPdp.Tests;

/// <summary>Isolated explicit authentication/certificate/time fixtures; no live TLS, JWT, introspection or central grant acceptance.</summary>
public static class HttpTests
{
    internal static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    private const string SubjectToken = "fixture.subject.token";
    private const string CallerToken = "fixture.gateway.token";

    public static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool pass, string label) { count++; if (!pass) throw new InvalidOperationException("PDP HTTP test failed: " + label); }
        async Task Denied(Action<Fixture> change, string label)
        {
            using var fixture = new Fixture(); change(fixture);
            Check(await fixture.Authenticate() is null, label);
        }
        using (var fixture = new Fixture())
        {
            var proof = await fixture.Authenticate();
            Check(proof is not null && proof.RevalidateCurrent(), "explicit separate bearer channels and native same-feature peer");
            var anotherRequest = HrReadPdpWire.Parse(Encoding.UTF8.GetBytes(WireTests.Fixture(fixture.Binding).ToJsonString()), fixture.Binding, Now)!;
            Check(proof!.IsFor(fixture.Request) && !proof.IsFor(anotherRequest), "proof retained only for the exact parsed request instance");
            Check(fixture.Authentication.Schemes.SequenceEqual(new[] { HrReadPdpAuthentication.CallerScheme, HrReadPdpAuthentication.SubjectScheme }), "only exact named schemes authenticate");
            Check(fixture.Certificates.Reads == 1 && fixture.Peer.Reads == 1, "native certificate and validator run before authority");
            Check(proof?.ValidUntil == Now.AddSeconds(5) && proof.CallerValidUntil == Now.AddSeconds(60)
                && proof.SubjectValidUntil == Now.AddSeconds(60), "request deadline caps separate signed deadlines");
            Check(proof?.SubjectTokenSha256 == Hash(SubjectToken) && proof.HeaderSha256 == Hash("Bearer " + CallerToken)
                && proof.SubjectHeaderSha256 == Hash("Bearer " + SubjectToken), "exact authorization headers and raw subject token have distinct hashes");
            var clone = proof!.SubjectPrincipal; ((ClaimsIdentity)clone.Identity!).AddClaim(new("scope", "fake"));
            Check(proof.RevalidateCurrent(), "public principal copy cannot mutate retained authentication proof");
            var der = proof.GetNativeCertificateDer(); Array.Fill<byte>(der, 0);
            Check(proof.RevalidateCurrent(), "public certificate copy cannot mutate retained DER proof");
            fixture.Time.Current = Now.AddSeconds(5);
            Check(!proof.RevalidateCurrent(), "final proof deadline is exclusive");
        }
        await Denied(f => f.Http.Request.Scheme = "http", "HTTP transport denied");
        await Denied(f => f.Http.Features.Set<ITlsHandshakeFeature>(null), "HTTPS without native handshake denied");
        await Denied(f => f.Peer.Protocol = SslProtocols.None, "unknown native TLS version denied");
        await Denied(f => f.Http.Features.Set<ITlsConnectionFeature>(new Peer(f.Certificate)), "forwarded/replaced certificate feature denied even same DER");
        await Denied(f => f.Http.Request.Headers["Authorization"] = new[] { "Bearer " + CallerToken, "Bearer " + CallerToken }, "duplicate gateway authorization denied");
        await Denied(f => f.Http.Request.Headers[HrReadPdpAuthentication.SubjectHeader] = "Bearer " + SubjectToken + ",Bearer " + SubjectToken, "comma-joined subject header denied");
        await Denied(f => f.Http.Request.Headers[HrReadPdpAuthentication.SubjectHeader] = "Bearer  " + SubjectToken, "noncanonical bearer spacing denied");
        await Denied(f => f.Http.Request.Headers[HrReadPdpAuthentication.SubjectHeader] = "bearer " + SubjectToken, "bearer syntax is canonical and explicit");
        await Denied(f => f.Http.Request.Headers.Remove(HrReadPdpAuthentication.SubjectHeader), "subject handler never falls back to gateway Authorization");
        await Denied(f => f.Http.Request.Headers[HrReadPdpAuthentication.SubjectHeader] = "Bearer other.token", "raw subject token hash bound to request");
        await Denied(f => f.Authentication.Caller = AuthenticateResult.NoResult(), "authenticated cookie User cannot replace named caller authentication");
        await Denied(f => f.Authentication.Subject = AuthenticateResult.NoResult(), "authenticated cookie User cannot replace named subject authentication");
        await Denied(f => f.Authentication.Subject = Auth(f.SubjectPrincipal, HrReadPdpAuthentication.CallerScheme), "wrong subject ticket scheme denied");
        await Denied(f => f.Authentication.Caller = Auth(f.CallerPrincipal, HrReadPdpAuthentication.SubjectScheme), "wrong caller ticket scheme denied");
        await Denied(f => f.Certificates.Allowed = false, "native chain validator failure denied");
        await Denied(f => f.Binding = f.Binding with { PeerCertificateSha256 = new string('f', 64) }, "caller/HR peer pins cannot diverge");
        await Denied(f => f.CallerSettings = f.CallerSettings with { Enabled = false }, "disabled caller configuration denies");
        await Denied(f => f.CallerSettings = f.CallerSettings with { Issuer = "https://sso.example.test/realms/fanasa/" }, "noncanonical issuer denies");
        await Denied(f => f.CallerSettings = f.CallerSettings with { RequiredScope = "two scopes" }, "caller scope is a single configured identifier");
        await Denied(f => f.Time.Current = Now.AddSeconds(5), "authentication cannot revive expired body assertion");
        await Denied(f => f.Certificates.BeforeReturn = () => f.Time.Current = Now.AddSeconds(5), "awaited chain work cannot revive expired body");
        await Denied(f => f.Authentication.BeforeReturn = () => f.Time.Current = Now.AddSeconds(61), "awaited authentication cannot outlive signed expiry");
        foreach (var subject in new[] { false, true })
        {
            foreach (var name in new[] { "iss", "sub", "azp", "scope", "exp", "nbf", "client_id", "active", "aud" })
            {
                await Denied(f =>
                {
                    var principal = subject ? f.SubjectPrincipal : f.CallerPrincipal;
                    var identity = (ClaimsIdentity)principal.Identity!;
                    identity.AddClaim(new(name, name switch { "active" => "false", "nbf" => "9999999999", "exp" => "0",
                        "aud" => identity.FindFirst("aud")!.Value, _ => "wrong" }));
                }, "duplicate/mismatched scalar " + name + " in " + (subject ? "subject" : "caller"));
            }
        }
        using (var fixture = new Fixture())
        {
            ((ClaimsIdentity)fixture.CallerPrincipal.Identity!).AddClaim(new("aud", "another-caller-audience"));
            ((ClaimsIdentity)fixture.SubjectPrincipal.Identity!).AddClaim(new("aud", "another-subject-audience"));
            Check((await fixture.Authenticate())?.RevalidateCurrent() == true,
                "unique additional token audiences allowed when exact configured channel audience remains present");
        }
        using (var fixture = new Fixture(clientEku: false))
            Check(await fixture.Authenticate() is null && fixture.Certificates.Reads == 0, "explicit clientAuth EKU required before injectable validator");
        using (var fixture = new Fixture())
        {
            var proof = (await fixture.Authenticate())!;
            ((ClaimsIdentity)fixture.SubjectPrincipal.Identity!).AddClaim(new("extra", "after-authentication"));
            Check(!proof.RevalidateCurrent(), "retained subject principal mutation invalidates final proof");
        }
        using (var fixture = new Fixture())
        {
            var proof = (await fixture.Authenticate())!;
            fixture.Http.Request.Headers["Authorization"] = "Bearer changed";
            Check(!proof.RevalidateCurrent(), "exact caller header reread at final gate");
        }
        using (var fixture = new Fixture())
        {
            var proof = (await fixture.Authenticate())!;
            fixture.Http.Features.Set<ITlsHandshakeFeature>(new Peer(fixture.Certificate));
            Check(!proof.RevalidateCurrent(), "native handshake identity cannot change after authentication");
        }
        using (var fixture = new Fixture())
        {
            var proof = (await fixture.Authenticate())!;
            fixture.Peer.ClientCertificate = null;
            Check(!proof.RevalidateCurrent(), "missing live native peer denies final gate");
        }
        using (var fixture = new Fixture())
        {
            using var cancelled = new CancellationTokenSource(); var proof = (await fixture.Authenticate())!;
            fixture.Http.RequestAborted = cancelled.Token; cancelled.Cancel();
            Check(!proof.RevalidateCurrent(), "request cancellation denies final local gate");
        }
        using (var fixture = new Fixture())
        {
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            var threw = false;
            try { _ = await HrReadPdpAuthentication.AuthenticateAsync(fixture.Http, fixture.CallerSettings, fixture.Binding,
                fixture.Request, fixture.Certificates, fixture.Time, cancelled.Token); }
            catch (OperationCanceledException) { threw = true; }
            Check(threw, "caller cancellation propagates");
        }
        async Task EndpointDenied(Action<Fixture> change, int status, string label)
        {
            using var fixture = new Fixture(); change(fixture);
            var sent = await HrReadPdpHttpEndpoint.ExecuteAsync(fixture.Http, fixture.CallerSettings, fixture.Binding,
                certificates: fixture.Certificates, time: fixture.Time);
            Check(!sent && fixture.Http.Response.StatusCode == status && fixture.Http.Response.Body.Length == 0
                && fixture.Http.Response.Headers.CacheControl == "no-store", label);
        }
        await EndpointDenied(_ => { }, 403, "unregistered service remains empty no-store deny after valid transport proof");
        await EndpointDenied(f => f.Http.Features.Get<IHttpRequestFeature>()!.RawTarget += "?x=1", 403, "PDP raw target is exact with no query");
        await EndpointDenied(f => f.Http.Request.Method = "GET", 403, "PDP requires actual POST");
        await EndpointDenied(f => f.Http.Request.Headers.ContentType = new[] { "application/json", "application/json" }, 403, "duplicate content type denied");
        await EndpointDenied(f => f.Http.Request.ContentType = "application/json; charset=utf-8", 403, "only exact application/json contract accepted");
        await EndpointDenied(f => f.Http.Request.Headers.ContentEncoding = "gzip", 403, "compressed request denied");
        await EndpointDenied(f => { f.Http.Request.Body = new MemoryStream(new byte[HrReadPdpWire.MaximumRequestBytes + 1]); f.Http.Request.ContentLength = null; }, 413, "actual bytes bounded independently of Content-Length");
        await EndpointDenied(f => f.Http.Request.ContentLength = HrReadPdpWire.MaximumRequestBytes + 1, 413, "oversized declared request rejected before body");
        await EndpointDenied(f => f.Http.Request.ContentLength = 1, 400, "declared and actual length mismatch denied");
        await EndpointDenied(f => { f.Http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes("{}")); f.Http.Request.ContentLength = null; }, 400, "closed parser rejects incomplete JSON before authentication");
        using (var fixture = new Fixture())
        {
            var clock = new ManualBudgetTime(); fixture.Http.Request.ContentLength = null;
            fixture.Http.Request.Body = new BlockedReadStream(clock.Fire);
            var sent = await HrReadPdpHttpEndpoint.ExecuteAsync(fixture.Http, fixture.CallerSettings, fixture.Binding,
                certificates: fixture.Certificates, time: clock);
            Check(!sent && clock.DueTime == TimeSpan.FromSeconds(5) && fixture.Http.Response.StatusCode == 403
                && fixture.Http.Response.Body.Length == 0 && fixture.Authentication.Schemes.Count == 0,
                "five-second budget starts before an uncooperative body read and denies without authentication");
        }
        using (var fixture = new Fixture())
        {
            var clock = new ManualBudgetTime(); fixture.Authentication.BeforeReturn = clock.Fire;
            fixture.Authentication.PendingResult = new TaskCompletionSource<AuthenticateResult>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
            var sent = await HrReadPdpHttpEndpoint.ExecuteAsync(fixture.Http, fixture.CallerSettings, fixture.Binding,
                certificates: fixture.Certificates, time: clock);
            Check(!sent && fixture.Http.Response.StatusCode == 403 && fixture.Http.Response.Body.Length == 0
                && fixture.Authentication.Schemes.SequenceEqual(new[] { HrReadPdpAuthentication.CallerScheme }),
                "body/auth share one execution budget even when named authentication has no cancellation API");
        }
        foreach (var aborted in new[] { false, true })
        {
            using var fixture = new Fixture(); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            if (aborted) fixture.Http.RequestAborted = cancelled.Token;
            var threw = false;
            try { _ = await HrReadPdpHttpEndpoint.ExecuteAsync(fixture.Http, fixture.CallerSettings, fixture.Binding,
                certificates: fixture.Certificates, time: fixture.Time, cancellationToken: aborted ? default : cancelled.Token); }
            catch (OperationCanceledException) { threw = true; }
            Check(threw && fixture.Authentication.Schemes.Count == 0 && fixture.Http.Response.Body.Length == 0,
                aborted ? "RequestAborted is linked even without explicit CT" : "explicit endpoint cancellation propagates");
        }
        using (var fixture = new Fixture())
        {
            var feature = new StartedResponseFeature { StatusCode = 202 };
            fixture.Http.Features.Set<IHttpResponseFeature>(feature);
            var threw = false;
            try { _ = await HrReadPdpHttpEndpoint.ExecuteAsync(fixture.Http, fixture.CallerSettings, fixture.Binding,
                certificates: fixture.Certificates, time: fixture.Time); }
            catch (InvalidOperationException) { threw = true; }
            Check(threw && feature.StatusCode == 202 && fixture.Authentication.Schemes.Count == 0,
                "already-started response is preserved and never overwritten by denial");
        }
        using (var fixture = await FabricSourceTests.CreateFixtureAsync())
        {
            var clock = new ManualBudgetTime();
            var factory = new ExistingFabricHrReadPdpSourceFactory(fixture.Database, fixture.Module.Projections, fixture.Records, clock);
            var service = new HrReadPdpService(fixture.Module, factory, clock);
            var stream = new BlockedWriteStream(clock.Fire); fixture.Http.Response.Body = stream;
            var threw = false;
            try { _ = await HrReadPdpHttpEndpoint.ExecuteAsync(fixture.Http, fixture.CallerSettings, fixture.Binding,
                service, new CertificateValidator(), clock); }
            catch (OperationCanceledException) { threw = true; }
            Check(threw && stream.Calls == 1 && !fixture.Http.Response.HasStarted && fixture.Http.Response.StatusCode == 200
                && fixture.Http.Response.ContentLength is > 0,
                "pending uncooperative send timeout propagates even before HasStarted and never becomes empty denial");
        }
        count += await SignedJwtHandlersAsync();
        return count;
    }

    private static async Task<int> SignedJwtHandlersAsync()
    {
        var count = 0;
        var now = DateTimeOffset.UtcNow;
        using var rsa = RSA.Create(2048);
        using var wrongRsa = RSA.Create(2048);
        var signingKey = new RsaSecurityKey(rsa) { KeyId = "isolated-test-signing-key" };
        var binding = WireTests.Settings();
        var caller = new HrReadPdpCallerSettings { Enabled = true, Issuer = binding.Issuer, Audience = "fixture-pcc",
            ClientId = "fixture-gateway", SubjectId = "55555555-5555-4555-8555-555555555555",
            RequiredScope = "fixture.pdp.evaluate", PeerCertificateSha256 = binding.PeerCertificateSha256 };
        var services = new ServiceCollection(); services.AddLogging(); services.AddHrReadPdpAuthentication(caller, binding);
        foreach (var scheme in new[] { HrReadPdpAuthentication.CallerScheme, HrReadPdpAuthentication.SubjectScheme })
            services.PostConfigure<JwtBearerOptions>(scheme, options =>
            {
                // Isolated issuer metadata/signing key; production helper remains strict and unchanged.
                var configuration = new OpenIdConnectConfiguration { Issuer = binding.Issuer };
                configuration.SigningKeys.Add(signingKey);
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
            });
        using var provider = services.BuildServiceProvider();
        JsonObject Payload(bool gateway = false) => new()
        {
            ["iss"] = binding.Issuer, ["sub"] = gateway ? caller.SubjectId : binding.SubjectId,
            ["azp"] = gateway ? caller.ClientId : binding.ClientId, ["client_id"] = gateway ? caller.ClientId : binding.ClientId,
            ["aud"] = gateway ? caller.Audience : binding.Audience, ["scope"] = gateway ? caller.RequiredScope : HrReadDesign.ReadScope,
            ["exp"] = now.AddMinutes(2).ToUnixTimeSeconds(), ["nbf"] = now.AddMinutes(-1).ToUnixTimeSeconds(), ["active"] = true
        };
        string Signed(string payload, RSA? key = null)
        {
            var header = Base64UrlEncoder.Encode("{\"alg\":\"RS256\",\"typ\":\"JWT\",\"kid\":\"isolated-test-signing-key\"}");
            var frame = header + "." + Base64UrlEncoder.Encode(payload);
            return frame + "." + Base64UrlEncoder.Encode((key ?? rsa).SignData(Encoding.ASCII.GetBytes(frame), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        }
        async Task CheckSubject(string? header, string? authorization, bool expected, string label, string? rawTarget = null)
        {
            using var scope = provider.CreateScope(); var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            http.Request.Method = "POST"; http.Features.Get<IHttpRequestFeature>()!.RawTarget = rawTarget ?? HrReadPdpWire.EndpointPath;
            if (header is not null) http.Request.Headers[HrReadPdpAuthentication.SubjectHeader] = header;
            if (authorization is not null) http.Request.Headers.Authorization = authorization;
            var result = await http.AuthenticateAsync(HrReadPdpAuthentication.SubjectScheme);
            count++; if (result.Succeeded != expected) throw new InvalidOperationException("PDP signed JWT handler test failed: " + label);
        }
        var validSubject = Signed(Payload().ToJsonString());
        await CheckSubject("Bearer " + validSubject, "Bearer " + Signed(Payload(gateway: true).ToJsonString()), true, "real signature and distinct subject header succeed");
        await CheckSubject(null, "Bearer " + validSubject, false, "subject never falls back to Authorization even valid HR JWT");
        await CheckSubject("Bearer " + validSubject, null, false, "subject handler inactive outside exact endpoint", HrReadPdpWire.EndpointPath + "?x=1");
        await CheckSubject("Bearer " + Signed(Payload().ToJsonString(), wrongRsa), null, false, "wrong signing key rejected");
        foreach (var name in new[] { "iss", "sub", "azp", "aud", "scope", "client_id" })
        {
            var payload = Payload(); payload[name] = "wrong";
            await CheckSubject("Bearer " + Signed(payload.ToJsonString()), null, false, "signed but wrong bound " + name);
        }
        foreach (var name in new[] { "exp", "nbf" })
        {
            var payload = Payload(); payload[name] = name == "exp" ? now.AddMinutes(-1).ToUnixTimeSeconds() : now.AddMinutes(1).ToUnixTimeSeconds();
            await CheckSubject("Bearer " + Signed(payload.ToJsonString()), null, false, "actual lifetime gate " + name);
        }
        var raw = Payload().ToJsonString();
        await CheckSubject("Bearer " + Signed(raw.Replace("\"scope\":\"organization.hr.read\"", "\"scope\":\"organization.hr.read\",\"scope\":\"organization.hr.read\"", StringComparison.Ordinal)), null, false, "cryptographically valid duplicate raw claim rejected");
        await CheckSubject("Bearer " + Signed(raw.Replace("\"scope\":\"organization.hr.read\"", "\"scope\":\"organization.hr.read\",\"sc\\u006fpe\":\"organization.hr.read\"", StringComparison.Ordinal)), null, false, "escaped duplicate raw claim rejected");
        var numeric = Payload(); numeric["exp"] = now.AddMinutes(2).ToUnixTimeSeconds() + 0.5m;
        await CheckSubject("Bearer " + Signed(numeric.ToJsonString()), null, false, "fractional signed NumericDate rejected");
        var inactive = Payload(); inactive["active"] = false;
        await CheckSubject("Bearer " + Signed(inactive.ToJsonString()), null, false, "inactive raw payload rejected");
        var multiAudience = Payload(); multiAudience["aud"] = new JsonArray(binding.Audience, "another-subject-audience");
        await CheckSubject("Bearer " + Signed(multiAudience.ToJsonString()), null, true, "unique audience array includes exact configured HR audience");
        var duplicateAudience = Payload(); duplicateAudience["aud"] = new JsonArray(binding.Audience, binding.Audience);
        await CheckSubject("Bearer " + Signed(duplicateAudience.ToJsonString()), null, false, "duplicate raw audience array rejected");
        using (var scope = provider.CreateScope())
        {
            var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            http.Request.Method = "POST"; http.Features.Get<IHttpRequestFeature>()!.RawTarget = HrReadPdpWire.EndpointPath;
            http.Request.Headers.Authorization = "Bearer " + Signed(Payload(gateway: true).ToJsonString());
            var result = await http.AuthenticateAsync(HrReadPdpAuthentication.CallerScheme);
            count++; if (!result.Succeeded || result.Ticket?.AuthenticationScheme != HrReadPdpAuthentication.CallerScheme)
                throw new InvalidOperationException("PDP caller signature/issuer/audience handler fixture failed.");
        }
        return count;
    }

    internal sealed class Fixture : IDisposable
    {
        public Fixture(bool clientEku = true)
        {
            using var rsa = RSA.Create(2048);
            var request = new CertificateRequest("CN=isolated-pdp-fixture", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            if (clientEku) request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.2") }, false));
            Certificate = request.CreateSelfSigned(Now.AddHours(-1), Now.AddHours(1));
            var pin = Convert.ToHexStringLower(SHA256.HashData(Certificate.RawData));
            Binding = WireTests.Settings() with { PeerCertificateSha256 = pin };
            CallerSettings = new() { Enabled = true, Issuer = Binding.Issuer, Audience = "fixture-pcc", ClientId = "fixture-gateway",
                SubjectId = "55555555-5555-4555-8555-555555555555", RequiredScope = "fixture.pdp.evaluate", PeerCertificateSha256 = pin };
            var json = WireTests.Fixture(Binding); json["tokenSha256"] = Hash(SubjectToken);
            Request = HrReadPdpWire.Parse(Encoding.UTF8.GetBytes(json.ToJsonString()), Binding, Now)!;
            CallerPrincipal = Principal(CallerSettings.Issuer, CallerSettings.Audience, CallerSettings.ClientId, CallerSettings.SubjectId, CallerSettings.RequiredScope);
            SubjectPrincipal = Principal(Binding.Issuer, Binding.Audience, Binding.ClientId, Binding.SubjectId, HrReadDesign.ReadScope);
            Authentication = new() { Caller = Auth(CallerPrincipal, HrReadPdpAuthentication.CallerScheme), Subject = Auth(SubjectPrincipal, HrReadPdpAuthentication.SubjectScheme) };
            Http.RequestServices = new Provider(Authentication); Http.User = new ClaimsPrincipal(new ClaimsIdentity([new("sub", "cookie-user")], "Cookies"));
            Http.Request.Scheme = "https"; Http.Request.Method = "POST"; Http.Request.Path = HrReadPdpWire.EndpointPath;
            Http.Features.Get<IHttpRequestFeature>()!.RawTarget = HrReadPdpWire.EndpointPath;
            Http.Request.Headers.Authorization = "Bearer " + CallerToken; Http.Request.Headers[HrReadPdpAuthentication.SubjectHeader] = "Bearer " + SubjectToken;
            Http.Request.ContentType = "application/json"; var bytes = Encoding.UTF8.GetBytes(json.ToJsonString());
            Http.Request.Body = new MemoryStream(bytes); Http.Request.ContentLength = bytes.Length; Http.Response.Body = new MemoryStream();
            Peer = new(Certificate); Http.Features.Set<ITlsConnectionFeature>(Peer); Http.Features.Set<ITlsHandshakeFeature>(Peer);
        }
        public DefaultHttpContext Http { get; } = new();
        public HrReadPdpCallerSettings CallerSettings { get; set; }
        public HrReadReceiverSettings Binding { get; set; }
        public HrReadPdpRequest Request { get; }
        public ClaimsPrincipal CallerPrincipal { get; }
        public ClaimsPrincipal SubjectPrincipal { get; }
        public Authentication Authentication { get; }
        public CertificateValidator Certificates { get; } = new();
        public FixtureTime Time { get; } = new();
        public Peer Peer { get; }
        public X509Certificate2 Certificate { get; }
        public ValueTask<AuthenticatedHrReadPdpCaller?> Authenticate() => HrReadPdpAuthentication.AuthenticateAsync(Http, CallerSettings, Binding, Request, Certificates, Time);
        public void Dispose() { Certificate.Dispose(); Http.Request.Body.Dispose(); Http.Response.Body.Dispose(); }
    }
    private static string Hash(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static AuthenticateResult Auth(ClaimsPrincipal principal, string scheme) => AuthenticateResult.Success(new AuthenticationTicket(principal, scheme));
    private static ClaimsPrincipal Principal(string issuer, string audience, string client, string subject, string scope) => new(new ClaimsIdentity([
        new("iss", issuer), new("aud", audience), new("sub", subject), new("azp", client), new("scope", scope),
        new("exp", Now.AddSeconds(60).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)),
        new("nbf", Now.AddMinutes(-1).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture)), new("client_id", client), new("active", "true")], "explicit-fixture-bearer"));
    internal sealed class FixtureTime : TimeProvider { public DateTimeOffset Current { get; set; } = Now; public override DateTimeOffset GetUtcNow() => Current; }
    internal sealed class CertificateValidator : IHrReadNativeCertificateValidator
    {
        public bool Allowed { get; set; } = true; public int Reads { get; private set; } public Action? BeforeReturn { get; set; }
        public ValueTask<bool> ValidateAsync(X509Certificate2 certificate, string pin, DateTimeOffset now, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Reads++; BeforeReturn?.Invoke(); return ValueTask.FromResult(Allowed); }
    }
    internal sealed class Authentication : IAuthenticationService
    {
        public required AuthenticateResult Caller { get; set; } public required AuthenticateResult Subject { get; set; }
        public List<string?> Schemes { get; } = []; public Action? BeforeReturn { get; set; } public Task<AuthenticateResult>? PendingResult { get; set; }
        public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme)
        { Schemes.Add(scheme); BeforeReturn?.Invoke(); return PendingResult ?? Task.FromResult(scheme == HrReadPdpAuthentication.CallerScheme ? Caller : scheme == HrReadPdpAuthentication.SubjectScheme ? Subject : AuthenticateResult.NoResult()); }
        public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();
        public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();
        public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) => throw new NotSupportedException();
        public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => throw new NotSupportedException();
    }
    private sealed class Provider(IAuthenticationService authentication) : IServiceProvider
    { public object? GetService(Type type) => type == typeof(IAuthenticationService) ? authentication : null; }
    private sealed class ManualBudgetTime : TimeProvider
    {
        private readonly List<Action> callbacks = [];
        public TimeSpan DueTime { get; private set; }
        public override DateTimeOffset GetUtcNow() => Now;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { callbacks.Add(() => callback(state)); DueTime = dueTime; return new ManualTimer(); }
        public void Fire() { var invoke = callbacks.ToArray(); callbacks.Clear(); foreach (var action in invoke) action(); }
        private sealed class ManualTimer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
    private sealed class BlockedReadStream(Action onRead) : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        { onRead(); return new(new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously).Task); }
    }
    private sealed class BlockedWriteStream(Action onWrite) : MemoryStream
    {
        public int Calls { get; private set; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { Calls++; onWrite(); return new(new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously).Task); }
    }
    private sealed class StartedResponseFeature : IHttpResponseFeature
    {
        public int StatusCode { get; set; }
        public string? ReasonPhrase { get; set; }
        public IHeaderDictionary Headers { get; set; } = new HeaderDictionary();
        public Stream Body { get; set; } = new MemoryStream();
        public bool HasStarted => true;
        public void OnStarting(Func<object, Task> callback, object state) { }
        public void OnCompleted(Func<object, Task> callback, object state) { }
    }
    internal sealed class Peer(X509Certificate2 certificate) : ITlsConnectionFeature, ITlsHandshakeFeature
    {
        public X509Certificate2? ClientCertificate { get; set; } = certificate; public int Reads { get; private set; }
        public SslProtocols Protocol { get; set; } = SslProtocols.Tls13;
        public Task<X509Certificate2?> GetClientCertificateAsync(CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Reads++; return Task.FromResult(ClientCertificate); }
#pragma warning disable SYSLIB0058
        public CipherAlgorithmType CipherAlgorithm => CipherAlgorithmType.Aes256; public int CipherStrength => 256;
        public HashAlgorithmType HashAlgorithm => HashAlgorithmType.Sha256; public int HashStrength => 256;
        public ExchangeAlgorithmType KeyExchangeAlgorithm => ExchangeAlgorithmType.DiffieHellman; public int KeyExchangeStrength => 256;
#pragma warning restore SYSLIB0058
    }
}
