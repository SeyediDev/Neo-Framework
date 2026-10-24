using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fanasa.ContractPolicy;
using Fanasa.HrReadCandidate;
using Fanasa.HrReadPdp;
using Fanasa.HrReadPdp.Tests;

namespace Fanasa.HrReadHost.Tests;

/// <summary>Fake HTTPS transport with real retained native/named authentication fixture. No external HTTP/IdP.</summary>
public static class IntrospectionTests
{
    public static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool value, string label)
        { count++; if (!value) throw new InvalidOperationException("HR3 introspection test failed: " + label); }
        async Task Reject(Action<Fixture> mutate, string label)
        {
            using var f = await Fixture.CreateAsync(); mutate(f);
            var before = f.TransportFixture.DataFingerprint();
            using var preparer = f.Preparer();
            Check(await preparer.PrepareAsync(f.Request, f.Caller, CancellationToken.None) is null, label);
            Check(before == f.TransportFixture.DataFingerprint(), label + " performs no Fabric/outbox write");
        }

        using (var f = await Fixture.CreateAsync())
        {
            var before = f.TransportFixture.DataFingerprint();
            using var preparer = f.Preparer();
            Check(f.Settings.Enabled && f.Settings.Validate().Count == 0, "positive fixture settings are explicitly enabled and structurally valid");
            Check(f.Caller.RevalidateCurrent(), "positive fixture retains current named/native caller proof before preparation");
            var source = await preparer.PrepareAsync(f.Request, f.Caller, CancellationToken.None);
            Check(source is not null,
                "positive preparation yields retained source (HTTP calls=" + f.Transport.Reads + ", authority reads=" + f.Authority.Reads + ", observed forms=" + f.Transport.Observed.Count + ")");
            Check(f.Transport.Reads == 2, "both original tokens are asynchronously introspected");
            Check(f.Authority.Reads == 2, "HTTP is bounded by independent before/after current authority samples");
            Check(f.Transport.Observed.All(x => x.Method == HttpMethod.Post && x.Uri == new Uri(f.Settings.Issuer + "/protocol/openid-connect/token/introspect")
                && x.AuthorizationScheme == "Basic" && x.NoStore && x.ContentType == "application/x-www-form-urlencoded"),
                "exact canonical same-issuer HTTPS endpoint, confidential client auth and no-store form transport are used");
            Check(f.Transport.Observed[0].Token == f.TransportFixture.Http.Request.Headers.Authorization.ToString()[7..]
                && f.Transport.Observed[1].Token == f.TransportFixture.Http.Request.Headers[HrReadPdpAuthentication.SubjectHeader].ToString()[7..],
                "Gateway and subject introspection use their separate exact original raw tokens");
            var proof = source!.ReadCurrent(f.Query, f.Caller, CancellationToken.None);
            Check(proof is not null && proof.ConsumerTenantId == f.Authority.Value!.ConsumerTenantId
                && proof.SubjectTokenSha256 == f.Request.TokenSha256 && proof.GatewayHeaderSha256 == f.Caller.HeaderSha256
                && proof.RequestId == f.Request.RequestId && proof.Selector == f.Query.Selector && proof.Action == f.Request.Action,
                "retained proof binds independently supplied consumer, request tuple, action and both token identities");
            Check(proof!.ObservedAt == FabricSourceTests.Now && proof.ValidUntil == FabricSourceTests.Now.AddSeconds(5)
                && proof.Scopes.SequenceEqual(HrReadPdpService.AuthorizationScopes(f.Caller), StringComparer.Ordinal),
                "proof retains exact derived scopes and exclusive bounded observation starting before both HTTP calls");
            _ = source.ReadCurrent(f.Query, f.Caller, CancellationToken.None);
            Check(f.Transport.Reads == 2 && f.Authority.Reads == 4,
                "synchronous retained reads recheck current central authority and never issue HTTP or block a task");
            Check(before == f.TransportFixture.DataFingerprint(), "prepare/retained reads never create authority, debit, nonce or outbox records");
            Check(source.ReadCurrent(f.Query with { Action = HrReadProfile.HistoryAction }, f.Caller, CancellationToken.None) is null,
                "retained proof cannot widen route/action");
            Check(source.ReadCurrent(f.Query with { Selector = f.Query.Selector! with { ContractId = "other-contract" } }, f.Caller, CancellationToken.None) is null,
                "retained proof cannot change full selector");
            f.Authority.Value = f.Authority.Value! with { Revision = "revoked-current-central-revision" };
            Check(source.ReadCurrent(f.Query, f.Caller, CancellationToken.None) is null,
                "changed current mapping/delegation revision invalidates retained authentication immediately");
        }

        await Reject(f => f.Settings = f.Settings with { Enabled = false }, "default disabled witness mode denies");
        await Reject(f => f.Settings = f.Settings with { MaximumWitnessSeconds = 6 }, "witness cannot exceed five seconds");
        await Reject(f => f.Settings = f.Settings with { MaximumWitnessSeconds = 0 }, "empty witness interval denies");
        foreach (var issuer in new[] { "http://sso.example.test/realms/fanasa", "https://sso.example.test/realms/fanasa/",
            "https://sso.example.test/realms/fanasa?x=1", "https://user:password@sso.example.test/realms/fanasa",
            "https://sso.example.test/realms/other", "https://sso.example.test/realms/fanasa#x" })
            await Reject(f => f.Settings = f.Settings with { Issuer = issuer }, "noncanonical or different issuer denies");
        await Reject(f => f.Credentials.Value = null, "missing confidential introspection credentials denies");
        await Reject(f => f.Credentials.Value = new("bad:client", "secret"), "ambiguous Basic client identity denies");
        await Reject(f => f.Credentials.Value = new("valid-client", "bad\nsecret"), "control characters in credential deny");
        await Reject(f => f.Authority.Value = null, "missing independent consumer/delegation authority denies");
        await Reject(f => f.Authority.Value = f.Authority.Value! with { ConsumerTenantId = Guid.NewGuid() }, "consumer cannot be selected from request in place of authoritative mapping");
        await Reject(f => f.Authority.Value = f.Authority.Value! with { GatewayClientId = "other-gateway" }, "Gateway client delegation mismatch denies");
        await Reject(f => f.Authority.Value = f.Authority.Value! with { SubjectClientId = "other-subject-client" }, "subject client authority mismatch denies");
        await Reject(f => f.Authority.Value = f.Authority.Value! with { ValidUntil = FabricSourceTests.Now }, "exclusive authority deadline denies");
        await Reject(f => f.Authority.Value = f.Authority.Value! with { Revision = "" }, "missing central authority revision denies");
        await Reject(f => f.Authority.BeforeRead = (read, _) => { if (read == 2) f.Authority.Value = f.Authority.Value! with { Revision = "changed" }; },
            "authority revision changing while HTTP is in flight denies");
        await Reject(f => f.Transport.BeforeSend = (read, _) => { if (read == 2) f.TransportFixture.Http.Request.Headers.Authorization = "Bearer changed.gateway.token"; },
            "original native signed caller proof is rechecked after HTTP");

        foreach (var (name, replacement) in new (string, JsonNode?)[]
        {
            ("active", JsonValue.Create(false)), ("active", JsonValue.Create("true")), ("iss", JsonValue.Create("https://wrong.test/realms/fanasa")),
            ("sub", JsonValue.Create("77777777-7777-4777-8777-777777777777")), ("azp", JsonValue.Create("other-client")),
            ("client_id", JsonValue.Create("other-client")), ("scope", JsonValue.Create("unrelated.scope")),
            ("exp", JsonValue.Create("1792065720")), ("exp", JsonValue.Create(-1)),
            ("exp", JsonValue.Create(1.25)), ("exp", JsonValue.Create(FabricSourceTests.Now.ToUnixTimeSeconds())),
            ("nbf", JsonValue.Create(FabricSourceTests.Now.AddSeconds(1).ToUnixTimeSeconds())),
            ("aud", JsonValue.Create("other-audience")), ("aud", new JsonArray("fixture-pcc", "fixture-pcc"))
        })
        {
            await Reject(f => f.ChangeBody(0, body => body[name] = replacement?.DeepClone()), "Gateway introspection claims must match exact retained signed " + name);
        }
        foreach (var name in new[] { "active", "iss", "sub", "azp", "client_id", "scope", "exp", "aud" })
            await Reject(f => f.ChangeBody(1, body => body.Remove(name)), "subject introspection missing required " + name + " denies");
        await Reject(f => f.ChangeBody(1, body => body["client_id"] = "fixture-gateway"), "subject token cannot become the Gateway token");
        await Reject(f => f.ChangeBody(1, body => body["scope"] = HrReadDesign.ReadScope), "subject full raw scope must match signed retained claim");
        await Reject(f => f.ChangeBody(1, body => body.Remove("consumerTenantId")), "missing independently introspected consumer denies");
        await Reject(f => f.ChangeBody(1, body => body["consumerTenantId"] = new JsonArray(f.Authority.Value!.ConsumerTenantId.ToString("D"))), "consumer array cannot select a tenant");
        await Reject(f => f.ChangeBody(1, body => body["consumerTenantId"] = Guid.NewGuid().ToString("D")), "introspected consumer must match independently read native mapping");
        await Reject(f => f.ChangeBody(1, body => body["consumerTenantId"] = ""), "empty introspected consumer denies");
        await Reject(f => f.ChangeBody(1, body => body["consumerTenantId"] = Guid.Empty.ToString("D")), "zero introspected consumer denies");
        await Reject(f => f.ChangeBody(1, body => body["consumerTenantId"] = "AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA"), "noncanonical uppercase introspected consumer denies");
        await Reject(f => f.ChangeBody(1, body => { body["ConsumerTenantId"] = body["consumerTenantId"]!.DeepClone(); body.Remove("consumerTenantId"); }), "wrong-case consumer property cannot be a fallback");
        await Reject(f => f.Transport.Bodies[1] = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(f.Transport.Bodies[1])[..^1] + ",\"consumerTenantId\":\"" + f.Authority.Value!.ConsumerTenantId.ToString("D") + "\"}"), "duplicate introspected consumer denies");
        foreach (var issued in new JsonNode[] { JsonValue.Create("123")!, JsonValue.Create(1.5)!, JsonValue.Create(-1)!,
            JsonValue.Create(FabricSourceTests.Now.AddSeconds(1).ToUnixTimeSeconds())! })
            await Reject(f => f.ChangeBody(1, body => body["iat"] = issued.DeepClone()), "introspection iat must be canonical and no later than now");
        await Reject(f => f.ChangeBody(0, body => body["aud"] = new JsonArray("fixture-pcc", "extra-audience")), "extra introspection audience absent from signed token denies");
        await Reject(f => f.Transport.Bodies[0] = Encoding.UTF8.GetBytes("{\"active\":true,\"\\u0061ctive\":false}"), "escaped duplicate root response property denies");
        await Reject(f => f.Transport.Bodies[0] = Encoding.UTF8.GetBytes("{\"outer\":{\"x\":1,\"x\":2}}"), "nested duplicate response property denies");
        await Reject(f => f.Transport.Bodies[0] = [0x7B, 0x22, 0x78, 0x22, 0x3A, 0x22, 0xC3, 0x28, 0x22, 0x7D], "invalid UTF-8 response denies");
        await Reject(f => f.Transport.Bodies[0] = Encoding.UTF8.GetBytes("[]"), "nonobject response denies");
        await Reject(f => f.Transport.Bodies[0] = Encoding.UTF8.GetBytes("{} trailing"), "trailing JSON bytes deny");
        await Reject(f => f.Transport.Bodies[0] = Encoding.UTF8.GetBytes(""), "empty response denies");
        await Reject(f => f.Transport.Bodies[0] = new byte[Hr3KeycloakIntrospectionPreparer.MaximumResponseBytes + 1], "declared oversized response denies");
        await Reject(f => { f.Transport.Bodies[0] = new byte[Hr3KeycloakIntrospectionPreparer.MaximumResponseBytes + 1]; f.Transport.UnknownLength = true; },
            "actual streamed response byte ceiling is enforced without Content-Length");
        await Reject(f => f.Transport.Status = HttpStatusCode.Redirect, "HTTP redirect is never accepted as active introspection");
        await Reject(f => f.Transport.Status = HttpStatusCode.Unauthorized, "confidential-client auth failure denies");
        await Reject(f => f.Transport.ContentType = "text/html", "HTML/error content cannot be treated as introspection JSON");
        await Reject(f => f.Transport.Charset = "utf-16", "non-UTF8 content type denies");
        await Reject(f => f.Transport.Encoding = "gzip", "compressed response is rejected before parsing");
        await Reject(f => f.Transport.ResponseUri = new Uri("https://elsewhere.example.test/introspection"), "response associated with a redirected endpoint denies");
        await Reject(f => f.Transport.BeforeSend = (_, _) => throw new HttpRequestException("synthetic unreachable issuer"), "HTTP transport unavailable denies");

        using (var f = await Fixture.CreateAsync())
        {
            using var defaultCredentials = new Hr3KeycloakIntrospectionPreparer(f.Settings, authority: f.Authority, transport: f.Transport, time: f.TransportFixture.Time);
            Check(await defaultCredentials.PrepareAsync(f.Request, f.Caller, CancellationToken.None) is null && f.Transport.Reads == 0,
                "default unavailable credentials never trigger an anonymous introspection request");
            using var defaultAuthority = new Hr3KeycloakIntrospectionPreparer(f.Settings, f.Credentials, transport: f.Transport, time: f.TransportFixture.Time);
            Check(await defaultAuthority.PrepareAsync(f.Request, f.Caller, CancellationToken.None) is null && f.Transport.Reads == 0,
                "default unavailable mapping/delegation never trusts request consumer or calls HTTP");
            Check(!f.Credentials.Value!.ToString()!.Contains("isolated-test-secret", StringComparison.Ordinal)
                && !JsonSerializer.Serialize(f.Credentials.Value).Contains("isolated-test-secret", StringComparison.Ordinal),
                "credential ToString and default JSON have no secret surface");
        }
        using (var f = await Fixture.CreateAsync())
        {
            f.Transport.BeforeSend = (read, _) => { if (read == 2) f.TransportFixture.SetTime(FabricSourceTests.Now.AddSeconds(4)); };
            using var preparer = f.Preparer(); var source = await preparer.PrepareAsync(f.Request, f.Caller, CancellationToken.None);
            Check(source?.ReadCurrent(f.Query, f.Caller, CancellationToken.None)?.ValidUntil == FabricSourceTests.Now.AddSeconds(5),
                "slow HTTP does not restart the original observation/request deadline");
            f.TransportFixture.SetTime(FabricSourceTests.Now.AddSeconds(5));
            Check(source!.ReadCurrent(f.Query, f.Caller, CancellationToken.None) is null && f.Transport.Reads == 2,
                "exclusive five-second expiry denies without background refresh or stale proof fallback");
        }
        using (var f = await Fixture.CreateAsync())
        {
            using var preparer = f.Preparer(); var source = await preparer.PrepareAsync(f.Request, f.Caller, CancellationToken.None);
            f.Authority.Value = f.Authority.Value! with { ValidUntil = FabricSourceTests.Now.AddSeconds(2) };
            Check(source?.ReadCurrent(f.Query, f.Caller, CancellationToken.None)?.ValidUntil == FabricSourceTests.Now.AddSeconds(2),
                "current local authority may shorten retained proof lease");
            f.Authority.Value = f.Authority.Value! with { ValidUntil = FabricSourceTests.Now.AddHours(1) };
            Check(source?.ReadCurrent(f.Query, f.Caller, CancellationToken.None)?.ValidUntil == FabricSourceTests.Now.AddSeconds(5),
                "fresh longer local lease never extends initial IdP observation ceiling");
            f.Authority.Value = null;
            Check(source!.ReadCurrent(f.Query, f.Caller, CancellationToken.None) is null, "current independent authority loss denies inside retained snapshot reads");
        }
        using (var f = await Fixture.CreateAsync())
        {
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); using var preparer = f.Preparer(); var cancelled = false;
            try { await preparer.PrepareAsync(f.Request, f.Caller, cancellation.Token); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && f.Transport.Reads == 0, "preparation propagates caller cancellation before HTTP");
        }
        using (var f = await Fixture.CreateAsync())
        {
            using var cancellation = new CancellationTokenSource();
            f.Transport.BeforeSend = (_, _) => cancellation.Cancel(); using var preparer = f.Preparer(); var cancelled = false;
            try { await preparer.PrepareAsync(f.Request, f.Caller, cancellation.Token); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled && f.Transport.Reads == 1, "in-flight HTTP caller cancellation propagates and never introspects the second token");
        }
        using (var f = await Fixture.CreateWithSignedIssuedAsync())
        {
            using var preparer = f.Preparer();
            Check(await preparer.PrepareAsync(f.Request, f.Caller, CancellationToken.None) is not null,
                "canonical introspection iat exactly matches authenticated signed iat on both tokens");
        }
        foreach (var mode in new[] { "missing", "different", "string" })
        {
            using var f = await Fixture.CreateWithSignedIssuedAsync();
            f.ChangeBody(1, body =>
            {
                if (mode == "missing") body.Remove("iat");
                else if (mode == "different") body["iat"] = FabricSourceTests.Now.AddMinutes(-2).ToUnixTimeSeconds();
                else body["iat"] = FabricSourceTests.Now.AddMinutes(-1).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
            });
            using var preparer = f.Preparer();
            Check(await preparer.PrepareAsync(f.Request, f.Caller, CancellationToken.None) is null,
                "signed iat requires canonical present matching introspection iat: " + mode);
        }
        return count;
    }

    private sealed class Credentials : IHr3IntrospectionCredentials
    {
        public Hr3IntrospectionCredential? Value { get; set; } = new("isolated-introspector", "isolated-test-secret");
        public ValueTask<Hr3IntrospectionCredential?> ReadAsync(CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(Value); }
    }
    private sealed class Authority : IHr3IntrospectionAuthority
    {
        public Hr3IntrospectionAuthoritySnapshot? Value { get; set; }
        public int Reads { get; private set; }
        public Action<int, HrReadPdpRequest>? BeforeRead { get; set; }
        public Hr3IntrospectionAuthoritySnapshot? ReadCurrent(HrReadPdpRequest request, AuthenticatedHrReadPdpCaller caller, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Reads++; BeforeRead?.Invoke(Reads, request); return Value; }
    }
    private sealed record Observation(HttpMethod Method, Uri Uri, string? AuthorizationScheme, bool NoStore, string? ContentType, string Token);
    private sealed class Transport : IHr3IntrospectionTransport
    {
        public List<byte[]> Bodies { get; } = [];
        public List<Observation> Observed { get; } = [];
        public int Reads { get; private set; }
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public string ContentType { get; set; } = "application/json";
        public string? Charset { get; set; }
        public string? Encoding { get; set; }
        public bool UnknownLength { get; set; }
        public Uri? ResponseUri { get; set; }
        public Action<int, HttpRequestMessage>? BeforeSend { get; set; }
        public async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var read = ++Reads; BeforeSend?.Invoke(read, request); cancellationToken.ThrowIfCancellationRequested();
            var form = await request.Content!.ReadAsStringAsync(cancellationToken);
            var token = WebUtility.UrlDecode(form.Split('&').Single(x => x.StartsWith("token=", StringComparison.Ordinal))[6..]);
            Observed.Add(new(request.Method, request.RequestUri!, request.Headers.Authorization?.Scheme,
                request.Headers.CacheControl?.NoStore == true, request.Content.Headers.ContentType?.MediaType, token));
            HttpContent content = UnknownLength ? new StreamContent(new NonSeekable(Bodies[read - 1])) : new ByteArrayContent(Bodies[read - 1]);
            content.Headers.ContentType = new MediaTypeHeaderValue(ContentType) { CharSet = Charset };
            if (Encoding is not null) content.Headers.ContentEncoding.Add(Encoding);
            return new HttpResponseMessage(Status) { Content = content,
                RequestMessage = ResponseUri is null ? null : new HttpRequestMessage(HttpMethod.Post, ResponseUri) };
        }
    }
    private sealed class NonSeekable(byte[] bytes) : Stream
    {
        private readonly MemoryStream inner = new(bytes);
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
    private sealed class Fixture : IDisposable
    {
        private Fixture(FabricSourceTests.Fixture fixture)
        {
            TransportFixture = fixture;
            Settings = new() { Enabled = true, Issuer = fixture.Binding.Issuer, GatewayAudience = fixture.CallerSettings.Audience,
                GatewayRequiredScope = fixture.CallerSettings.RequiredScope };
            Authority.Value = new(Guid.Parse(fixture.Binding.ConsumerTenantId), fixture.CallerSettings.SubjectId, fixture.CallerSettings.ClientId,
                fixture.Request.SubjectId, fixture.Request.ClientId, "existing-mapping-and-delegation-revision-1", FabricSourceTests.Now.AddSeconds(5));
            Transport.Bodies.Add(Payload(Caller.CallerPrincipal)); Transport.Bodies.Add(Payload(Caller.SubjectPrincipal, Authority.Value.ConsumerTenantId));
        }
        public static async Task<Fixture> CreateAsync() => new(await FabricSourceTests.CreateFixtureAsync(seed: false));
        public static async Task<Fixture> CreateWithSignedIssuedAsync()
        {
            var authentication = new HttpTests.Fixture();
            try
            {
                var issued = FabricSourceTests.Now.AddMinutes(-1).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture);
                ((ClaimsIdentity)authentication.CallerPrincipal.Identity!).AddClaim(new("iat", issued));
                ((ClaimsIdentity)authentication.SubjectPrincipal.Identity!).AddClaim(new("iat", issued));
                var proof = await authentication.Authenticate() ?? throw new InvalidOperationException("Synthetic signed-iat fixture authentication failed.");
                return new(new FabricSourceTests.Fixture(authentication, proof));
            }
            catch { authentication.Dispose(); throw; }
        }
        public FabricSourceTests.Fixture TransportFixture { get; }
        public HrReadPdpRequest Request => TransportFixture.Request;
        public AuthenticatedHrReadPdpCaller Caller => TransportFixture.Caller;
        public TrustedAccessSnapshotQuery Query => TransportFixture.Query;
        public Hr3IntrospectionSettings Settings { get; set; }
        public Credentials Credentials { get; } = new(); public Authority Authority { get; } = new(); public Transport Transport { get; } = new();
        public Hr3KeycloakIntrospectionPreparer Preparer() => new(Settings, Credentials, Authority, Transport, TransportFixture.Time);
        public void ChangeBody(int index, Action<JsonObject> mutate)
        { var body = JsonNode.Parse(Transport.Bodies[index])!.AsObject(); mutate(body); Transport.Bodies[index] = JsonSerializer.SerializeToUtf8Bytes(body); }
        private static byte[] Payload(ClaimsPrincipal principal, Guid? consumer = null)
        {
            var identity = principal.Identities.Single(x => x.IsAuthenticated);
            var body = new JsonObject { ["active"] = true, ["client_id"] = identity.FindFirst("azp")!.Value };
            foreach (var name in new[] { "iss", "sub", "azp", "scope" }) body[name] = identity.FindFirst(name)!.Value;
            body["exp"] = long.Parse(identity.FindFirst("exp")!.Value, System.Globalization.CultureInfo.InvariantCulture);
            if (identity.FindFirst("nbf") is { } nbf) body["nbf"] = long.Parse(nbf.Value, System.Globalization.CultureInfo.InvariantCulture);
            if (identity.FindFirst("iat") is { } iat) body["iat"] = long.Parse(iat.Value, System.Globalization.CultureInfo.InvariantCulture);
            if (consumer is { } tenant) body["consumerTenantId"] = tenant.ToString("D");
            body["aud"] = JsonSerializer.SerializeToNode(identity.FindAll("aud").Select(x => x.Value).ToArray());
            return JsonSerializer.SerializeToUtf8Bytes(body);
        }
        public void Dispose() => TransportFixture.Dispose();
    }
}
