using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fanasa.ContractPolicy;
using Fanasa.AccessManagement.Web;
using Fanasa.AccessManagement.Web.Api;
using Fanasa.AccessManagement.Web.Accounting;
using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.ContractPolicies;
using Fanasa.AccessManagement.Web.Domain.Access;
using Fanasa.AccessManagement.Web.Governance;
using Fanasa.AccessManagement.Web.Metering;
using Fanasa.AccessManagement.Web.Organization;
using Fanasa.AccessManagement.Web.Persistence;
using Fanasa.AccessManagement.Web.Platform;
using Fanasa.AccessManagement.Web.Security;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Https;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

// Local integration only: synthetic keys, contracts, tariffs and registration;
// real production handlers, Kestrel TLS/client certificates and temporary SQLite.
var passed = 0;
var failed = 0;
async Task Test(string name, Func<Task> body)
{
    try { await body(); passed++; Console.WriteLine("PASS Gateway " + name); }
    catch (Exception error) { failed++; Console.Error.WriteLine("FAIL Gateway " + name + ": " + error); }
}

await using (var host = await GatewayFixture.Start())
{
    await Test("registered Gateway JWT and authoritative PIP return pure Permit", async () =>
    {
        var before = host.BusinessSnapshot();
        using var response = await host.Decide();
        Equal(HttpStatusCode.OK, response.StatusCode);
        var decision = await response.Content.ReadFromJsonAsync<PolicyDecision>(ContractJson.Options);
        Equal("Permit", decision!.Decision); Equal(2, decision.Obligations.Count);
        Check(response.Headers.CacheControl?.NoStore == true, "decision cache header");
        Equal(before, host.BusinessSnapshot());
    });
    foreach (var mode in new[] { "signature", "issuer", "audience", "expired", "client", "scope", "human", "producer", "duplicate-iss", "escaped-iss", "quoted-exp", "fractional-exp", "array-scope", "duplicate-aud", "wrong-active" })
        await Test("Gateway JWT rejects " + mode, async () =>
        {
            var before = host.BusinessSnapshot(); using var response = await host.Decide(token: host.GatewayToken(mode));
            Check(!response.IsSuccessStatusCode, "invalid Gateway identity accepted: " + mode); Equal(before, host.BusinessSnapshot());
        });
    foreach (var mode in new[] { "unknown", "duplicate", "environment", "owner", "allocation", "scope", "consumer", "requested-future", "requested-stale", "expiry", "quoted-expiry", "missing" })
        await Test("trusted Gateway request rejects " + mode, async () =>
        {
            var before = host.BusinessSnapshot(); using var response = await host.Decide(body: host.PolicyRequest(mode));
            Check(response.StatusCode != HttpStatusCode.InternalServerError, "parser threw");
            if (response.IsSuccessStatusCode) Equal("Indeterminate", (await response.Content.ReadFromJsonAsync<PolicyDecision>(ContractJson.Options))!.Decision);
            Equal(before, host.BusinessSnapshot());
        });
    await Test("revoked Gateway registered scope denies current signed token", async () =>
    {
        host.PutGatewayClient(host.GatewayClient with { AllowedScopes = [] });
        try { using var response = await host.Decide(); Equal(HttpStatusCode.Forbidden, response.StatusCode); }
        finally { host.PutGatewayClient(host.GatewayClient); }
    });
    await Test("inactive Gateway registered client denies current signed token", async () =>
    {
        host.PutGatewayClient(host.GatewayClient with { IsActive = false });
        try { using var response = await host.Decide(); Equal(HttpStatusCode.Forbidden, response.StatusCode); }
        finally { host.PutGatewayClient(host.GatewayClient); }
    });
    await Test("Gateway scope revoked during an awaited body read denies without writes", async () =>
    {
        var before = host.BusinessSnapshot(); using var content = new DeferredGatewayContent(host.PolicyRequest());
        var observed = host.ObserveNextGatewayBodyRead(); var pending = host.DecideContent(content, host.GatewayToken());
        try
        {
            await observed.WaitAsync(TimeSpan.FromSeconds(10));
            host.PutGatewayClient(host.GatewayClient with { AllowedScopes = [] }); content.Release();
            using var response = await pending; Equal(HttpStatusCode.Forbidden, response.StatusCode); Equal(before, host.BusinessSnapshot());
        }
        finally { content.Release(); host.PutGatewayClient(host.GatewayClient); }
    });

    foreach (var mode in new[] { "missing", "forged-signature", "wrong-body", "wrong-auth", "wrong-path", "wrong-idempotency", "expired", "future", "too-long", "wrong-tenant", "wrong-subject", "wrong-client", "wrong-policy", "wrong-contract", "wrong-revision", "wrong-period", "unknown-context", "duplicate-context", "unknown-envelope", "duplicate-envelope", "not-confirmed", "not-allowed", "foreign-resource", "noncanonical-number", "unknown-key", "wrong-tier", "duplicate-header", "underscore-header", "oversize-body", "unknown-command", "duplicate-command" })
        await Test("signed context rejects " + mode + " without business writes", async () =>
        {
            var before = host.BusinessSnapshot(); using var response = await host.Operate(host.Command(), mode);
            Check(!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.InternalServerError, "invalid witness accepted: " + mode + " HTTP " + response.StatusCode);
            Equal(before, host.BusinessSnapshot());
        });
    await Test("correct HMAC without client certificate fails direct backend boundary", async () =>
    {
        var before = host.BusinessSnapshot(); using var response = await host.Operate(host.Command(), clientMode: "none");
        Equal(HttpStatusCode.Forbidden, response.StatusCode); Equal(before, host.BusinessSnapshot());
    });
    await Test("correct HMAC with wrong client certificate fails pinned boundary", async () =>
    {
        var before = host.BusinessSnapshot(); using var response = await host.Operate(host.Command(), clientMode: "wrong");
        Equal(HttpStatusCode.Forbidden, response.StatusCode); Equal(before, host.BusinessSnapshot());
    });
    await Test("grant revocation invalidates correctly signed context", async () =>
    {
        host.Access.SetGrant(new(host.Tenant, host.Actor, "organization.write", DateTimeOffset.UtcNow.AddMinutes(-1)), "synthetic", "revoke");
        var before = host.BusinessSnapshot();
        try { using var response = await host.Operate(host.Command()); Equal(HttpStatusCode.Forbidden, response.StatusCode); Equal(before, host.BusinessSnapshot()); }
        finally { host.RestoreGrant(); }
    });
    await Test("role revocation invalidates correctly signed context", async () =>
    {
        host.AssignRole(false); var before = host.BusinessSnapshot();
        try { using var response = await host.Operate(host.Command()); Equal(HttpStatusCode.Forbidden, response.StatusCode); Equal(before, host.BusinessSnapshot()); }
        finally { host.AssignRole(true); }
    });
    await Test("real mTLS plus request-bound HMAC executes committed unit save once", async () =>
    {
        var command = host.Command(); var request = host.Witness(command);
        using var first = await host.SendWitness(request); Equal(HttpStatusCode.OK, first.StatusCode);
        var originalReceipt = await first.Content.ReadAsStringAsync();
        Equal(1L, host.Organization.Read(host.Tenant).Revision);
        Equal(12m, host.Billing.Read(host.Tenant).Outstanding);
        Equal(1, host.Database.All<JsonElement>("measured.operation").Length);
        Check(host.Database.Outbox(take: 100).Any(item => item.Kind.Contains("usage", StringComparison.OrdinalIgnoreCase)), "committed usage outbox");
        var before = host.BusinessSnapshot();
        using var replay = await host.SendWitness(request); Equal(HttpStatusCode.Forbidden, replay.StatusCode); Equal(before, host.BusinessSnapshot());
        using var businessRetry = await host.Operate(command); Equal(HttpStatusCode.OK, businessRetry.StatusCode);
        Equal(originalReceipt, await businessRetry.Content.ReadAsStringAsync());
        Equal(before, host.BusinessSnapshot());
    });
    await Test("unknown parent ID is denied without business writes", async () =>
    {
        var before = host.BusinessSnapshot();
        using var response = await host.Operate(host.Command() with { ParentId = Guid.NewGuid() });
        Equal(HttpStatusCode.Forbidden, response.StatusCode); Equal(before, host.BusinessSnapshot());
    });
    await Test("one Gateway nonce is claimed once under concurrent HTTP requests", async () =>
    {
        var command = host.Command(); var witness = host.Witness(command);
        var responses = await Task.WhenAll(host.SendWitness(witness), host.SendWitness(witness));
        try
        {
            Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.OK));
            Equal(1, responses.Count(response => response.StatusCode == HttpStatusCode.Forbidden));
            Equal(2L, host.Organization.Read(host.Tenant).Revision); Equal(24m, host.Billing.Read(host.Tenant).Outstanding);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    });
    await Test("replay claims persist through independent Fabric connection reopen", () =>
    {
        using var reopened = new FabricDatabase(host.DatabasePath);
        Equal(host.Database.All<JsonElement>("contract-policy.gateway-context.replay.v1").Length,
            reopened.All<JsonElement>("contract-policy.gateway-context.replay.v1").Length);
        Check(reopened.All<JsonElement>("contract-policy.gateway-context.replay.v1").Length >= 2, "no persisted replay tombstones");
        return Task.CompletedTask;
    });
}
await using (var retiredPolicy = await GatewayFixture.Start())
    await Test("retired published policy vetoes pure decision and signed operation", async () =>
    {
        var previous = retiredPolicy.Store.ReadPolicy(retiredPolicy.Tenant)!;
        retiredPolicy.Store.RetirePolicy(retiredPolicy.Tenant, previous.Revision, "synthetic-test", "retire independent published policy");
        var before = retiredPolicy.BusinessSnapshot();
        using var decision = await retiredPolicy.Decide(); Equal(HttpStatusCode.OK, decision.StatusCode);
        Check((await decision.Content.ReadFromJsonAsync<PolicyDecision>(ContractJson.Options))!.Decision != "Permit", "retired policy still permits");
        using var response = await retiredPolicy.Operate(retiredPolicy.Command()); Equal(HttpStatusCode.Forbidden, response.StatusCode); Equal(before, retiredPolicy.BusinessSnapshot());
    });
