using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Fanasa.AccessManagement.Web.Api;
using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Domain.Access;
using Fanasa.AccessManagement.Web.Metering;
using Fanasa.AccessManagement.Web.Organization;
using Fanasa.AccessManagement.Web.Persistence;
using Fanasa.AccessManagement.Web.Platform;
using Fanasa.AccessManagement.Web.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

// All keys, certificates, identities, registrations and subscriptions are disposable synthetic data.
// HTTP requests traverse the production Kestrel TLS, JWT extension, boundary and HR controller.
var checks = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new Exception("FAILED: " + label);
    checks++; Console.WriteLine("PASS: " + label);
}

await using (var fixture = await SyntheticHrHost.Start())
{
    async Task Denied(string label, HttpStatusCode status, string? token = null, OrgCommand? command = null,
        Guid? tenant = null, bool gateway = true, string? raw = null, bool idempotency = true,
        string contentType = "application/json", bool forged = false, bool encoded = false)
    {
        var before = fixture.Snapshot();
        using var response = await fixture.Post(token, command, tenant, gateway, raw, idempotency, contentType, forged, encoded);
        Check(response.StatusCode == status, label + " HTTP status (actual " + (int)response.StatusCode + ")");
        Check(fixture.Snapshot() == before, label + " leaves chart/history/receipts/outbox unchanged");
    }

    Check(fixture.Options.IsConfigured, "complete synthetic HTTPS service configuration is accepted");
    Check(OrganizationHrHosting.IsPinnedGatewayCertificate(fixture.GatewayCertificate,
        SHA256.HashData(fixture.GatewayCertificate.RawData)), "matching live clientAuth TLS certificate is accepted");
    Check(!OrganizationHrHosting.IsPinnedGatewayCertificate(fixture.OtherCertificate,
        SHA256.HashData(fixture.GatewayCertificate.RawData)), "different actual TLS peer certificate is rejected");
    Check(!OrganizationHrHosting.IsPinnedGatewayCertificate(fixture.ServerCertificate,
        SHA256.HashData(fixture.ServerCertificate.RawData)), "serverAuth-only certificate cannot impersonate Gateway");
    Check(!new OrganizationHrOptions { Enabled = true, Authority = "http://synthetic.invalid/realms/fanasa" }.IsConfigured,
        "incomplete or plaintext service configuration is rejected");

    await Denied("anonymous", HttpStatusCode.Unauthorized);
    await fixture.IssueCookie();
    await Denied("valid browser cookie alone", HttpStatusCode.Unauthorized);
    await Denied("invalid signing key", HttpStatusCode.Unauthorized, fixture.Token(badKey: true));
    await Denied("wrong issuer", HttpStatusCode.Unauthorized, fixture.Token(issuer: "https://forged.invalid/realms/fanasa"));
    await Denied("wrong audience", HttpStatusCode.Unauthorized, fixture.Token(audience: "different-api"));
    await Denied("expired JWT", HttpStatusCode.Unauthorized, fixture.Token(expired: true));
    foreach (var timing in new[] { "missing-exp", "array-exp", "noncanonical-exp", "future-nbf", "array-sub", "missing-azp", "array-azp", "array-client_id", "duplicate-property" })
        await Denied(timing + " signed JWT", HttpStatusCode.Unauthorized, fixture.TimingToken(timing));
    await Denied("valid token without actual Gateway TLS certificate", HttpStatusCode.Forbidden, fixture.Token(), gateway: false);
    await Denied("forged Gateway and permission headers without TLS certificate", HttpStatusCode.Forbidden,
        fixture.Token(), gateway: false, forged: true);
    await Denied("wrong service subject", HttpStatusCode.Forbidden, fixture.Token(subject: fixture.HumanSubject));
    await Denied("wrong client", HttpStatusCode.Forbidden, fixture.Token(client: "other-client"));
    await Denied("missing write scope", HttpStatusCode.Forbidden, fixture.Token(scope: "openid"));
    await Denied("read scope cannot write", HttpStatusCode.Forbidden, fixture.Token(scope: SyntheticHrHost.ReadScope));
    await Denied("conflicting client_id", HttpStatusCode.Forbidden, fixture.Token(extra: [new("client_id", "other-client")]));
    await Denied("inactive signed identity", HttpStatusCode.Forbidden, fixture.Token(extra: [new("active", "false")]));
    await Denied("other tenant", HttpStatusCode.Forbidden, fixture.Token(), tenant: fixture.OtherTenant);

    using (var response = await fixture.Chart(fixture.Token(scope: SyntheticHrHost.ReadScope)))
        Check(response.StatusCode == HttpStatusCode.OK && (await response.Content.ReadFromJsonAsync<OrgState>())?.Revision == 0,
            "authorized service reads a real empty tenant chart over TLS");
    using (var response = await fixture.Chart(fixture.Token()))
        Check(response.StatusCode == HttpStatusCode.Forbidden, "write scope cannot read");
    foreach (var probe in new[]
    {
        (Operation: "read", Scope: SyntheticHrHost.ReadScope, Gateway: true, Status: HttpStatusCode.NoContent),
        (Operation: "write", Scope: SyntheticHrHost.WriteScope, Gateway: true, Status: HttpStatusCode.NoContent),
        (Operation: "write", Scope: SyntheticHrHost.ReadScope, Gateway: true, Status: HttpStatusCode.Forbidden),
        (Operation: "read", Scope: SyntheticHrHost.ReadScope, Gateway: false, Status: HttpStatusCode.Forbidden),
        (Operation: "unknown", Scope: SyntheticHrHost.ReadScope, Gateway: true, Status: HttpStatusCode.BadRequest)
    })
    {
        var before = fixture.Snapshot();
        using var response = await fixture.GatewayAccess(fixture.Token(scope: probe.Scope), probe.Operation, probe.Gateway);
        Check(response.StatusCode == probe.Status, $"Gateway auth {probe.Operation}/{probe.Scope}/peer={probe.Gateway} returns {(int)probe.Status}");
        Check(fixture.Snapshot() == before, "Gateway auth subrequest never changes durable records");
    }
    try
    {
        using var response = await fixture.Chart(fixture.Token(scope: SyntheticHrHost.ReadScope), otherCertificate: true);
        Check(false, "non-pinned TLS peer must fail before HTTP");
    }
    catch (HttpRequestException) { Check(true, "non-pinned client certificate fails the TLS handshake"); }

    fixture.Access.SetGrant(new(fixture.Tenant, fixture.ServiceSubject, "organization.write", DateTimeOffset.UtcNow.AddMinutes(-1)),
        "synthetic-test", "synthetic grant revocation");
    await Denied("revoked durable grant", HttpStatusCode.Forbidden, fixture.Token());
    fixture.RestoreGrants();
    fixture.PutClient(fixture.RegisteredClient with { IsActive = false });
    await Denied("inactive central client", HttpStatusCode.Forbidden, fixture.Token());
    fixture.PutClient(fixture.RegisteredClient with { AllowedScopes = [SyntheticHrHost.ReadScope] });
    await Denied("write scope removed from central client", HttpStatusCode.Forbidden, fixture.Token());
    fixture.PutClient(fixture.RegisteredClient);
    var tenantRecord = fixture.Access.GetTenants().Single(x => x.Id == fixture.Tenant);
    fixture.Database.Transaction(() => { fixture.Database.Put("tenant", fixture.Tenant.ToString(), tenantRecord with { IsActive = false }); return true; });
    await Denied("inactive durable tenant", HttpStatusCode.Forbidden, fixture.Token());
    fixture.Database.Transaction(() => { fixture.Database.Put("tenant", fixture.Tenant.ToString(), tenantRecord); return true; });
    fixture.Access.SetMembership(fixture.Tenant, fixture.ServiceMember.Id, false, "synthetic-test", "synthetic offboarding");
    await Denied("offboarded service membership", HttpStatusCode.Forbidden, fixture.Token());
    fixture.Access.SetMembership(fixture.Tenant, fixture.ServiceMember.Id, true, "synthetic-test", "synthetic onboarding");
    await Denied("reactivation does not resurrect revoked grants", HttpStatusCode.Forbidden, fixture.Token());
    fixture.RestoreGrants();
    var subscription = fixture.Access.GetSubscriptions(fixture.Tenant).Single();
    fixture.Access.SetSubscription(fixture.Tenant, subscription.Id, "suspended", null, "synthetic-test", "synthetic suspension");
    await Denied("suspended product subscription", HttpStatusCode.Forbidden, fixture.Token());
    fixture.Access.SetSubscription(fixture.Tenant, subscription.Id, "active", DateTimeOffset.UtcNow.AddMonths(1), "synthetic-test", "synthetic renewal");

    var request = fixture.Command();
    var requestJson = JsonSerializer.Serialize(request, SyntheticHrHost.JsonOptions);
    await Denied("missing Idempotency-Key", HttpStatusCode.BadRequest, fixture.Token(), request, idempotency: false);
    await Denied("unsupported content type", HttpStatusCode.UnsupportedMediaType, fixture.Token(), request, contentType: "text/plain");
    await Denied("unsupported content encoding", HttpStatusCode.UnsupportedMediaType, fixture.Token(), request, encoded: true);
    await Denied("unknown JSON field", HttpStatusCode.BadRequest, fixture.Token(), request,
        raw: requestJson[..^1] + ",\"permission\":\"organization.write\"}");
    await Denied("duplicate JSON field", HttpStatusCode.BadRequest, fixture.Token(), request,
        raw: requestJson[..^1] + ",\"expectedRevision\":0}");
    await Denied("oversized body", HttpStatusCode.RequestEntityTooLarge, fixture.Token(), request,
        raw: requestJson[..^1] + ",\"padding\":\"" + new string('x', OrganizationHrApi.MaximumBodyBytes) + "\"}");
    await Denied("unsupported operation", HttpStatusCode.BadRequest, fixture.Token(), request with { Operation = "membership.create" });
    await Denied("timestamp without UTC or offset", HttpStatusCode.BadRequest, fixture.Token(), request,
        raw: requestJson.Replace("\"from\":null", "\"from\":\"2026-10-12T10:00:00\"", StringComparison.Ordinal));
    foreach (var malformed in new[] { "{}", "[]", "{",
        requestJson.Replace("\"capacity\":1", "\"capacity\":\"1\"", StringComparison.Ordinal),
        requestJson.Replace("\"expectedRevision\":0", "\"expectedRevision\":\"0\"", StringComparison.Ordinal),
        requestJson.Replace("\"from\":null", "\"from\":\"2026-10-12t10:00:00z\"", StringComparison.Ordinal),
        requestJson.Replace("\"from\":null", "\"from\":\"2026-10-12T10:00:00.12345678Z\"", StringComparison.Ordinal) })
        await Denied("strict JSON/timestamp schema", HttpStatusCode.BadRequest, fixture.Token(), request, raw: malformed);
    await Denied("invalid UUID requestId", HttpStatusCode.BadRequest, fixture.Token(), request with { RequestId = "not-a-uuid" });
    await Denied("uppercase requestId", HttpStatusCode.BadRequest, fixture.Token(), request with { RequestId = "A1111111-1111-1111-1111-111111111111" });
    await Denied("zero requestId", HttpStatusCode.BadRequest, fixture.Token(), request with { RequestId = Guid.Empty.ToString("D") });
    foreach (var keys in new[] { new[] { Guid.NewGuid().ToString("D") }, new[] { request.RequestId!, request.RequestId! } })
    {
        var before = fixture.Snapshot();
        using var response = await fixture.Post(fixture.Token(), request, idempotencyKeys: keys);
        Check(response.StatusCode == HttpStatusCode.BadRequest && fixture.Snapshot() == before, "mismatched or repeated idempotency header fails without mutation");
    }
    await Denied("transfer not admitted", HttpStatusCode.BadRequest, fixture.Token(), request with { Operation = "appointment.transfer" });
    await Denied("bulk replacement not admitted", HttpStatusCode.BadRequest, fixture.Token(), request with { Operation = "replace" });
    await Denied("capacity bounds", HttpStatusCode.BadRequest, fixture.Token(), request with { Capacity = 10001 });
    var initial = await fixture.Execute(request);
    Check(initial.Revision == 1 && initial.Units.Single().Code == "synthetic-team", "valid HR unit command commits one chart revision");
    var committed = fixture.Snapshot();
    using (var replay = await fixture.Post(fixture.Token(), request))
        Check(replay.StatusCode == HttpStatusCode.OK && (await replay.Content.ReadFromJsonAsync<OrgState>())?.Revision == 1
            && fixture.Snapshot() == committed, "exact request replay returns original result without duplicate effect or outbox");
    await Denied("changed payload under same request identity", HttpStatusCode.Conflict, fixture.Token(), request with { Name = "changed" });
    await Denied("stale expected revision", HttpStatusCode.Conflict, fixture.Token(), fixture.Command() with { ExpectedRevision = 0 });
    var role = await fixture.Execute(fixture.Command("role.save") with { Name = "Synthetic role", Code = "synthetic-role" });
    Check(fixture.Access.GetRoles(fixture.Tenant).Count == 0 && initial.Changes.Last().Actor == fixture.ServiceSubject,
        "organization role creates no software role; audit actor uses verified service subject");
    var position = await fixture.Execute(fixture.Command("position.save") with { Name = "Synthetic position", Code = "synthetic-position",
        UnitId = initial.Units.Single().Id, RoleId = role.Roles!.Single().Id });
    var appointment = fixture.Command("appointment.add") with { PositionId = position.Positions.Single().Id,
        Subject = fixture.HumanSubject, From = DateTimeOffset.UtcNow.AddDays(-1), To = DateTimeOffset.UtcNow.AddDays(1) };
    await Denied("service identity cannot receive HR appointment", HttpStatusCode.BadRequest, fixture.Token(), appointment with { Subject = fixture.ServiceSubject });
    await Denied("unregistered appointment subject", HttpStatusCode.BadRequest, fixture.Token(), appointment with { Subject = Guid.NewGuid().ToString("D") });
    var appointed = await fixture.Execute(appointment);
    Check(appointed.Appointments.Single().Subject == fixture.HumanSubject, "appointment uses existing active human tenant membership");
    Check(fixture.Access.GetGrants(fixture.HumanSubject).Length == 0, "appointment creates no software grants");
    await Denied("overlapping appointment", HttpStatusCode.BadRequest, fixture.Token(), fixture.Command("appointment.add") with
        { PositionId = position.Positions.Single().Id, Subject = fixture.HumanSubject, From = appointment.From, To = appointment.To });
    await Denied("archive unit with active position", HttpStatusCode.BadRequest, fixture.Token(), fixture.Command("unit.archive") with { Id = initial.Units.Single().Id });
    await Denied("archive position with active appointment", HttpStatusCode.BadRequest, fixture.Token(), fixture.Command("position.archive") with { Id = position.Positions.Single().Id });
    await fixture.Execute(fixture.Command("appointment.end") with { Id = appointed.Appointments.Single().Id, To = DateTimeOffset.UtcNow.AddSeconds(-1) });
    await fixture.Execute(fixture.Command("position.archive") with { Id = position.Positions.Single().Id });
    var archived = await fixture.Execute(fixture.Command("unit.archive") with { Id = initial.Units.Single().Id });
    Check(!archived.Units.Single().Active && !archived.Positions.Single().Active, "valid archive sequence preserves records and ends active entities");
    using (var response = await fixture.Chart(fixture.Token(scope: SyntheticHrHost.ReadScope), revision: 1))
        Check(response.StatusCode == HttpStatusCode.OK && (await response.Content.ReadFromJsonAsync<OrgState>())?.Units.Single().Active == true,
            "historic revision remains immutable after later HR commands");
    foreach (var history in new[] { (Revision: 0L, Status: HttpStatusCode.BadRequest), (Revision: 999L, Status: HttpStatusCode.NotFound) })
    {
        var before = fixture.Snapshot();
        using var response = await fixture.Chart(fixture.Token(scope: SyntheticHrHost.ReadScope), revision: history.Revision);
        Check(response.StatusCode == history.Status && fixture.Snapshot() == before, "nonpositive/missing history cannot change durable records");
    }
    using (var restarted = new FabricDatabase(fixture.DatabasePath))
    {
        Check(restarted.Read<OrgState>("organization", fixture.Tenant.ToString())?.Revision == archived.Revision
            && restarted.All<OrgReceipt>("organization.receipt").Length == 7, "independent database connection sees durable chart and exactly seven command receipts");
        var reopenedAccess = new PersistentAccessManagement(restarted, new InMemoryAccessManagement());
        var reopenedStore = new OrganizationStore(fixture.DataDirectory, reopenedAccess, true, restarted);
        var before = fixture.Snapshot(); var replay = reopenedStore.Execute(fixture.Tenant, request, fixture.ServiceSubject);
        Check(replay.Revision == 1 && fixture.Snapshot() == before, "reopened store replays persisted historical receipt without another effect");
    }
    fixture.Access.SetGrant(new(fixture.Tenant, fixture.ServiceSubject, "organization.write", DateTimeOffset.UtcNow.AddMinutes(-1)),
        "synthetic-test", "synthetic replay revocation");
    await Denied("revoked grant blocks exact historic receipt replay", HttpStatusCode.Forbidden, fixture.Token(), request);
}

