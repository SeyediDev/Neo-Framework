using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Fanasa.AccessManagement.Web.Accounting;
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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

// Every identity, registration, admission, contract and rate in this program is synthetic.
// Real HTTP/Kestrel and the production JWT/authorization extension are used without TestHost.
var checks = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new Exception("FAILED: " + label);
    checks++;
    Console.WriteLine("PASS: " + label);
}

await using (var fixture = await SyntheticHost.Start())
{
    async Task Denied(string label, HttpStatusCode expected, string? token = null, Guid? tenant = null,
        OrgCommand? command = null, bool cookie = false, bool forgedHeaders = false)
    {
        var before = fixture.Snapshot();
        using var response = await fixture.Post(token, tenant, command, cookie, forgedHeaders);
        Check(response.StatusCode == expected, label + " HTTP status (actual " + (int)response.StatusCode + ")");
        Check(fixture.Snapshot() == before, label + " leaves organization/history/receipts/usage/ledger/outbox unchanged");
    }
    await Denied("anonymous", HttpStatusCode.Unauthorized);
    await Denied("valid cookie alone", HttpStatusCode.Unauthorized, cookie: true);
    await Denied("invalid signature", HttpStatusCode.Unauthorized, fixture.Token(badKey: true));
    await Denied("wrong issuer", HttpStatusCode.Unauthorized, fixture.Token(issuer: "https://forged.invalid/realms/fanasa"));
    await Denied("wrong audience", HttpStatusCode.Unauthorized, fixture.Token(audience: "other-api"));
    await Denied("expired JWT", HttpStatusCode.Unauthorized, fixture.Token(expired: true));
    foreach (var timing in new[] { "missing-exp", "duplicate-exp", "noncanonical-exp", "future-nbf" })
    {
        var before = fixture.Snapshot();
        using var response = await fixture.Post(fixture.TimingToken(timing));
        Check(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, timing + " signed JWT is denied");
        Check(fixture.Snapshot() == before, timing + " leaves all business/financial records unchanged");
    }
    await Denied("inactive JWT", HttpStatusCode.Forbidden, fixture.Token(inactive: true));
    await Denied("unauthorized client", HttpStatusCode.Forbidden, fixture.Token(client: "other-client"));
    await Denied("missing scope", HttpStatusCode.Forbidden, fixture.Token(scope: "openid"));
    await Denied("conflicting client_id", HttpStatusCode.Forbidden, fixture.Token(extra: [new("client_id", "other-client")]));
    await Denied("tenant A actor targeting B", HttpStatusCode.Forbidden, fixture.Token(), fixture.OtherTenant);
    await Denied("forged tenant/permission claims and headers without durable grant", HttpStatusCode.Forbidden,
        fixture.Token(actor: fixture.UngrantedActor, extra: [new("permission", "organization.write"), new("tenant_permission", fixture.Tenant + ":organization.write"), new("tenant_id", fixture.Tenant.ToString())]), forgedHeaders: true);
    await Denied("unsupported operation", HttpStatusCode.BadRequest, fixture.Token(), command: fixture.Command() with { Operation = "role.save" });
    await Denied("missing request identity", HttpStatusCode.BadRequest, fixture.Token(), command: fixture.Command() with { RequestId = null });

    fixture.Access.SetGrant(new(fixture.Tenant, fixture.Actor, "organization.write", DateTimeOffset.UtcNow.AddMinutes(-1)), "synthetic-test", "synthetic revocation");
    await Denied("revoked durable grant", HttpStatusCode.Forbidden, fixture.Token());
    fixture.RestoreGrant();
    fixture.Access.SetMembership(fixture.Tenant, fixture.Member.Id, false, "synthetic-test", "synthetic offboard");
    await Denied("inactive membership", HttpStatusCode.Forbidden, fixture.Token());
    fixture.Access.SetMembership(fixture.Tenant, fixture.Member.Id, true, "synthetic-test", "synthetic restore"); fixture.RestoreGrant();
    var tenantRecord = fixture.Access.GetTenants().Single(x => x.Id == fixture.Tenant);
    fixture.Database.Transaction(() => { fixture.Database.Put("tenant", fixture.Tenant.ToString(), tenantRecord with { IsActive = false }); return true; });
    await Denied("inactive tenant", HttpStatusCode.Forbidden, fixture.Token());
    fixture.Database.Transaction(() => { fixture.Database.Put("tenant", fixture.Tenant.ToString(), tenantRecord); return true; });
    fixture.Database.Transaction(() => { fixture.Database.Put("client", fixture.RegisteredClient.Id.ToString(), fixture.RegisteredClient with { IsActive = false }); return true; });
    await Denied("inactive registered client", HttpStatusCode.Forbidden, fixture.Token());
    fixture.Database.Transaction(() => { fixture.Database.Put("client", fixture.RegisteredClient.Id.ToString(), fixture.RegisteredClient with { AllowedScopes = [] }); return true; });
    await Denied("scope removed from registered client", HttpStatusCode.Forbidden, fixture.Token());
    fixture.Database.Transaction(() => { fixture.Database.Put("client", fixture.RegisteredClient.Id.ToString(), fixture.RegisteredClient); return true; });
    var subscription = fixture.Access.GetSubscriptions(fixture.Tenant).Single();
    fixture.Access.SetSubscription(fixture.Tenant, subscription.Id, "suspended", null, "synthetic-test", "synthetic suspension");
    await Denied("suspended product subscription", HttpStatusCode.Forbidden, fixture.Token());
    fixture.Access.SetSubscription(fixture.Tenant, subscription.Id, "active", DateTimeOffset.UtcNow.AddMonths(1), "synthetic-test", "synthetic renewal");
    fixture.Access.ApplyContractEvent(new("synthetic-expired", fixture.Tenant, "synthetic-contract", "organization-fabric", "fanasa.ayin",
        "synthetic-post", "ContractExpired", null, "change", DateTimeOffset.UtcNow));
    await Denied("expired product entitlement", HttpStatusCode.Forbidden, fixture.Token());
    fixture.Access.ApplyContractEvent(new("synthetic-renewed", fixture.Tenant, "synthetic-contract", "organization-fabric", "fanasa.ayin",
        "synthetic-post", "ContractRenewed", null, "change", DateTimeOffset.UtcNow));
    fixture.Admission.Allow = false;
    await Denied("trusted synthetic admission denial", HttpStatusCode.Forbidden, fixture.Token(), forgedHeaders: true);
    fixture.Admission.Allow = true;
    fixture.Admission.AfterAdmit = fixture.RestoreThenRevoke;
    await Denied("grant revoked during admission", HttpStatusCode.Forbidden, fixture.Token());
    fixture.Admission.AfterAdmit = null; fixture.RestoreGrant();
    fixture.Admission.WaitPastTokenExpiry = true;
    await Denied("token expires while awaiting synthetic admission", HttpStatusCode.Forbidden,
        fixture.Token(expiresAt: DateTime.UtcNow.AddSeconds(5)));
    Check(fixture.Admission.ObservedLiveTokenBeforeWait && fixture.Admission.ObservedExpiredTokenAfterWait,
        "expiry test enters admission with a live JWT and returns after its signed deadline");
    fixture.Admission.WaitPastTokenExpiry = false;

    var request = fixture.Command();
    using var first = await fixture.Post(fixture.Token(), command: request);
    Check(first.StatusCode == HttpStatusCode.OK, "trusted synthetic admission and durable grant execute HTTP unit.save");
    var firstState = await first.Content.ReadFromJsonAsync<OrgState>() ?? throw new Exception("Missing result");
    Check(firstState.Revision == 1 && firstState.Units.Length == 1, "one real organization mutation commits");
    Check(fixture.Billing.Read(fixture.Tenant).Outstanding == 12
        && fixture.Database.All<MeasuredUsageOperation>("measured.operation").Length == 1
        && fixture.Database.Outbox(take: 100).Count(x => x.Kind == "UsageCommitted") == 1,
        "synthetic configured rate produces one transactional usage/debit/outbox");
    var committed = fixture.Snapshot();
    using var repeat = await fixture.Post(fixture.Token(), command: request);
    Check(repeat.StatusCode == HttpStatusCode.OK && (await repeat.Content.ReadFromJsonAsync<OrgState>())?.Revision == firstState.Revision
        && fixture.Snapshot() == committed, "exact repeated HTTP receipt executes and charges once");
    await Denied("changed payload under same receipt", HttpStatusCode.Conflict, fixture.Token(), command: request with { Name = "changed" });
    fixture.Access.SetGrant(new(fixture.Tenant, fixture.Actor, "organization.write", DateTimeOffset.UtcNow.AddMinutes(-1)), "synthetic-test", "synthetic receipt revocation");
    await Denied("revoked grant blocks exact receipt replay", HttpStatusCode.Forbidden, fixture.Token(), command: request);
}