await using (var mismatched = await GatewayFixture.Start("mismatched-rate-contract"))
    await Test("Gateway Permit cannot charge a different configured contract version", async () =>
    {
        using var decision = await mismatched.Decide(); Equal(HttpStatusCode.OK, decision.StatusCode);
        Equal("Permit", (await decision.Content.ReadFromJsonAsync<PolicyDecision>(ContractJson.Options))!.Decision);
        var before = mismatched.BusinessSnapshot(); using var operation = await mismatched.Operate(mismatched.Command());
        Equal(HttpStatusCode.Forbidden, operation.StatusCode); Equal(before, mismatched.BusinessSnapshot());
    });
await using (var disabled = await GatewayFixture.Start("request-disabled"))
    await Test("omitted signed Gateway mode keeps actual business route closed", async () =>
    {
        var before = disabled.BusinessSnapshot(); using var operation = await disabled.Operate(disabled.Command());
        Equal(HttpStatusCode.Forbidden, operation.StatusCode); Equal(before, disabled.BusinessSnapshot());
    });
await using (var uppercase = await GatewayFixture.Start("uppercase-subject"))
    await Test("uppercase GUID subject preserves signed identity and stored membership", async () =>
    {
        using var decision = await uppercase.Decide(); Equal(HttpStatusCode.OK, decision.StatusCode);
        Equal("Permit", (await decision.Content.ReadFromJsonAsync<PolicyDecision>(ContractJson.Options))!.Decision);
        using var operation = await uppercase.Operate(uppercase.Command()); Equal(HttpStatusCode.OK, operation.StatusCode);
        Equal(1L, uppercase.Organization.Read(uppercase.Tenant).Revision); Equal(12m, uppercase.Billing.Read(uppercase.Tenant).Outstanding);
    });