foreach (var mode in new[] { "token", "grant" })
{
    await using var fixture = await SyntheticHrHost.Start();
    var expiry = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddSeconds(8).ToUnixTimeSeconds());
    if (mode == "grant") fixture.Access.SetGrant(new(fixture.Tenant, fixture.ServiceSubject, "organization.write", expiry),
        "synthetic-test", "synthetic expiry during Store");
    var command = fixture.Command("role.save") with { Name = "Synthetic rollback role", Code = "synthetic-rollback-role" };
    var before = fixture.Snapshot(); fixture.StoreBarrier.Arm();
    var pending = fixture.Post(fixture.Token(expiresAt: mode == "token" ? expiry.UtcDateTime : null), command);
    try
    {
        var entered = await fixture.StoreBarrier.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        Check(entered < expiry && fixture.StoreBarrier.EnteredInTransaction, mode + " expiry enters Store inside Fabric with live authorization");
        var wait = expiry - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(100);
        if (wait > TimeSpan.Zero) await Task.Delay(wait);
        fixture.StoreBarrier.Release();
        using var response = await pending;
        Check(fixture.StoreBarrier.ReleasedAt >= expiry, mode + " expiry crosses actual deadline during Store processing");
        Check(response.StatusCode == HttpStatusCode.Forbidden, mode + " expiry is denied by the post-Execute boundary");
        Check(fixture.Snapshot() == before, mode + " expiry rolls back chart/history/receipt/outbox");
        if (mode == "grant") fixture.RestoreGrants();
        var outboxBeforeRetry = fixture.Database.Outbox(take: 100).Length;
        var recovered = await fixture.Execute(command);
        Check(recovered.Revision == 1 && recovered.Roles!.Single().Code == command.Code
            && fixture.Database.All<OrgReceipt>("organization.receipt").Length == 1
            && fixture.Database.All<OrgState>("organization.history").Length == 1
            && fixture.Database.Outbox(take: 100).Length == outboxBeforeRetry + 1,
            mode + " expiry leaves no ghost receipt; same request commits once");
        var committed = fixture.Snapshot(); using var replay = await fixture.Post(fixture.Token(), command);
        Check(replay.StatusCode == HttpStatusCode.OK && fixture.Snapshot() == committed, mode + " recovered request replays once");
    }
    finally
    {
        fixture.StoreBarrier.Release();
        if (!pending.IsCompleted) { using var abandoned = await pending; }
    }
}
foreach (var mode in new[] { "disabled", "misconfigured", "dual-listener" })
{
    await using var fixture = await SyntheticHrHost.Start(mode: mode);
    var before = fixture.Snapshot();
    if (mode == "dual-listener")
    {
        using var human = await fixture.HumanGet();
        Check(human.StatusCode == HttpStatusCode.OK, "named HR endpoint preserves independent human HTTP listener");
        using var bypass = await fixture.HumanHrGet();
        Check(bypass.StatusCode == HttpStatusCode.Forbidden && fixture.Snapshot() == before, "HR route on human listener denies TLS bypass");
    }
    else
    {
        using var response = await fixture.Chart(fixture.Token(scope: SyntheticHrHost.ReadScope));
        Check(response.StatusCode == HttpStatusCode.Unauthorized && fixture.Snapshot() == before, mode + " HR service authentication fails closed");
    }
}
foreach (var setting in new[] { "missing-listener", "http-listener", "invalid-pin" })
{
    var builder = WebApplication.CreateBuilder(); builder.Logging.ClearProviders();
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Organization:HrApi:Enabled"] = "true", ["Organization:HrApi:Authority"] = "https://synthetic.invalid/realms/fanasa",
        ["Organization:HrApi:Audience"] = "synthetic", ["Organization:HrApi:ClientId"] = "synthetic",
        ["Organization:HrApi:ServiceSubject"] = Guid.NewGuid().ToString("D"), ["Organization:HrApi:ReadScope"] = "synthetic.read",
        ["Organization:HrApi:WriteScope"] = "synthetic.write", ["Organization:HrApi:GatewayCertificateSha256"] = setting == "invalid-pin" ? "invalid" : new string('0', 64),
        ["Kestrel:Endpoints:OrganizationHr:Url"] = setting == "missing-listener" ? null : "http://127.0.0.1:0"
    });
    try { builder.ConfigureOrganizationHrUpstream(); throw new Exception("Invalid upstream accepted"); }
    catch (InvalidOperationException) { Check(true, setting + " rejects enabled upstream before startup"); }
}