foreach (var mode in new[] { "default-disabled", "no-admission", "metering-disabled", "no-bearer-settings" })
{
    await using var fixture = await SyntheticHost.Start(mode);
    var before = fixture.Snapshot();
    using var response = await fixture.Post(fixture.Token(), forgedHeaders: true);
    Check(response.StatusCode == (mode == "no-bearer-settings" ? HttpStatusCode.Unauthorized : HttpStatusCode.Forbidden), mode + " fails closed over HTTP");
    Check(fixture.Snapshot() == before, mode + " leaves all business/financial records unchanged");
    using var anonymous = await fixture.Post(null);
    Check(anonymous.StatusCode == HttpStatusCode.Unauthorized && fixture.Snapshot() == before,
        mode + " anonymous request rejects without a 500 or mutation");
}
Console.WriteLine($"{checks} synthetic Bearer HTTP checks passed.");

sealed class SyntheticAdmission : IOrganizationUnitSaveAdmission
{
    public bool IsConfigured => true;
    public bool Allow { get; set; } = true;
    public Action? AfterAdmit { get; set; }
    public bool WaitPastTokenExpiry { get; set; }
    public bool ObservedLiveTokenBeforeWait { get; private set; }
    public bool ObservedExpiredTokenAfterWait { get; private set; }
    public async ValueTask<bool> AdmitAsync(Guid tenant, string actor, OrgCommand command, ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        // Explicitly synthetic server dependency. This is not Gateway evidence or a production allow adapter.
        if (WaitPastTokenExpiry)
        {
            var expiry = DateTimeOffset.FromUnixTimeSeconds(long.Parse(principal.FindFirst("exp")!.Value, CultureInfo.InvariantCulture));
            ObservedLiveTokenBeforeWait = DateTimeOffset.UtcNow < expiry;
            var delay = expiry - DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(100);
            if (delay > TimeSpan.Zero) await Task.Delay(delay, cancellationToken);
            ObservedExpiredTokenAfterWait = DateTimeOffset.UtcNow >= expiry;
        }
        AfterAdmit?.Invoke();
        return Allow;
    }
}