await using (var late = await GatewayFixture.Start("late-admission-expiry"))
    await Test("Gateway witness expiring after admission vetoes actual business commit", async () =>
    {
        var before = late.BusinessSnapshot(); using var operation = await late.Operate(late.Command());
        Equal(HttpStatusCode.Forbidden, operation.StatusCode); Check(late.ClockWasChanged, "real admission did not reach expiry hook"); Equal(before, late.BusinessSnapshot());
    });
await using (var lateWrite = await GatewayFixture.Start("late-write-expiry"))
    await Test("Gateway witness expiry during usage admission rolls back shared business ledger outbox", async () =>
    {
        var before = lateWrite.BusinessSnapshot(); using var operation = await lateWrite.Operate(lateWrite.Command());
        Equal(HttpStatusCode.Forbidden, operation.StatusCode); Check(lateWrite.ClockWasChanged, "real usage validation did not reach expiry hook"); Equal(before, lateWrite.BusinessSnapshot());
    });
await using (var subsecond = await GatewayFixture.Start("late-subsecond-expiry"))
    await Test("one tick beyond signed fractional deadline vetoes business commit", async () =>
    {
        var before = subsecond.BusinessSnapshot(); using var operation = await subsecond.Operate(subsecond.Command());
        Equal(HttpStatusCode.Forbidden, operation.StatusCode); Check(subsecond.ClockWasChanged, "real admission did not reach fractional expiry hook"); Equal(before, subsecond.BusinessSnapshot());
    });
await using (var retiredRate = await GatewayFixture.Start("tariff-expiry"))
    await Test("fresh authorized receipt retry preserves historical tariff after rate window closes", async () =>
    {
        var command = retiredRate.Command(); using var first = await retiredRate.Operate(command); Equal(HttpStatusCode.OK, first.StatusCode);
        var originalReceipt = await first.Content.ReadAsStringAsync();
        var before = retiredRate.BusinessSnapshot(); retiredRate.AdvanceClock(TimeSpan.FromMinutes(3));
        using var retry = await retiredRate.Operate(command); Equal(HttpStatusCode.OK, retry.StatusCode); Equal(originalReceipt, await retry.Content.ReadAsStringAsync()); Equal(before, retiredRate.BusinessSnapshot());
        using var newBusiness = await retiredRate.Operate(retiredRate.Command()); Equal(HttpStatusCode.Forbidden, newBusiness.StatusCode); Equal(before, retiredRate.BusinessSnapshot());
    });
Console.WriteLine($"Gateway integration: {passed} passed, {failed} failed.");
Environment.ExitCode = failed == 0 ? 0 : 1;

static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}; actual {actual}"); }

sealed record SignedRequest(string Path, string Body, string Token, string? Context, string? Signature, string Idempotency, bool DuplicateHeader = false, bool UnderscoreHeader = false);
sealed class DeferredGatewayContent(string json) : HttpContent
{
    private readonly TaskCompletionSource released = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public void Release() => released.TrySetResult();
    protected override bool TryComputeLength(out long length) { length = 0; return false; }
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        var bytes = Encoding.UTF8.GetBytes(json); await stream.WriteAsync(bytes.AsMemory(0, 1)); await stream.FlushAsync();
        await released.Task; await stream.WriteAsync(bytes.AsMemory(1));
    }
}
sealed class ReadObservedStream(Stream inner, TaskCompletionSource observed) : Stream
{
    public override bool CanRead => inner.CanRead; public override bool CanSeek => inner.CanSeek; public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length; public override long Position { get => inner.Position; set => inner.Position = value; }
    public override void Flush() => inner.Flush(); public override int Read(byte[] buffer, int offset, int count) { observed.TrySetResult(); return inner.Read(buffer, offset, count); }
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) { observed.TrySetResult(); return inner.ReadAsync(buffer, offset, count, cancellationToken); }
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { observed.TrySetResult(); return inner.ReadAsync(buffer, cancellationToken); }
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin); public override void SetLength(long value) => inner.SetLength(value);
    public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
}
sealed class MutableGatewayClock : TimeProvider
{
    private TimeSpan offset;
    private DateTimeOffset? frozen;
    public bool WasChanged { get; private set; }
    public override DateTimeOffset GetUtcNow() => frozen ?? DateTimeOffset.UtcNow + offset;
    public void Advance(TimeSpan amount) { offset += amount; WasChanged = true; }
    public void Freeze(DateTimeOffset at) { frozen = at; WasChanged = true; }
}
sealed class LateExpiryAdmission(IOrganizationUnitSaveAdmission inner, MutableGatewayClock clock, IHttpContextAccessor accessor, bool precise) : IOrganizationUnitSaveAdmission
{
    public bool IsConfigured => inner.IsConfigured;
    public async ValueTask<bool> AdmitAsync(Guid tenant, string actor, OrgCommand command, ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        var admitted = await inner.AdmitAsync(tenant, actor, command, principal, cancellationToken);
        if (admitted)
        {
            if (precise)
            {
                using var envelope = JsonDocument.Parse(Convert.FromBase64String(accessor.HttpContext!.Request.Headers["X-Fanasa-Context"].ToString()));
                var expiry = envelope.RootElement.GetProperty("expiresAt").GetDecimal();
                clock.Freeze(DateTimeOffset.UnixEpoch.AddTicks((long)(expiry * 10000000m) + 1));
            }
            else clock.Advance(TimeSpan.FromSeconds(30));
        }
        return admitted;
    }
}
sealed class LateUsageValidator(IUsageSnapshotValidator inner, MutableGatewayClock clock) : IUsageSnapshotValidator
{
    public void ValidateNew(UsageEventV1 message)
    {
        inner.ValidateNew(message);
        clock.Advance(TimeSpan.FromSeconds(30));
    }
}