await using (var metered = await SyntheticHrHost.Start(metering: true))
{
    var before = metered.Snapshot();
    using var response = await metered.Post(metered.Token(), metered.Command());
    Check(response.StatusCode == HttpStatusCode.Forbidden && metered.Snapshot() == before,
        "enabled measured unit.save profile cannot be bypassed through HR command route");
}
Console.WriteLine($"{checks} synthetic HR TLS/JWT/HTTP checks passed.");

// Only Store is delayed; the production authorization boundary reads the real durable adapter.
sealed class SyntheticStoreBarrier(IAccessManagement inner, FabricDatabase database) : IAccessManagement, IDisposable
{
    private readonly ManualResetEventSlim release = new(false);
    private TaskCompletionSource<DateTimeOffset> entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int armed;
    public Task<DateTimeOffset> Entered => entered.Task;
    public bool EnteredInTransaction { get; private set; }
    public DateTimeOffset ReleasedAt { get; private set; }
    public void Arm()
    {
        release.Reset(); entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        EnteredInTransaction = false; ReleasedAt = default; Volatile.Write(ref armed, 1);
    }
    public void Release() => release.Set();
    public IReadOnlyCollection<Tenant> GetTenants()
    {
        if (Interlocked.Exchange(ref armed, 0) == 1)
        {
            EnteredInTransaction = database.IsInTransaction; entered.TrySetResult(DateTimeOffset.UtcNow);
            // Keep the synchronous ThreadLocal transaction on the same thread.
            if (!release.Wait(TimeSpan.FromSeconds(12))) throw new TimeoutException("Synthetic Store barrier timed out.");
            ReleasedAt = DateTimeOffset.UtcNow;
        }
        return inner.GetTenants();
    }
    public Tenant CreateTenant(string key, string displayName) => inner.CreateTenant(key, displayName);
    public IReadOnlyCollection<Product> GetProducts(string? centerSlug = null) => inner.GetProducts(centerSlug);
    public Product RegisterProduct(RegisterProductRequest request) => inner.RegisterProduct(request);
    public IReadOnlyCollection<ProductClient> GetClients(Guid productId) => inner.GetClients(productId);
    public ProductClient RegisterClient(Guid productId, RegisterClientRequest request) => inner.RegisterClient(productId, request);
    public IReadOnlyCollection<ContractEntitlement> GetEntitlements(Guid tenantId) => inner.GetEntitlements(tenantId);
    public ContractEntitlement ApplyContractEvent(ContractLifecycleEvent message) => inner.ApplyContractEvent(message);
    public UsageEvent RecordUsage(RecordUsageCommand command) => inner.RecordUsage(command);
    public IReadOnlyCollection<UsageSummary> GetUsage(Guid tenantId, DateTimeOffset from, DateTimeOffset to) => inner.GetUsage(tenantId, from, to);
    public PricingPlan RegisterPlan(RegisterPlanRequest request) => inner.RegisterPlan(request);
    public IReadOnlyCollection<PricingPlan> GetPlans(string? productKey = null) => inner.GetPlans(productKey);
    public PricingRule AddPricingRule(Guid planId, AddPricingRuleRequest request) => inner.AddPricingRule(planId, request);
    public AccountBalance GetBalance(Guid tenantId, string accountType = "consumer") => inner.GetBalance(tenantId, accountType);
    public UsageCharge CalculateCharge(Guid tenantId, string productKey, string metric, decimal quantity, string idempotencyKey) => inner.CalculateCharge(tenantId, productKey, metric, quantity, idempotencyKey);
    public RevenueSnapshot GetRevenue(string productKey, DateTimeOffset from, DateTimeOffset to) => inner.GetRevenue(productKey, from, to);
    public TenantSubscription Subscribe(CreateSubscriptionRequest request) => inner.Subscribe(request);
    public TenantUser AddUser(AddTenantUserRequest request) => inner.AddUser(request);
    public IReadOnlyCollection<TenantSubscription> GetSubscriptions(Guid tenantId) => inner.GetSubscriptions(tenantId);
    public IReadOnlyCollection<TenantUser> GetUsers(Guid tenantId) => inner.GetUsers(tenantId);
    public AccessRole CreateRole(CreateRoleRequest request) => inner.CreateRole(request);
    public IReadOnlyCollection<AccessRole> GetRoles(Guid tenantId, string? productKey = null) => inner.GetRoles(tenantId, productKey);
    public void Dispose() => release.Dispose();
}