sealed class SyntheticHost : IAsyncDisposable
{
    private const string Issuer = "https://synthetic-idp.invalid/realms/fanasa";
    private const string Audience = "synthetic-organization-api";
    private const string ClientId = "synthetic-organization-client";
    private const string Scope = "synthetic.organization.unit.save";
    private readonly string directory;
    private readonly WebApplication app;
    private readonly HttpClient client;
    private readonly RsaSecurityKey key;
    private readonly RSA rsa;
    public FabricDatabase Database { get; }
    public PersistentAccessManagement Access { get; }
    public AccountingStore Billing { get; }
    public Guid Tenant { get; }
    public Guid OtherTenant { get; }
    public string Actor { get; } = Guid.NewGuid().ToString("D");
    public string UngrantedActor { get; } = Guid.NewGuid().ToString("D");
    public TenantUser Member { get; }
    public ProductClient RegisteredClient { get; }
    public SyntheticAdmission Admission { get; } = new();

    private SyntheticHost(string mode)
    {
        directory = Path.Combine(Path.GetTempPath(), "synthetic-organization-bearer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { ["Platform:RegistryPath"] = Path.Combine(directory, "registry.db") });
        var registry = new PlatformRegistry(builder.Configuration);
        Database = new FabricDatabase(Path.Combine(directory, "fabric.db"));
        Access = new PersistentAccessManagement(Database, new InMemoryAccessManagement(registry));
        var owner = Access.GetTenants().Single().Id;
        Tenant = Access.CreateTenant("synthetic-a", "Synthetic A").Id;
        OtherTenant = Access.CreateTenant("synthetic-b", "Synthetic B").Id;
        registry.SetTenantSource(Access.GetTenants); registry.SetMembershipSource(Access.GetUsers);
        var product = registry.Register(new(Guid.NewGuid(), "organization-fabric", "Synthetic Organization", Audience, "fanasa.ayin", null, null, owner, "synthetic-owner"), "synthetic-test");
        RegisteredClient = Access.RegisterClient(product.Id, new(ClientId, "Synthetic client", "public", [], [Scope]));
        Member = Access.AddUser(new(Tenant, Actor, "Synthetic member"));
        Access.AddUser(new(Tenant, UngrantedActor, "Synthetic member without grant")); RestoreGrant();
        var plan = Access.RegisterPlan(new("organization-fabric", "synthetic-post", "Synthetic plan", "postpaid", "IRR", 0, 0));
        Access.Subscribe(new(Tenant, "organization-fabric", plan.Id, 10));
        Access.ApplyContractEvent(new("synthetic-event", Tenant, "synthetic-contract", "organization-fabric", "fanasa.ayin", "synthetic-post", "ContractActivated", null, "change", DateTimeOffset.UtcNow.AddMinutes(-1)));
        Billing = new AccountingStore(directory, Access, database: Database);
        Billing.Execute(Tenant, new(0, "configure", "synthetic-credit", "postpaid", "IRR", 1000, null, null, 0, 0, 0, "synthetic credit", null, null), "synthetic-test");
        var values = new Dictionary<string, string?>
        {
            ["Organization:Bearer:Enabled"] = mode == "default-disabled" ? "false" : "true",
            ["Organization:Bearer:Authority"] = Issuer, ["Organization:Bearer:Audience"] = Audience,
            ["Organization:Bearer:ClientId"] = ClientId, ["Organization:Bearer:Scope"] = Scope,
            ["UsageMetering:Organization:Enabled"] = mode == "metering-disabled" ? "false" : "true",
            ["UsageMetering:Organization:Environment"] = "development", ["UsageMetering:Organization:ProducerId"] = "synthetic-backend",
            ["UsageMetering:Organization:DeploymentId"] = "synthetic-kestrel"
        };
        if (mode == "default-disabled") values.Remove("Organization:Bearer:Enabled");
        if (mode == "no-bearer-settings")
            foreach (var name in values.Keys.Where(x => x.StartsWith("Organization:Bearer:", StringComparison.Ordinal)).ToArray()) values.Remove(name);
        const string prefix = "UsageMetering:Organization:Bindings:0:";
        values[prefix + "ConsumerTenantId"] = Tenant.ToString(); values[prefix + "ContractId"] = "synthetic-contract";
        values[prefix + "ContractVersion"] = "1"; values[prefix + "AllocationId"] = "synthetic-allocation";
        values[prefix + "AllocationPeriodId"] = "synthetic-period"; values[prefix + "PlanKey"] = "synthetic-post";
        values[prefix + "PricingReference"] = "synthetic-rate";
        values[prefix + "RateSnapshot:RateId"] = "synthetic-rate"; values[prefix + "RateSnapshot:RateVersion"] = "1";
        values[prefix + "RateSnapshot:EffectiveFrom"] = Utc(DateTimeOffset.UtcNow.AddHours(-1));
        values[prefix + "RateSnapshot:EffectiveTo"] = Utc(DateTimeOffset.UtcNow.AddHours(1));
        values[prefix + "RateSnapshot:UnitPrice"] = "12"; values[prefix + "RateSnapshot:Currency"] = "IRR";
        values[prefix + "RateSnapshot:MinorUnitScale"] = "0"; values[prefix + "RateSnapshot:Rounding"] = "away-from-zero";
        values[prefix + "RateSnapshot:BillingMode"] = "postpaid";
        builder.Configuration.AddInMemoryCollection(values);
        var metering = new OrganizationUsageMetering(builder.Configuration, Access, registry);
        var measured = new MeasuredUsageStore(Database, Billing, metering);
        var store = new OrganizationStore(directory, Access, true, Database, metering: metering, measuredUsage: measured);
        builder.Services.AddSingleton(registry); builder.Services.AddSingleton(Database); builder.Services.AddSingleton(Access);
        builder.Services.AddSingleton(metering); builder.Services.AddSingleton(store);
        builder.Services.AddTransient<IClaimsTransformation, FabricClaimsTransformation>();
        builder.Services.AddControllers().AddApplicationPart(typeof(OrganizationOperationApi).Assembly);
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options => { options.Cookie.Name = "synthetic-session"; });
        if (mode != "no-admission") builder.Services.AddSingleton<IOrganizationUnitSaveAdmission>(Admission);
        builder.Services.AddOrganizationBearerSecurity(builder.Configuration);
        rsa = RSA.Create(2048); key = new RsaSecurityKey(rsa) { KeyId = "synthetic-test-key" };
        builder.Services.PostConfigure<JwtBearerOptions>(OrganizationBearerSecurity.Scheme, options =>
        {
            // Offline synthetic signer avoids network metadata while retaining production issuer/audience/lifetime checks.
            options.Authority = null; options.MetadataAddress = "";
            var metadata = new OpenIdConnectConfiguration { Issuer = Issuer }; metadata.SigningKeys.Add(key);
            options.Configuration = metadata;
            options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata);
        });
        app = builder.Build();
        app.UseRouting(); app.UseAuthentication(); app.UseAuthorization();
        app.MapControllers().RequireAuthorization();
        app.MapGet("/synthetic-cookie", async (Microsoft.AspNetCore.Http.HttpContext context) =>
        {
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(new ClaimsIdentity([new("sub", Actor)], CookieAuthenticationDefaults.AuthenticationScheme)));
            return Microsoft.AspNetCore.Http.Results.Ok();
        }).AllowAnonymous();
        client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true });
    }
    public static async Task<SyntheticHost> Start(string mode = "configured")
    {
        var fixture = new SyntheticHost(mode); await fixture.app.StartAsync();
        var address = fixture.app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        fixture.client.BaseAddress = new Uri(address); return fixture;
    }
    public void RestoreGrant() => Access.SetGrant(new(Tenant, Actor, "organization.write", null), "synthetic-test", "synthetic grant");
    public void RestoreThenRevoke() => Database.Transaction(() =>
    {
        // Synthetic concurrent state change avoids attributing its own tenancy outbox to the denied operation.
        Database.Put("tenant-grant", $"{Tenant}:{Actor}:organization.write",
            new TenantGrant(Tenant, Actor, "organization.write", DateTimeOffset.UtcNow.AddMinutes(-1)));
        return true;
    });
    public OrgCommand Command() => new(0, "unit.save", null, "Synthetic team", "synthetic-team", null, "team", null, 1,
        null, null, null, null, "synthetic HTTP operation", RequestId: Guid.NewGuid().ToString("D"));
    public string Token(string? issuer = null, string? audience = null, string? client = null, string? scope = null,
        string? actor = null, bool badKey = false, bool expired = false, bool inactive = false, Claim[]? extra = null, DateTime? expiresAt = null)
    {
        var claims = new List<Claim> { new("sub", actor ?? Actor), new("azp", client ?? ClientId), new("scope", scope ?? Scope) };
        if (inactive) claims.Add(new("active", "false")); if (extra is not null) claims.AddRange(extra);
        using var other = badKey ? RSA.Create(2048) : null;
        var signingKey = badKey ? new RsaSecurityKey(other!) { KeyId = key.KeyId } : key;
        var now = DateTime.UtcNow;
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(issuer ?? Issuer, audience ?? Audience, claims,
            now.AddMinutes(-10), expired ? now.AddMinutes(-1) : expiresAt ?? now.AddMinutes(5), new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256)));
    }
    public string TimingToken(string mode)
    {
        var expiry = DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeSeconds();
        var payload = new JwtPayload
        {
            ["iss"] = Issuer, ["aud"] = Audience, ["sub"] = Actor, ["azp"] = ClientId, ["scope"] = Scope
        };
        if (mode != "missing-exp") payload["exp"] = mode switch
        {
            "duplicate-exp" => new[] { expiry, expiry },
            "noncanonical-exp" => "0" + expiry.ToString(CultureInfo.InvariantCulture),
            _ => (object)expiry
        };
        if (mode == "future-nbf") payload["nbf"] = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds();
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(new JwtHeader(new SigningCredentials(key, SecurityAlgorithms.RsaSha256)), payload));
    }
    public async Task<HttpResponseMessage> Post(string? token, Guid? tenant = null, OrgCommand? command = null, bool cookie = false, bool forgedHeaders = false)
    {
        if (cookie) { using var issued = await client.GetAsync("/synthetic-cookie"); issued.EnsureSuccessStatusCode(); }
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/organization-operations/tenants/{tenant ?? Tenant}/unit.save")
        { Content = JsonContent.Create(command ?? Command()) };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (forgedHeaders)
        {
            request.Headers.Add("X-Tenant-ID", (tenant ?? Tenant).ToString()); request.Headers.Add("X-Permission", "organization.write");
            request.Headers.Add("X-Gateway-Admission", "forged-admission"); request.Headers.Add("X-Policy-Decision", "Permit");
        }
        return await client.SendAsync(request);
    }
    public string Snapshot() => JsonSerializer.Serialize(new
    {
        Organization = Database.All<OrgState>("organization"), History = Database.All<OrgState>("organization.history"),
        Receipts = Database.All<OrgReceipt>("organization.receipt"), Usage = Database.All<MeasuredUsageOperation>("measured.operation"),
        UsageReceipts = Database.All<MeasuredEventReceipt>("measured.event"), LegacyUsage = Database.All<UsageEvent>("usage"),
        Ledger = Database.All<BillingAccount>("accounting"),
        Outbox = Database.Outbox(take: 100)
    });
    private static string Utc(DateTimeOffset time) => time.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
    public async ValueTask DisposeAsync()
    {
        client.Dispose(); await app.StopAsync(); await app.DisposeAsync(); rsa.Dispose(); SqliteConnection.ClearAllPools();
        var full = Path.GetFullPath(directory); var temp = Path.GetFullPath(Path.GetTempPath());
        if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("synthetic-organization-bearer-", StringComparison.Ordinal))
            throw new InvalidOperationException("Synthetic cleanup path escaped the temporary directory.");
        Directory.Delete(full, true);
    }
}