sealed class GatewayFixture : IAsyncDisposable
{
    public const string Issuer = "https://synthetic-pcc-idp.invalid/realms/fanasa";
    public const string ConsumerAudience = "fanasa-organization-fabric-api";
    public const string ProducerAudience = "fanasa-access-management-web";
    public const string ConsumerClientKey = "synthetic-org-client";
    public const string ProducerClientKey = "synthetic-contracts-client";
    public const string ProducerScope = "contracts.projection.write";
    public const string GatewayClientKey = "synthetic-gateway-client";
    public const string GatewayScope = "gateway.policy.evaluate";
    private const string Secret = "synthetic-context-secret-at-least-32-bytes-local-only";
    private readonly string directory;
    private readonly WebApplication app;
    private readonly HttpClient client;
    private readonly HttpClient noCertificateClient;
    private readonly HttpClient wrongCertificateClient;
    private readonly RSA rsa;
    private readonly RsaSecurityKey key;
    private readonly X509Certificate2 serverCertificate;
    private readonly X509Certificate2 clientCertificate;
    private readonly X509Certificate2 wrongCertificate;
    private readonly MutableGatewayClock? fixtureClock;
    public FabricDatabase Database { get; }
    public string DatabasePath => Path.Combine(directory, "fabric", "fabric.db");
    public PersistentAccessManagement Access { get; }
    public PlatformRegistry Registry { get; }
    public ContractPolicyStore Store { get; }
    public ContractPolicyModule Module { get; }
    public OrganizationStore Organization => app.Services.GetRequiredService<OrganizationStore>();
    public AccountingStore Billing => app.Services.GetRequiredService<AccountingStore>();
    public Guid Tenant { get; }
    public Guid OtherTenant { get; }
    public string Actor { get; } = Guid.NewGuid().ToString("D");
    public AccessRole Role { get; }
    public ProductClient GatewayClient { get; }
    public ContractProjectionEvent Event { get; }
    private TaskCompletionSource? gatewayBodyRead;
    private static string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    private static decimal Epoch(DateTimeOffset value) => (value.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10000000m;

    private GatewayFixture(string mode)
    {
        if (mode == "uppercase-subject") Actor = Actor.ToUpperInvariant();
        directory = Path.Combine(Path.GetTempPath(), "synthetic-gateway-contract-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        serverCertificate = Certificate("CN=localhost", true); clientCertificate = Certificate("CN=synthetic-gateway", false); wrongCertificate = Certificate("CN=wrong-gateway", false);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0, listener => listener.UseHttps(tls =>
        {
            tls.ServerCertificate = serverCertificate; tls.ClientCertificateMode = ClientCertificateMode.AllowCertificate;
            // Local self-signed test identities; the production receiver applies its exact certificate pin.
            tls.ClientCertificateValidation = (_, _, _) => true;
        })));
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Platform:RegistryPath"] = Path.Combine(directory, "registry.db"), ["Fabric:DataDirectory"] = Path.Combine(directory, "fabric"),
            ["Organization:DataDirectory"] = Path.Combine(directory, "legacy-org"), ["Accounting:DataDirectory"] = Path.Combine(directory, "legacy-accounting"),
            ["Authentication:Authority"] = Issuer,
            ["Organization:Bearer:Enabled"] = "true", ["Organization:Bearer:Authority"] = Issuer,
            ["Organization:Bearer:Audience"] = ConsumerAudience, ["Organization:Bearer:ClientId"] = ConsumerClientKey, ["Organization:Bearer:Scope"] = "organization.unit.save",
            ["ContractPolicy:Organization:Enabled"] = "true", ["ContractPolicy:Organization:Environment"] = "production", ["ContractPolicy:Organization:Zone"] = "ir-tehran",
            ["ContractPolicy:Organization:MaximumSnapshotAgeSeconds"] = "30",
            ["ContractPolicy:Producer:Enabled"] = "true", ["ContractPolicy:Producer:Authority"] = Issuer,
            ["ContractPolicy:Producer:Audience"] = ProducerAudience, ["ContractPolicy:Producer:ClientId"] = ProducerClientKey,
            ["ContractPolicy:Producer:Scope"] = ProducerScope, ["ContractPolicy:Producer:ProductKey"] = "contracts",
            ["ContractPolicy:GatewayDecision:Enabled"] = "true", ["ContractPolicy:GatewayDecision:Authority"] = Issuer,
            ["ContractPolicy:GatewayDecision:Audience"] = ProducerAudience, ["ContractPolicy:GatewayDecision:ClientId"] = GatewayClientKey,
            ["ContractPolicy:GatewayDecision:Scope"] = GatewayScope, ["ContractPolicy:GatewayDecision:ProductKey"] = "api-gateway",
            ["ContractPolicy:GatewayRequest:Enabled"] = mode == "request-disabled" ? "false" : "true", ["ContractPolicy:GatewayRequest:MaximumBodyBytes"] = "65536",
            ["ContractPolicy:GatewayRequest:Keys:0:KeyId"] = "synthetic-context-key", ["ContractPolicy:GatewayRequest:Keys:0:Tier"] = "edge",
            ["ContractPolicy:GatewayRequest:Keys:0:Secret"] = Secret,
            ["ContractPolicy:GatewayRequest:Keys:0:ClientCertificateSha256"] = Convert.ToHexString(SHA256.HashData(clientCertificate.RawData))
        });
        Registry = new PlatformRegistry(builder.Configuration); builder.Services.AddSingleton(Registry);
        builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(directory, "synthetic-keys")));
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
        builder.Services.AddAuthorization(options => options.AddPolicy("PlatformAdmin", policy => policy.RequireAuthenticatedUser().RequireClaim("permission", "platform.admin")));
        builder.Services.AddRazorPages();
        MutableGatewayClock? lateClock = mode is "late-admission-expiry" or "late-write-expiry" or "late-subsecond-expiry" or "tariff-expiry" ? new() : null;
        fixtureClock = lateClock;
        if (lateClock is not null) builder.Services.AddSingleton<TimeProvider>(lateClock);
        FabricServices.AddIdentityOrganizationFabric(builder.Services, builder.Configuration, builder.Environment);
        builder.Services.AddOrganizationBearerSecurity(builder.Configuration);
        builder.Services.AddControllers().AddApplicationPart(typeof(OrganizationOperationApi).Assembly);
        if (lateClock is not null && mode is "late-admission-expiry" or "late-subsecond-expiry")
            builder.Services.AddSingleton<IOrganizationUnitSaveAdmission>(services => new LateExpiryAdmission(
                new ContractPolicyOrganizationAdmission(services.GetRequiredService<OrganizationContractPolicyDecisionService>(),
                    services.GetRequiredService<IContractPolicyObligationExecutor>(), services.GetRequiredService<ContractPolicyModule>(),
                    services.GetRequiredService<TimeProvider>()), lateClock, services.GetRequiredService<IHttpContextAccessor>(), mode == "late-subsecond-expiry"));
        if (lateClock is not null && mode == "late-write-expiry")
            builder.Services.AddSingleton<IUsageSnapshotValidator>(services => new LateUsageValidator(
                services.GetRequiredService<OrganizationUsageMetering>(), lateClock));
        rsa = RSA.Create(2048); key = new RsaSecurityKey(rsa) { KeyId = "synthetic-gateway-key" };
        foreach (var scheme in new[] { OrganizationBearerSecurity.Scheme, "ContractProducerBearer", "GatewayDecisionBearer" })
            builder.Services.PostConfigure<JwtBearerOptions>(scheme, options =>
            {
                options.Authority = null; options.MetadataAddress = "";
                var metadata = new OpenIdConnectConfiguration { Issuer = Issuer }; metadata.SigningKeys.Add(key);
                options.Configuration = metadata; options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata);
            });
        app = builder.Build();
        Database = app.Services.GetRequiredService<FabricDatabase>(); Access = app.Services.GetRequiredService<PersistentAccessManagement>();
        Store = app.Services.GetRequiredService<ContractPolicyStore>(); Module = app.Services.GetRequiredService<ContractPolicyModule>();
        var owner = Access.GetTenants().Single().Id;
        Tenant = Access.CreateTenant("synthetic-consumer", "Synthetic Consumer").Id; OtherTenant = Access.CreateTenant("synthetic-other", "Synthetic Other").Id;
        Registry.SetTenantSource(Access.GetTenants); Registry.SetMembershipSource(Access.GetUsers);
        var product = Registry.Register(new(Guid.NewGuid(), "organization-fabric", "Synthetic Organization", ConsumerAudience, "fanasa.ayin", null, null, owner, "synthetic-owner"), "synthetic-test");
        var contracts = Registry.Register(new(Guid.NewGuid(), "contracts", "Synthetic Producer", ProducerAudience, "fanasa.ayin", null, null, owner, "synthetic-owner"), "synthetic-test");
        var gateway = Registry.Register(new(Guid.NewGuid(), "api-gateway", "Synthetic Gateway", "synthetic-gateway-api", "fanasa.ayin", null, null, owner, "synthetic-owner"), "synthetic-test");
        Access.RegisterClient(product.Id, new(ConsumerClientKey, "Synthetic consumer", "confidential", [], ["organization.unit.save"]));
        Access.RegisterClient(contracts.Id, new(ProducerClientKey, "Synthetic producer", "confidential", [], [ProducerScope]));
        GatewayClient = Access.RegisterClient(gateway.Id, new(GatewayClientKey, "Synthetic gateway", "confidential", [], [GatewayScope]));
        Access.AddUser(new(Tenant, Actor, "Synthetic member")); RestoreGrant();
        Role = Access.CreateRole(new(Tenant, "organization-fabric", "ApiInvoker", "Synthetic API Invoker", ["organization.write"]));
        var template = ContractJson.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "integration-projection-event.v2.json")))!.Deserialize<ContractProjectionEvent>(ContractJson.Options)!;
        var now = DateTimeOffset.UtcNow; var scope = template.Projection.Limits.Scope with { ConsumerTenantId = Tenant.ToString("D"), Environments = ["production"] };
        Event = template with { EventId = "synthetic-live-contract-7", OccurredAt = Utc(now), Projection = template.Projection with
        {
            OwnerTenantId = owner.ToString("D"), ConsumerTenantId = Tenant.ToString("D"), ValidFrom = Utc(now.AddDays(-1)), ValidTo = Utc(now.AddDays(1)),
            EffectiveAt = Utc(now.AddMinutes(-1)), ZoneConstraints = ["ir-tehran"],
            Limits = template.Projection.Limits with { Scope = scope, CanonicalScope = ContractProjectionValidator.CanonicalScope(scope) }
        }};
        Store.PublishPolicy(Tenant, Event.Projection.Policy, 0, "synthetic-publisher", "independent server policy publication"); AssignRole(true);
        var producerPrincipal = new ClaimsPrincipal(new ClaimsIdentity([new("iss", Issuer), new("aud", ProducerAudience), new("azp", ProducerClientKey), new("scope", ProducerScope)], "synthetic-local-producer"));
        var ingested = Store.Apply(JsonSerializer.Serialize(Event, ContractJson.Options), producerPrincipal, new(Issuer, ProducerAudience, ProducerClientKey, ProducerScope), () => true);
        if (ingested.Status != "applied") throw new InvalidOperationException("Fixture projection not applied: " + ingested.Status);
        var plan = Access.RegisterPlan(new("organization-fabric", "synthetic-post", "Synthetic plan", "postpaid", "IRR", 0, 0));
        Access.Subscribe(new(Tenant, "organization-fabric", plan.Id, 25));
        Access.ApplyContractEvent(new("synthetic-usage-event", Tenant, Event.Projection.ContractId, "organization-fabric", "fanasa.ayin", "synthetic-post", "ContractActivated", null, "change", now.AddMinutes(-1)));
        const string prefix = "UsageMetering:Organization:Bindings:0:";
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["UsageMetering:Organization:Enabled"] = "true", ["UsageMetering:Organization:Environment"] = "production", ["UsageMetering:Organization:ProducerId"] = "synthetic-backend", ["UsageMetering:Organization:DeploymentId"] = "synthetic-kestrel-tls",
            [prefix + "ConsumerTenantId"] = Tenant.ToString("D"), [prefix + "ContractId"] = Event.Projection.ContractId, [prefix + "ContractVersion"] = (Event.Projection.ContractVersion + (mode == "mismatched-rate-contract" ? 1 : 0)).ToString(CultureInfo.InvariantCulture),
            [prefix + "AllocationId"] = Event.Projection.Limits.AllocationId, [prefix + "AllocationPeriodId"] = now.ToString("yyyy-MM", CultureInfo.InvariantCulture), [prefix + "PlanKey"] = "synthetic-post",
            [prefix + "PricingReference"] = Event.Projection.PricingReference.PriceId, [prefix + "RateSnapshot:RateId"] = Event.Projection.PricingReference.PriceId, [prefix + "RateSnapshot:RateVersion"] = Event.Projection.PricingReference.PriceVersion.ToString(CultureInfo.InvariantCulture),
            [prefix + "RateSnapshot:EffectiveFrom"] = Utc(now.AddHours(-1)), [prefix + "RateSnapshot:EffectiveTo"] = Utc(mode == "tariff-expiry" ? now.AddMinutes(2) : now.AddHours(1)), [prefix + "RateSnapshot:UnitPrice"] = "12", [prefix + "RateSnapshot:Currency"] = "IRR",
            [prefix + "RateSnapshot:MinorUnitScale"] = "0", [prefix + "RateSnapshot:Rounding"] = "away-from-zero", [prefix + "RateSnapshot:BillingMode"] = "postpaid"
        });
        Billing.Execute(Tenant, new(0, "configure", "synthetic-credit", "postpaid", "IRR", 1000, null, null, 0, 0, 0, "synthetic credit", null, null), "synthetic-test");
        app.UseRouting(); app.UseAuthentication(); app.UseAuthorization();
        app.Use(async (HttpContext http, RequestDelegate next) =>
        {
            var observed = gatewayBodyRead;
            if (observed is null || http.Request.Path != "/api/gateway/decision") { await next(http); return; }
            gatewayBodyRead = null; var original = http.Request.Body; http.Request.Body = new ReadObservedStream(original, observed);
            try { await next(http); } finally { http.Request.Body = original; }
        });
        app.MapControllers().RequireAuthorization();
        client = Client(clientCertificate); noCertificateClient = Client(null); wrongCertificateClient = Client(wrongCertificate);
    }

    public static async Task<GatewayFixture> Start(string mode = "configured")
    {
        var fixture = new GatewayFixture(mode); await fixture.app.StartAsync();
        var address = new Uri(fixture.app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        fixture.client.BaseAddress = address; fixture.noCertificateClient.BaseAddress = address; fixture.wrongCertificateClient.BaseAddress = address; return fixture;
    }
    private HttpClient Client(X509Certificate2? certificate)
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false,
            ServerCertificateCustomValidationCallback = (_, presented, _, _) => presented is not null && CryptographicOperations.FixedTimeEquals(SHA256.HashData(presented.RawData), SHA256.HashData(serverCertificate.RawData)) };
        if (certificate is not null) handler.ClientCertificates.Add(certificate);
        return new(handler);
    }
    private static X509Certificate2 Certificate(string name, bool server)
    {
        using var key = RSA.Create(2048); var request = new CertificateRequest(name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new(server ? "1.3.6.1.5.5.7.3.1" : "1.3.6.1.5.5.7.3.2") }, true));
        if (server) { var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("localhost"); names.AddIpAddress(IPAddress.Loopback); request.CertificateExtensions.Add(names.Build()); }
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(2));
        // Windows Schannel needs a temporary key container for real Kestrel TLS.
        // No PersistKeySet or certificate-store registration; Dispose removes the imported key.
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
    }
    public void RestoreGrant() => Access.SetGrant(new(Tenant, Actor, "organization.write", null), "synthetic-test", "restore direct member permission");
    public bool ClockWasChanged => fixtureClock?.WasChanged == true;
    public void AdvanceClock(TimeSpan amount) => (fixtureClock ?? throw new InvalidOperationException("No synthetic clock configured.")).Advance(amount);
    public void PutGatewayClient(ProductClient value) => Database.Transaction(() => { Database.Put("client", value.Id.ToString(), value); return true; });
    public void AssignRole(bool active)
    {
        var previous = Store.ReadRoleAssignment(Tenant, Actor); var revision = previous is null ? 0 : ContractJson.ToNode(previous)!["revision"]!.GetValue<long>();
        var now = DateTimeOffset.UtcNow; Store.SaveRoleAssignment(Tenant, Actor, Role.Id, active, now.AddDays(-1), now.AddDays(1), revision, "synthetic-test", "explicit product role assignment");
    }
    public OrgCommand Command() => new(Organization.Read(Tenant).Revision, "unit.save", null, "Synthetic team", "synthetic-" + Guid.NewGuid().ToString("N"), null, "team", null, 1, null, null, null, null, "synthetic Gateway operation", RequestId: Guid.NewGuid().ToString("D"));
    public string Token(string clientId = ConsumerClientKey, string audience = ConsumerAudience, string scope = "organization.unit.save", string? subject = null, string? issuer = null, bool badKey = false, DateTime? expiry = null)
    {
        using var other = badKey ? RSA.Create(2048) : null;
        var signing = badKey ? new RsaSecurityKey(other!) { KeyId = key.KeyId } : key;
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(issuer ?? Issuer, audience, [new("sub", subject ?? Actor), new("azp", clientId), new("scope", scope)],
            DateTime.UtcNow.AddMinutes(-10), expiry ?? DateTime.UtcNow.AddMinutes(5), new SigningCredentials(signing, SecurityAlgorithms.RsaSha256)));
    }
    public string GatewayToken(string mode = "valid")
    {
        var token = mode switch
        {
            "signature" => Token(GatewayClientKey, ProducerAudience, GatewayScope, "service-account-gateway", badKey: true),
            "issuer" => Token(GatewayClientKey, ProducerAudience, GatewayScope, "service-account-gateway", issuer: "https://other.invalid/realms/fanasa"),
            "audience" => Token(GatewayClientKey, "wrong-audience", GatewayScope, "service-account-gateway"),
            "expired" => Token(GatewayClientKey, ProducerAudience, GatewayScope, "service-account-gateway", expiry: DateTime.UtcNow.AddMinutes(-1)),
            "client" => Token("other-client", ProducerAudience, GatewayScope, "service-account-gateway"),
            "scope" => Token(GatewayClientKey, ProducerAudience, "other.scope", "service-account-gateway"),
            "human" => Token(), "producer" => Token(ProducerClientKey, ProducerAudience, ProducerScope),
            _ => Token(GatewayClientKey, ProducerAudience, GatewayScope, "service-account-gateway")
        };
        if (mode is "valid" or "signature" or "issuer" or "audience" or "expired" or "client" or "scope" or "human" or "producer") return token;
        var parts = token.Split('.'); var payload = Base64UrlEncoder.Decode(parts[1]); var node = JsonNode.Parse(payload)!;
        payload = mode switch
        {
            "duplicate-iss" => payload.Insert(1, "\"iss\":" + JsonSerializer.Serialize(Issuer) + ","),
            "escaped-iss" => payload.Insert(1, "\"\\u0069ss\":" + JsonSerializer.Serialize(Issuer) + ","),
            "quoted-exp" => Modify(node, "exp", node["exp"]!.ToJsonString()),
            "fractional-exp" => System.Text.RegularExpressions.Regex.Replace(payload, "\"exp\":([0-9]+)", "$0.0"),
            "array-scope" => Modify(node, "scope", new JsonArray(GatewayScope)),
            "duplicate-aud" => Modify(node, "aud", new JsonArray(ProducerAudience, ProducerAudience)),
            "wrong-active" => Modify(node, "active", false),
            _ => throw new ArgumentException(mode)
        };
        var unsigned = parts[0] + "." + Base64UrlEncoder.Encode(payload); return unsigned + "." + Base64UrlEncoder.Encode(rsa.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }
    private static string Modify(JsonNode node, string key, JsonNode? value) { node[key] = value; return node.ToJsonString(); }
    public string PolicyRequest(string mode = "valid", string? nonce = null)
    {
        var p = Event.Projection; var now = fixtureClock?.GetUtcNow() ?? DateTimeOffset.UtcNow;
        var request = new ContractGatewayPolicyRequest("fanasa.gateway-pdp.request.v2", nonce ?? "g-" + Guid.NewGuid().ToString("N"), Actor, ConsumerClientKey, Tenant.ToString("D"), p.OwnerTenantId,
            p.ProductId, "production", "organization.unit.save", "organization", ConsumerAudience, "organization.unit.save", p.Limits.AllocationId, p.Limits.CanonicalScope, Epoch(now), new JwtSecurityTokenHandler().ReadJwtToken(Token()).Payload.Expiration!.Value);
        var node = JsonSerializer.SerializeToNode(request, ContractJson.Options)!;
        switch (mode)
        {
            case "unknown": node["callerEvidence"] = true; break;
            case "duplicate": return node.ToJsonString().Insert(1, "\"subjectId\":" + JsonSerializer.Serialize(Actor) + ",");
            case "environment": node["environment"] = "development"; break;
            case "owner": node["ownerTenantId"] = OtherTenant.ToString("D"); break;
            case "allocation": node["allocationId"] = "foreign-allocation"; break;
            case "scope": node["allocationScope"] = "foreign-scope"; break;
            case "consumer": node["consumerTenantId"] = OtherTenant.ToString("D"); break;
            case "requested-future": node["requestedAt"] = Epoch(now.AddMinutes(1)); break;
            case "requested-stale": node["requestedAt"] = Epoch(now.AddMinutes(-1)); break;
            case "expiry": node["tokenExpiresAt"] = now.AddMinutes(-1).ToUnixTimeSeconds(); break;
            case "quoted-expiry": node["tokenExpiresAt"] = request.TokenExpiresAt.ToString(CultureInfo.InvariantCulture); break;
            case "missing": node.AsObject().Remove("subjectId"); break;
        }
        return node.ToJsonString();
    }
    public async Task<HttpResponseMessage> Decide(string? token = null, string? body = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/gateway/decision") { Content = new StringContent(body ?? PolicyRequest(), Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? GatewayToken()); return await client.SendAsync(request);
    }
    public Task ObserveNextGatewayBodyRead() => (gatewayBodyRead = new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
    public async Task<HttpResponseMessage> DecideContent(HttpContent content, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/gateway/decision") { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token); return await client.SendAsync(request);
    }
    public SignedRequest Witness(OrgCommand command, string mode = "valid")
    {
        var token = Token(); var now = fixtureClock?.GetUtcNow() ?? DateTimeOffset.UtcNow; var p = Event.Projection; var nonce = "g-" + Guid.NewGuid().ToString("N");
        var start = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero); var deadline = Epoch(now.AddSeconds(20));
        var context = new JsonObject
        {
            ["requestId"] = nonce, ["decisionId"] = nonce, ["subjectId"] = Actor, ["clientId"] = ConsumerClientKey, ["consumerTenantId"] = Tenant.ToString("D"),
            ["ownerTenantId"] = p.OwnerTenantId, ["productId"] = p.ProductId, ["environment"] = "production", ["action"] = "organization.unit.save", ["policyVersion"] = p.Policy.PolicyId + "@" + p.Policy.PolicyVersion,
            ["contractId"] = p.ContractId, ["contractVersion"] = p.ContractVersion, ["allocationId"] = p.Limits.AllocationId, ["allocationScope"] = p.Limits.CanonicalScope, ["configRevision"] = p.Limits.ConfigRevision,
            ["allocationPeriodId"] = start.ToString("yyyy-MM", CultureInfo.InvariantCulture), ["periodStartMs"] = start.ToUnixTimeMilliseconds(), ["periodEndMs"] = start.AddMonths(1).ToUnixTimeMilliseconds(),
            ["resourceTenant"] = new JsonObject { ["consumerTenantId"] = Tenant.ToString("D"), ["productId"] = p.ProductId, ["environment"] = "production", ["action"] = "organization.unit.save" },
            ["decisionValidUntil"] = deadline, ["tokenExpiresAt"] = new JwtSecurityTokenHandler().ReadJwtToken(token).Payload.Expiration, ["admissionId"] = "admission:" + nonce, ["allowed"] = true, ["confirmed"] = true
        };
        var envelope = new JsonObject { ["version"] = 1, ["keyId"] = "synthetic-context-key", ["tier"] = "edge", ["issuedAt"] = Epoch(now), ["expiresAt"] = deadline, ["context"] = context };
        var body = JsonSerializer.Serialize(command, ContractJson.Options); var path = $"/api/organization-operations/tenants/{Tenant:D}/unit.save"; var idempotency = command.RequestId!;
        switch (mode)
        {
            case "expired": envelope["issuedAt"] = Epoch(now.AddSeconds(-10)); envelope["expiresAt"] = Epoch(now.AddSeconds(-1)); break;
            case "future": envelope["issuedAt"] = Epoch(now.AddSeconds(1)); break;
            case "too-long": envelope["expiresAt"] = Epoch(now.AddSeconds(31)); context["decisionValidUntil"] = Epoch(now.AddSeconds(32)); break;
            case "wrong-tenant": context["consumerTenantId"] = OtherTenant.ToString("D"); context["resourceTenant"]!["consumerTenantId"] = OtherTenant.ToString("D"); break;
            case "wrong-subject": context["subjectId"] = Guid.NewGuid().ToString("D"); break;
            case "wrong-client": context["clientId"] = "other-client"; break;
            case "wrong-policy": context["policyVersion"] = p.Policy.PolicyId + "@999"; break;
            case "wrong-contract": context["contractVersion"] = p.ContractVersion + 1; break;
            case "wrong-revision": context["configRevision"] = p.Limits.ConfigRevision + 1; break;
            case "wrong-period": context["allocationPeriodId"] = start.AddMonths(-1).ToString("yyyy-MM", CultureInfo.InvariantCulture); break;
            case "unknown-context": context["callerEvidence"] = true; break;
            case "unknown-envelope": envelope["callerEvidence"] = true; break;
            case "not-confirmed": context["confirmed"] = false; break;
            case "not-allowed": context["allowed"] = false; break;
            case "foreign-resource": context["resourceTenant"]!["consumerTenantId"] = OtherTenant.ToString("D"); break;
            case "unknown-key": envelope["keyId"] = "other-key"; break;
            case "wrong-tier": envelope["tier"] = "internal"; break;
            case "oversize-body": body += new string(' ', 65536); break;
            case "unknown-command": body = body.Insert(1, "\"callerProof\":true,"); break;
            case "duplicate-command": body = body.Insert(1, "\"requestId\":" + JsonSerializer.Serialize(command.RequestId) + ","); break;
        }
        var json = envelope.ToJsonString();
        if (mode == "duplicate-envelope") json = json.Insert(1, "\"version\":1,");
        if (mode == "duplicate-context") json = json.Replace("\"context\":{", "\"context\":{\"confirmed\":true,");
        if (mode == "noncanonical-number")
        {
            var field = "\"configRevision\":" + p.Limits.ConfigRevision!.Value.ToString(CultureInfo.InvariantCulture);
            var changed = json.Replace(field + ",", field + ".0,");
            if (changed == json) throw new InvalidOperationException("Fixture did not produce a noncanonical signed revision.");
            json = changed;
        }
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        var signature = Sign(path, body, token, idempotency, encoded);
        return new SignedRequest(path, mode == "wrong-body" ? body + " " : body, mode == "wrong-auth" ? Token(subject: Guid.NewGuid().ToString("D")) : token,
            mode == "missing" ? null : encoded, mode == "forged-signature" ? new string('0', 64) : signature,
            mode == "wrong-idempotency" ? "other-idempotency" : idempotency, mode == "duplicate-header", mode == "underscore-header")
            with { Path = mode == "wrong-path" ? path + "?forged=true" : path };
    }
    private static string Sign(string path, string body, string token, string idempotency, string encoded)
    {
        static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        var parts = new[] { "fanasa.context.v1", "POST", path, Hash("Bearer " + token), Hash(body), Hash(idempotency), encoded };
        var frame = string.Concat(parts.Select(part => Encoding.UTF8.GetByteCount(part).ToString(CultureInfo.InvariantCulture) + ":" + part));
        return Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(Secret), Encoding.UTF8.GetBytes(frame))).ToLowerInvariant();
    }
    public Task<HttpResponseMessage> Operate(OrgCommand command, string mode = "valid", string clientMode = "valid") => SendWitness(Witness(command, mode), clientMode);
    public async Task<HttpResponseMessage> SendWitness(SignedRequest value, string clientMode = "valid")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, value.Path) { Content = new StringContent(value.Body, Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", value.Token); request.Headers.Add("Idempotency-Key", value.Idempotency);
        if (value.Context is not null)
        {
            request.Headers.TryAddWithoutValidation(value.UnderscoreHeader ? "X_Fanasa_Context" : "X-Fanasa-Context", value.DuplicateHeader ? [value.Context, value.Context] : [value.Context]);
            request.Headers.Add("X-Fanasa-Context-Signature", value.Signature);
        }
        return await (clientMode == "none" ? noCertificateClient : clientMode == "wrong" ? wrongCertificateClient : client).SendAsync(request);
    }
    public string BusinessSnapshot() => JsonSerializer.Serialize(new
    {
        Organization = Database.All<JsonElement>("organization"), History = Database.All<JsonElement>("organization.history"), Receipts = Database.All<JsonElement>("organization.receipt"),
        Usage = Database.All<JsonElement>("measured.event"), Measured = Database.All<JsonElement>("measured.operation"), Ledger = Database.All<JsonElement>("accounting"), Outbox = Database.Outbox(take: 100)
    });
    public async ValueTask DisposeAsync()
    {
        client.Dispose(); noCertificateClient.Dispose(); wrongCertificateClient.Dispose(); await app.StopAsync(); await app.DisposeAsync(); rsa.Dispose(); serverCertificate.Dispose(); clientCertificate.Dispose(); wrongCertificate.Dispose(); SqliteConnection.ClearAllPools();
        var full = Path.GetFullPath(directory); var temp = Path.GetFullPath(Path.GetTempPath());
        if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("synthetic-gateway-contract-policy-", StringComparison.Ordinal)) throw new InvalidOperationException("Invalid test cleanup directory");
        Directory.Delete(full, true);
    }
}