sealed class SyntheticHrHost : IAsyncDisposable
{
    public const string ReadScope = "synthetic.organization.hr.read";
    public const string WriteScope = "synthetic.organization.hr.write";
    private const string Issuer = "https://synthetic-idp.invalid/realms/fanasa";
    private const string Audience = "synthetic-organization-hr-api";
    private const string ClientId = "synthetic-hr-service";
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string directory;
    private readonly WebApplication app;
    private readonly HttpClient gatewayClient;
    private readonly HttpClient directClient;
    private readonly HttpClient otherClient;
    private readonly RSA rsa = RSA.Create(2048);
    private readonly RsaSecurityKey key;
    public X509Certificate2 GatewayCertificate { get; }
    public X509Certificate2 OtherCertificate { get; }
    public X509Certificate2 ServerCertificate { get; }
    public string DataDirectory => directory;
    public string DatabasePath => Path.Combine(directory, "fabric.db");
    public FabricDatabase Database { get; }
    public PersistentAccessManagement Access { get; }
    public OrganizationStore Store { get; }
    public SyntheticStoreBarrier StoreBarrier { get; }
    private Uri? humanAddress;
    public Guid Tenant { get; }
    public Guid OtherTenant { get; }
    public string ServiceSubject { get; } = Guid.NewGuid().ToString("D");
    public string HumanSubject { get; } = Guid.NewGuid().ToString("D");
    public TenantUser ServiceMember { get; }
    public ProductClient RegisteredClient { get; }
    public OrganizationHrOptions Options { get; }

    private SyntheticHrHost(bool metering, string mode)
    {
        directory = Path.Combine(Path.GetTempPath(), "synthetic-organization-hr-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        GatewayCertificate = Certificate("synthetic-gateway", true); OtherCertificate = Certificate("synthetic-other-gateway", true);
        ServerCertificate = Certificate("localhost", false);
        var pfx = Path.Combine(directory, "server.pfx"); File.WriteAllBytes(pfx, ServerCertificate.Export(X509ContentType.Pfx));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Logging.ClearProviders();
        var values = new Dictionary<string, string?>
        {
            ["Platform:RegistryPath"] = Path.Combine(directory, "registry.db"),
            ["Organization:HrApi:Enabled"] = "true", ["Organization:HrApi:Authority"] = Issuer,
            ["Organization:HrApi:Audience"] = Audience, ["Organization:HrApi:ClientId"] = ClientId,
            ["Organization:HrApi:ServiceSubject"] = ServiceSubject, ["Organization:HrApi:ReadScope"] = ReadScope,
            ["Organization:HrApi:WriteScope"] = WriteScope,
            ["Organization:HrApi:GatewayCertificateSha256"] = Convert.ToHexString(SHA256.HashData(GatewayCertificate.RawData)),
            ["Kestrel:Endpoints:OrganizationHr:Url"] = "https://127.0.0.1:0",
            ["Kestrel:Endpoints:OrganizationHr:Certificate:Path"] = pfx,
            ["UsageMetering:Organization:Enabled"] = metering ? "true" : "false"
        };
        if (mode == "disabled") values["Organization:HrApi:Enabled"] = "false";
        if (mode == "misconfigured") values["Organization:HrApi:Audience"] = null;
        if (mode == "dual-listener") values["Kestrel:Endpoints:Human:Url"] = "http://127.0.0.1:0";
        builder.Configuration.AddInMemoryCollection(values);
        if (mode == "misconfigured") builder.WebHost.ConfigureKestrel((context, options) => options.Configure(context.Configuration.GetSection("Kestrel")));
        else builder.ConfigureOrganizationHrUpstream();
        var registry = new PlatformRegistry(builder.Configuration);
        Database = new FabricDatabase(DatabasePath);
        Access = new PersistentAccessManagement(Database, new InMemoryAccessManagement(registry));
        var owner = Access.GetTenants().Single().Id;
        Tenant = Access.CreateTenant("synthetic-hr", "Synthetic HR").Id;
        OtherTenant = Access.CreateTenant("synthetic-other", "Synthetic Other").Id;
        registry.SetTenantSource(Access.GetTenants); registry.SetMembershipSource(Access.GetUsers);
        var product = registry.Register(new(Guid.NewGuid(), "organization-fabric", "Synthetic HR", Audience, "fanasa.ayin",
            null, null, owner, "synthetic-owner"), "synthetic-test");
        RegisteredClient = Access.RegisterClient(product.Id, new(ClientId, "Synthetic HR service", "confidential", [], [ReadScope, WriteScope]));
        ServiceMember = Access.AddUser(new(Tenant, ServiceSubject, "Synthetic HR service"));
        Access.AddUser(new(Tenant, HumanSubject, "Synthetic human")); RestoreGrants();
        var plan = Access.RegisterPlan(new("organization-fabric", "synthetic-hr", "Synthetic HR subscription", "postpaid", "IRR", 0, 0));
        Access.Subscribe(new(Tenant, "organization-fabric", plan.Id, 10));
        if (metering)
        {
            const string prefix = "UsageMetering:Organization:Bindings:0:";
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["UsageMetering:Organization:Environment"] = "development",
                ["UsageMetering:Organization:ProducerId"] = "synthetic-hr-backend",
                ["UsageMetering:Organization:DeploymentId"] = "synthetic-hr-test",
                [prefix + "ConsumerTenantId"] = Tenant.ToString(), [prefix + "ContractId"] = "synthetic-contract",
                [prefix + "ContractVersion"] = "1", [prefix + "AllocationId"] = "synthetic-allocation",
                [prefix + "AllocationPeriodId"] = "synthetic-period", [prefix + "PlanKey"] = "synthetic-hr",
                [prefix + "PricingReference"] = "synthetic-rate", [prefix + "RateSnapshot:RateId"] = "synthetic-rate",
                [prefix + "RateSnapshot:RateVersion"] = "1",
                [prefix + "RateSnapshot:EffectiveFrom"] = DateTimeOffset.UtcNow.AddHours(-1).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'"),
                [prefix + "RateSnapshot:EffectiveTo"] = DateTimeOffset.UtcNow.AddHours(1).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'"),
                [prefix + "RateSnapshot:UnitPrice"] = "12", [prefix + "RateSnapshot:Currency"] = "IRR",
                [prefix + "RateSnapshot:MinorUnitScale"] = "0", [prefix + "RateSnapshot:Rounding"] = "away-from-zero",
                [prefix + "RateSnapshot:BillingMode"] = "postpaid"
            });
        }
        var usage = new OrganizationUsageMetering(builder.Configuration, Access, registry);
        StoreBarrier = new SyntheticStoreBarrier(Access, Database);
        Store = new OrganizationStore(directory, StoreBarrier, true, Database);
        builder.Services.AddSingleton(registry); builder.Services.AddSingleton(Database); builder.Services.AddSingleton(Access);
        builder.Services.AddSingleton(usage); builder.Services.AddSingleton(Store);
        builder.Services.AddControllers().AddApplicationPart(typeof(OrganizationHrApi).Assembly);
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options => options.Cookie.Name = "synthetic-hr-cookie");
        builder.Services.AddOrganizationHrSecurity(builder.Configuration);
        Options = builder.Configuration.GetSection("Organization:HrApi").Get<OrganizationHrOptions>()!;
        key = new RsaSecurityKey(rsa) { KeyId = "synthetic-hr-key" };
        builder.Services.PostConfigure<JwtBearerOptions>(OrganizationHrSecurity.Scheme, options =>
        {
            // Offline synthetic signer still executes production signature/issuer/audience/lifetime validation.
            options.Authority = null; options.MetadataAddress = "";
            var metadata = new OpenIdConnectConfiguration { Issuer = Issuer }; metadata.SigningKeys.Add(key);
            options.Configuration = metadata; options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata);
        });
        app = builder.Build(); app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.MapControllers().RequireAuthorization();
        app.MapGet("/synthetic-cookie", async (HttpContext context) =>
        {
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(new ClaimsIdentity([new("sub", HumanSubject)], CookieAuthenticationDefaults.AuthenticationScheme)));
            return Results.Ok();
        }).AllowAnonymous();
        app.MapGet("/synthetic-human", () => Results.Ok()).AllowAnonymous();
        gatewayClient = Client(GatewayCertificate); directClient = Client(null); otherClient = Client(OtherCertificate);
    }
    private HttpClient Client(X509Certificate2? certificate)
    {
        var serverPin = SHA256.HashData(ServerCertificate.RawData);
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true,
            ServerCertificateCustomValidationCallback = (_, peer, _, _) => peer is not null
                && CryptographicOperations.FixedTimeEquals(SHA256.HashData(peer.RawData), serverPin) };
        if (certificate is not null) handler.ClientCertificates.Add(certificate);
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }
    public static async Task<SyntheticHrHost> Start(bool metering = false, string mode = "configured")
    {
        var fixture = new SyntheticHrHost(metering, mode); await fixture.app.StartAsync();
        var addresses = fixture.app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        var url = addresses.Single(x => x.StartsWith("https://", StringComparison.Ordinal));
        var humanUrl = addresses.SingleOrDefault(x => x.StartsWith("http://", StringComparison.Ordinal));
        if (humanUrl is not null) fixture.humanAddress = new Uri(humanUrl);
        fixture.gatewayClient.BaseAddress = new Uri(url); fixture.directClient.BaseAddress = new Uri(url); fixture.otherClient.BaseAddress = new Uri(url);
        return fixture;
    }
    public Task<HttpResponseMessage> HumanGet() => directClient.GetAsync(new Uri(humanAddress!, "/synthetic-human"));
    public async Task<HttpResponseMessage> HumanHrGet()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(humanAddress!, $"/api/organization-hr/v1/tenants/{Tenant}/chart"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token(scope: ReadScope));
        return await directClient.SendAsync(request);
    }
    public void RestoreGrants()
    {
        foreach (var permission in new[] { "organization.read", "organization.write" })
            Access.SetGrant(new(Tenant, ServiceSubject, permission, null), "synthetic-test", "synthetic service grant");
    }
    public void PutClient(ProductClient client) => Database.Transaction(() => { Database.Put("client", client.Id.ToString(), client); return true; });
    public OrgCommand Command(string operation = "unit.save") => new(Database.Read<OrgState>("organization", Tenant.ToString())?.Revision ?? 0, operation, null,
        "Synthetic team", "synthetic-team", null, "team", null, 1, null, null, null, null,
        "synthetic HR command", RequestId: Guid.NewGuid().ToString("D"));
    public string Token(string? issuer = null, string? audience = null, string? subject = null, string? client = null,
        string? scope = null, bool badKey = false, bool expired = false, Claim[]? extra = null, DateTime? expiresAt = null)
    {
        var claims = new List<Claim> { new("sub", subject ?? ServiceSubject), new("azp", client ?? ClientId), new("scope", scope ?? WriteScope) };
        if (extra is not null) claims.AddRange(extra);
        using var other = badKey ? RSA.Create(2048) : null;
        var signer = badKey ? new RsaSecurityKey(other!) { KeyId = key.KeyId } : key;
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(issuer ?? Issuer, audience ?? Audience, claims,
            DateTime.UtcNow.AddMinutes(-10), expired ? DateTime.UtcNow.AddMinutes(-1) : expiresAt ?? DateTime.UtcNow.AddMinutes(5),
            new SigningCredentials(signer, SecurityAlgorithms.RsaSha256)));
    }
    public string TimingToken(string mode)
    {
        var expiry = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds();
        var payload = new JwtPayload { ["iss"] = Issuer, ["aud"] = Audience, ["sub"] = ServiceSubject, ["azp"] = ClientId, ["scope"] = WriteScope };
        if (mode != "missing-exp") payload["exp"] = mode switch
        {
            "array-exp" => new[] { expiry, expiry }, "noncanonical-exp" => "0" + expiry,
            _ => (object)expiry
        };
        if (mode == "future-nbf") payload["nbf"] = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds();
        if (mode == "array-sub") payload["sub"] = new[] { ServiceSubject, ServiceSubject };
        if (mode == "missing-azp") payload.Remove("azp");
        if (mode == "array-azp") payload["azp"] = new[] { ClientId, ClientId };
        if (mode == "array-client_id") payload["client_id"] = new[] { ClientId, ClientId };
        if (mode == "duplicate-property")
        {
            var header = Base64UrlEncoder.Encode(JsonSerializer.Serialize(new { alg = "RS256", typ = "JWT", kid = key.KeyId }));
            var raw = JsonSerializer.Serialize(payload); raw = raw[..^1] + ",\"azp\":\"" + ClientId + "\"}";
            var signingInput = header + "." + Base64UrlEncoder.Encode(raw);
            var signature = rsa.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            return signingInput + "." + Base64UrlEncoder.Encode(signature);
        }
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(new JwtHeader(new SigningCredentials(key, SecurityAlgorithms.RsaSha256)), payload));
    }
    public async Task IssueCookie() { using var response = await gatewayClient.GetAsync("/synthetic-cookie"); response.EnsureSuccessStatusCode(); }
    public Task<HttpResponseMessage> Chart(string token, long? revision = null, bool otherCertificate = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"/api/organization-hr/v1/tenants/{Tenant}/" + (revision.HasValue ? "history/" + revision.Value : "chart"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (otherCertificate ? otherClient : gatewayClient).SendAsync(request);
    }
    public Task<HttpResponseMessage> GatewayAccess(string token, string operation, bool gateway)
    {
        var request = new HttpRequestMessage(HttpMethod.Get,
            $"/api/organization-hr/v1/tenants/{Tenant}/gateway-access?operation={Uri.EscapeDataString(operation)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (gateway ? gatewayClient : directClient).SendAsync(request);
    }
    public async Task<HttpResponseMessage> Post(string? token, OrgCommand? command = null, Guid? tenant = null, bool gateway = true,
        string? raw = null, bool idempotency = true, string contentType = "application/json", bool forged = false, bool encoded = false, string[]? idempotencyKeys = null)
    {
        command ??= Command();
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/organization-hr/v1/tenants/{tenant ?? Tenant}/commands")
        { Content = new StringContent(raw ?? JsonSerializer.Serialize(command, JsonOptions), Encoding.UTF8) };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (idempotencyKeys is not null) request.Headers.TryAddWithoutValidation("Idempotency-Key", idempotencyKeys);
        else if (idempotency) request.Headers.Add("Idempotency-Key", command.RequestId);
        if (encoded) request.Content.Headers.ContentEncoding.Add("identity");
        if (forged) { request.Headers.Add("X-Client-Cert", Convert.ToBase64String(GatewayCertificate.RawData)); request.Headers.Add("X-Gateway-Admission", "Permit"); request.Headers.Add("X-Permission", "organization.write"); }
        return await (gateway ? gatewayClient : directClient).SendAsync(request);
    }
    public async Task<OrgState> Execute(OrgCommand command)
    {
        using var response = await Post(Token(), command);
        response.EnsureSuccessStatusCode(); return await response.Content.ReadFromJsonAsync<OrgState>() ?? throw new Exception("Missing HR command response");
    }
    public string Snapshot() => JsonSerializer.Serialize(new
    {
        Organization = Database.All<OrgState>("organization"), History = Database.All<OrgState>("organization.history"),
        Receipts = Database.All<OrgReceipt>("organization.receipt"), Outbox = Database.Outbox(take: 100)
    });
    private static X509Certificate2 Certificate(string name, bool client)
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=" + name, key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection
            { new(client ? "1.3.6.1.5.5.7.3.2" : "1.3.6.1.5.5.7.3.1") }, true));
        if (!client)
        { var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("localhost"); san.AddIpAddress(IPAddress.Loopback); request.CertificateExtensions.Add(san.Build()); }
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddHours(1));
        // Schannel client authentication requires a user-key handle on Windows.
        // Without PersistKeySet the temporary synthetic key is removed on certificate disposal.
        return X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null,
            X509KeyStorageFlags.UserKeySet | X509KeyStorageFlags.Exportable);
    }
    public async ValueTask DisposeAsync()
    {
        gatewayClient.Dispose(); directClient.Dispose(); otherClient.Dispose(); await app.StopAsync(); await app.DisposeAsync();
        StoreBarrier.Dispose();
        Database.Dispose(); rsa.Dispose(); GatewayCertificate.Dispose(); OtherCertificate.Dispose(); ServerCertificate.Dispose();
        SqliteConnection.ClearAllPools();
        var full = Path.GetFullPath(directory); var temp = Path.GetFullPath(Path.GetTempPath());
        if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("synthetic-organization-hr-", StringComparison.Ordinal))
            throw new InvalidOperationException("Synthetic cleanup escaped the temporary directory.");
        Directory.Delete(full, true);
    }
}
