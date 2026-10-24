using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fanasa.AccessManagement.Web.Api;
using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.ConsumerCatalog;
using Fanasa.AccessManagement.Web.Domain.Access;
using Fanasa.AccessManagement.Web.Persistence;
using Fanasa.AccessManagement.Web.Platform;
using Fanasa.AccessManagement.Web.ServiceCatalog;
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
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

// Disposable synthetic data, real Kestrel/JWT middleware and production controllers.
// Historical registry schema is created only by this fixture, never by the API.
var checks = 0;
void Check(bool value, string label)
{
    if (!value) throw new InvalidOperationException("FAILED consumer catalog: " + label);
    checks++;
    Console.WriteLine("PASS consumer catalog: " + label);
}
async Task<string> Expect(Fixture f, string path, HttpStatusCode status, string label,
    string? token = null, bool noStore = true, string? header = null, HttpMethod? method = null)
{
    var before = f.Snapshot();
    using var response = await f.Send(path, token, header, method);
    var body = await response.Content.ReadAsStringAsync();
    Check(response.StatusCode == status, label + " HTTP " + (int)response.StatusCode);
    Check(f.Snapshot() == before, label + " preserves all tables and schemas in both databases");
    if (noStore && status != HttpStatusCode.Unauthorized)
        Check(response.Headers.CacheControl?.NoStore == true, label + " is no-store");
    return body;
}
async Task Keys(Fixture f, string[] expected, string label, string? subject = null)
{
    var body = await Expect(f, f.ProductsPath(subject: subject), HttpStatusCode.OK, label, f.Token());
    using var json = JsonDocument.Parse(body);
    var actual = json.RootElement.EnumerateArray().Select(x => x.GetProperty("key").GetString()!).Order(StringComparer.Ordinal);
    Check(actual.SequenceEqual(expected.Order(StringComparer.Ordinal)), label + " exposes exactly the eligible applications");
}

await using (var disabled = await Fixture.Start(enabled: false))
{
    Check(disabled.Configuration["Platform:ConsumerCatalog:Enabled"] is null, "default-off fixture supplies no enable flag");
    await Expect(disabled, disabled.TenantsPath(), HttpStatusCode.ServiceUnavailable, "tenant reader default-off", disabled.Token());
    await Expect(disabled, disabled.ProductsPath(), HttpStatusCode.ServiceUnavailable, "consumer products default-off", disabled.Token());
}
await using (var f = await Fixture.Start())
{
    var tenants = f.TenantsPath();
    var products = f.ProductsPath();
    await Expect(f, tenants, HttpStatusCode.Unauthorized, "anonymous tenant reader");
    await Expect(f, products, HttpStatusCode.Unauthorized, "anonymous products");
    await Expect(f, products, HttpStatusCode.Unauthorized, "invalid JWT signature", f.Token(badSignature: true));
    await Expect(f, products, HttpStatusCode.Unauthorized, "wrong JWT audience", f.Token(audience: "wrong-audience"));
    await Expect(f, products, HttpStatusCode.Unauthorized, "expired JWT", f.Token(expired: true));
    using (var login = await f.Send("/synthetic-login"))
        Check(login.IsSuccessStatusCode, "synthetic administrator cookie established");
    await Expect(f, tenants, HttpStatusCode.Unauthorized, "administrator cookie cannot replace bearer");
    await Expect(f, products, HttpStatusCode.Forbidden, "unapproved client", f.Token(client: "other-client"));
    await Expect(f, products, HttpStatusCode.Forbidden, "developer scope cannot substitute catalog scope", f.Token(scope: "platform.registry"));
    await Expect(f, products, HttpStatusCode.Forbidden, "conflicting signed client id", f.Token(extra: [new("client_id", "other-client")]));
    await Expect(f, products, HttpStatusCode.Forbidden, "inactive signed token", f.Token(extra: [new("active", "false")]));

    var body = await Expect(f, tenants, HttpStatusCode.OK, "memberships independent of developer grants", f.Token());
    using (var json = JsonDocument.Parse(body))
    {
        Check(json.RootElement.GetArrayLength() == 1 && json.RootElement[0].GetProperty("id").GetGuid() == f.Consumer,
            "only delegated subject's active consumer membership is listed");
        Check(json.RootElement[0].EnumerateObject().Select(x => x.Name).Order()
            .SequenceEqual(new[] { "id", "name", "namespace" }.Order()), "tenant DTO exposes only membership fields");
    }
    Check(!f.Registry.Allows(Fixture.Subject, f.Consumer, "developer.read"), "consumer has no manufactured developer permission");
    await Keys(f, [f.Saas.Key, f.Dedicated.Key, f.Linked.Key], "authorized application projection");
    var applicationBody = await Expect(f, products, HttpStatusCode.OK, "consumer transport redaction", f.Token());
    using (var json = JsonDocument.Parse(applicationBody))
    {
        var saas = json.RootElement.EnumerateArray().Single(x => x.GetProperty("key").GetString() == f.Saas.Key);
        Check(saas.GetProperty("tenantId").GetGuid() == f.Producer && f.Producer != f.Consumer,
            "authorized SaaS keeps its different producer tenant");
        Check(saas.GetProperty("repository").ValueKind == JsonValueKind.Null && saas.GetProperty("owner").GetString() == ""
            && saas.GetProperty("attributes").ValueKind == JsonValueKind.Null, "repository, owner identity and Attributes are redacted");
        Check(saas.EnumerateObject().Select(x => x.Name).Order().SequenceEqual(new[]
        {
            "id", "key", "displayName", "audience", "center", "url", "repository", "tenantId", "owner", "attributes"
        }.Order()), "consumer DTO has only compatibility fields");
        Check(!applicationBody.Contains("PRIVATE", StringComparison.Ordinal), "no private registry marker enters consumer response");
    }
    await Keys(f, [], "member without application grants", Fixture.UnentitledSubject);
    await Expect(f, f.ProductsPath(tenant: f.OtherConsumer), HttpStatusCode.Forbidden, "cross-consumer query", f.Token());
    foreach (var path in new[]
    {
        "/api/platform/application-tenants", tenants + "&subject=" + Fixture.Subject, tenants + "&extra=1",
        "/api/platform/application-tenants?subject=" + Fixture.Subject.ToUpperInvariant(),
        "/api/platform/application-tenants?subject=" + Guid.Empty.ToString("D"),
        "/api/platform/products?subject=" + Fixture.Subject, products + "&tenant=" + f.Consumer,
        products + "&maxRows=1", "/api/platform/products?subject=" + Fixture.Subject + "&tenant=bad"
    })
        await Expect(f, path, HttpStatusCode.BadRequest, "noncanonical, duplicate or unsupported query", f.Token());
    foreach (var header in new[] { "X-Subject", "X-User-Id", "X-Tenant-Id", "X-Consumer-Tenant-Id" })
        await Expect(f, products, HttpStatusCode.BadRequest, "spoofed delegation header " + header, f.Token(), header: header);

    f.PutClient(f.ReaderClient with { ClientType = "public" });
    await Expect(f, products, HttpStatusCode.Forbidden, "public client cannot delegate subjects", f.Token());
    f.PutClient(f.ReaderClient with { IsActive = false });
    await Expect(f, products, HttpStatusCode.Forbidden, "deactivated registered client", f.Token());
    f.PutClient(f.ReaderClient with { AllowedScopes = [] });
    await Expect(f, products, HttpStatusCode.Forbidden, "revoked registered catalog scope", f.Token());
    f.PutClient(f.ReaderClient);
    f.SetTenantActive(f.ControlTenant, false);
    await Expect(f, products, HttpStatusCode.Forbidden, "deactivated reader product owner", f.Token());
    f.SetTenantActive(f.ControlTenant, true);
    f.SetTenantActive(f.Consumer, false);
    await Expect(f, products, HttpStatusCode.Forbidden, "deactivated selected consumer", f.Token());
    f.SetTenantActive(f.Consumer, true);
    f.PutMember(f.Member with { IsActive = false });
    await Expect(f, products, HttpStatusCode.Forbidden, "offboarded delegated subject", f.Token());
    var offboarded = await Expect(f, tenants, HttpStatusCode.OK, "offboarded membership list", f.Token());
    using (var json = JsonDocument.Parse(offboarded))
        Check(json.RootElement.GetArrayLength() == 0, "offboarded subject has no listed consumers");
    f.PutMember(f.Member);

    f.Grant(f.Saas, f.Consumer, Fixture.Subject, DateTimeOffset.UtcNow.AddMinutes(-1));
    await Keys(f, [f.Dedicated.Key, f.Linked.Key], "expired application grant");
    f.Grant(f.Saas, f.Consumer, Fixture.Subject);
    f.RemoveGrant(f.Saas, f.Consumer, Fixture.Subject);
    await Keys(f, [f.Dedicated.Key, f.Linked.Key], "revoked application grant");
    f.Grant(f.Saas, f.Consumer, Fixture.Subject);
    f.Offer(f.Saas, active: false);
    await Keys(f, [f.Dedicated.Key, f.Linked.Key], "inactive consumer offer");
    f.Offer(f.Saas, expires: DateTimeOffset.UtcNow.AddMinutes(-1));
    await Keys(f, [f.Dedicated.Key, f.Linked.Key], "expired consumer offer");
    f.Offer(f.Saas);
    f.SetTenantActive(f.Producer, false);
    await Keys(f, [f.Dedicated.Key], "inactive application producer");
    f.SetTenantActive(f.Producer, true);
    foreach (var subscription in new[]
    {
        f.Subscription with { Status = "suspended" }, f.Subscription with { RenewsAt = DateTimeOffset.UtcNow.AddMinutes(-1) },
        f.Subscription with { StartedAt = DateTimeOffset.UtcNow.AddDays(1) }, f.Subscription with { SeatLimit = 9 },
        f.Subscription with { ActiveSeats = 11 }, f.Subscription with { ProductKey = f.Saas.Key },
        f.Subscription with { TenantId = f.OtherConsumer }
    })
    {
        f.PutSubscription(subscription);
        await Keys(f, [f.Saas.Key, f.Dedicated.Key], "invalid or noncurrent linked subscription");
    }
    f.PutSubscription(f.Subscription);
    await Keys(f, [f.Saas.Key, f.Dedicated.Key, f.Linked.Key], "restored authoritative subscription");
    f.ChangeOffer(f.Saas, value => value.Remove("subscriptionId"));
    await Keys(f, [f.Saas.Key, f.Dedicated.Key, f.Linked.Key], "historical offer without optional subscriptionId");
    f.Offer(f.Saas);
    f.SetGrantExpiry(f.Saas, "2026-12-01T00:00:00");
    await Expect(f, products, HttpStatusCode.ServiceUnavailable, "zoneless grant expiry fails closed", f.Token());
    f.Grant(f.Saas, f.Consumer, Fixture.Subject);
    f.ChangeOffer(f.Saas, value => value["expiresAt"] = "2026-12-01T00:00:00");
    await Expect(f, products, HttpStatusCode.ServiceUnavailable, "zoneless offer expiry fails closed", f.Token());
    f.Offer(f.Saas);
    f.ChangeOffer(f.Saas, value => value["active"] = "true");
    await Expect(f, products, HttpStatusCode.ServiceUnavailable, "malformed eligible offer fails closed", f.Token());
    f.Offer(f.Saas);
    var unrelated = f.Registry.Products().Single(x => x.Key == "synthetic-no-grant");
    f.ChangeProduct(unrelated, value => value.Remove("publishAsApplication"));
    await Keys(f, [f.Saas.Key, f.Dedicated.Key, f.Linked.Key], "ungranted malformed metadata does not poison eligible applications");
    foreach (var readProducts in new[] { true, false })
    {
        var beforeRevocation = f.Snapshot();
        var callbacks = 0;
        bool AuthorizeWithCommittedRevocation()
        {
            if (++callbacks == 3) f.PutMember(f.Member with { IsActive = false });
            return true;
        }
        var rejected = false;
        var store = new ConsumerApplicationCatalog(f.Database, f.Access, f.Configuration, TimeProvider.System);
        try
        {
            if (readProducts) store.Products(Fixture.Subject, f.Consumer, AuthorizeWithCommittedRevocation);
            else store.Tenants(Fixture.Subject, AuthorizeWithCommittedRevocation);
        }
        catch (UnauthorizedAccessException) { rejected = true; }
        Check(rejected && callbacks == 3, (readProducts ? "products" : "tenants")
            + " observes membership revocation committed after its Fabric snapshot");
        f.PutMember(f.Member);
        Check(f.Snapshot() == beforeRevocation, "revocation race fixture restores all rows and schemas");
    }
    f.ChangeProduct(f.Saas, value => value.Remove("publishAsApplication"));
    var unavailable = await Expect(f, products, HttpStatusCode.ServiceUnavailable, "missing metadata fails closed", f.Token());
    Check(!unavailable.Contains("PRIVATE", StringComparison.Ordinal) && !unavailable.Contains(f.Saas.Key, StringComparison.Ordinal)
        && !unavailable.Contains("Sqlite", StringComparison.Ordinal), "outage exposes no partial catalog or storage diagnostics");
    f.ChangeProduct(f.Saas, value => value["publishAsApplication"] = true);
    await Keys(f, [f.Saas.Key, f.Dedicated.Key, f.Linked.Key], "restored application metadata");

    var developerPath = "/api/platform/products?subject=" + Fixture.DeveloperSubject;
    var developerBody = await Expect(f, developerPath, HttpStatusCode.OK, "pre-existing developer branch",
        f.Token(client: Fixture.DeveloperClient, scope: "platform.registry"), noStore: false);
    using (var json = JsonDocument.Parse(developerBody))
    {
        var developer = json.RootElement.EnumerateArray().Single(x => x.GetProperty("key").GetString() == f.DeveloperProduct.Key);
        Check(developer.GetProperty("repository").GetString() == "PRIVATE-developer-repository", "developer branch retains its authorized existing DTO");
    }
    await Expect(f, developerPath, HttpStatusCode.Forbidden, "developer branch still requires PlatformService",
        f.Token(client: "unapproved-developer", scope: "platform.registry"), noStore: false);
    await Expect(f, products, HttpStatusCode.Forbidden, "tenant indicator prevents developer fallback",
        f.Token(client: Fixture.DeveloperClient, scope: "platform.registry"));
    await Expect(f, developerPath, HttpStatusCode.Forbidden, "catalog scope prevents developer fallback",
        f.Token(client: Fixture.DeveloperClient, scope: "platform.registry platform.catalog"));
    await Expect(f, "/api/platform/memberships?subject=" + Fixture.Subject, HttpStatusCode.Forbidden,
        "catalog client cannot read developer memberships", f.Token(), noStore: false);
    foreach (var route in new[] { "/api/platform/authorize", "/api/platform/products", "/api/platform/developer-projects" })
        await Expect(f, route, HttpStatusCode.Forbidden, "catalog client cannot mutate developer API", f.Token(), noStore: false, method: HttpMethod.Post);
    f.Configuration["Platform:ServiceCatalog:ClientProductKey"] = null;
    await Expect(f, products, HttpStatusCode.ServiceUnavailable, "unconfigured catalog reader", f.Token());
}
await using (var missing = await Fixture.Start(legacySchema: false))
    await Expect(missing, missing.ProductsPath(), HttpStatusCode.ServiceUnavailable, "missing historical schema is not silently created", missing.Token());
Console.WriteLine($"PASS consumer catalog: {checks} checks");

sealed class Fixture : IAsyncDisposable
{
    public const string Subject = "11111111-aaaa-4aaa-8aaa-111111111111";
    public const string UnentitledSubject = "22222222-bbbb-4bbb-8bbb-222222222222";
    public const string DeveloperSubject = "33333333-cccc-4ccc-8ccc-333333333333";
    public const string DeveloperClient = "fanasa-developer-service";
    private const string ReaderId = "synthetic-catalog-service";
    private const string Issuer = "https://synthetic-idp.invalid/realms/fanasa";
    private const string Audience = "synthetic-central-api";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string directory;
    private readonly string registryPath;
    private readonly string fabricPath;
    private readonly WebApplication app;
    private readonly RSA rsa;
    private readonly RsaSecurityKey signingKey;
    private HttpClient client = null!;
    public IConfiguration Configuration { get; }
    public FabricDatabase Database { get; }
    public PlatformRegistry Registry { get; }
    public PersistentAccessManagement Access { get; }
    public Guid ControlTenant { get; }
    public Guid Producer { get; }
    public Guid Consumer { get; }
    public Guid OtherConsumer { get; }
    public TenantUser Member { get; }
    public ProductClient ReaderClient { get; }
    public PlatformProduct Saas { get; }
    public PlatformProduct Dedicated { get; }
    public PlatformProduct Linked { get; }
    public PlatformProduct DeveloperProduct { get; }
    public TenantSubscription Subscription { get; }

    private Fixture(bool enabled, bool legacySchema)
    {
        directory = Path.Combine(Path.GetTempPath(), "synthetic-consumer-catalog-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        registryPath = Path.Combine(directory, "registry.db");
        fabricPath = Path.Combine(directory, "fabric.db");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development", ContentRootPath = directory,
            ApplicationName = typeof(ConsumerApplicationsApi).Assembly.FullName
        });
        builder.Logging.ClearProviders();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Platform:RegistryPath"] = registryPath,
            ["Platform:ServiceCatalog:ClientId"] = ReaderId,
            ["Platform:ServiceCatalog:ClientProductKey"] = "synthetic-portal",
            ["Platform:ConsumerCatalog:Enabled"] = enabled ? "true" : null
        });
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        Configuration = builder.Configuration;
        Registry = new PlatformRegistry(Configuration);
        Database = new FabricDatabase(fabricPath);
        Access = new PersistentAccessManagement(Database, new InMemoryAccessManagement(Registry));
        ControlTenant = Access.GetTenants().Single().Id;
        Producer = Access.CreateTenant("synthetic-producer", "Synthetic Producer").Id;
        Consumer = Access.CreateTenant("synthetic-consumer", "Synthetic Consumer").Id;
        OtherConsumer = Access.CreateTenant("synthetic-other-consumer", "Synthetic Other Consumer").Id;
        Registry.SetTenantSource(Access.GetTenants);
        Registry.SetMembershipSource(Access.GetUsers);
        foreach (var tenant in Access.GetTenants())
            RegistrySql("INSERT OR IGNORE INTO platform_tenants(id,name,namespace) VALUES($id,$name,$ns)",
                ("$id", tenant.Id.ToString("D")), ("$name", tenant.DisplayName), ("$ns", tenant.Key));
        Member = Access.AddUser(new(Consumer, Subject, "Synthetic Consumer Human"));
        Access.AddUser(new(Consumer, UnentitledSubject, "Synthetic Ungranted Human"));
        Access.AddUser(new(ControlTenant, DeveloperSubject, "Synthetic Developer Human"));
        var portal = Product("synthetic-portal", ControlTenant);
        ReaderClient = Access.RegisterClient(portal.Id, new(ReaderId, "Synthetic Catalog Service", "service", [], [PublishedServiceCatalog.ReadScope]));
        DeveloperProduct = Product("synthetic-developer-product", ControlTenant, repository: "PRIVATE-developer-repository");
        Registry.Grant(new(DeveloperSubject, ControlTenant, "developer.read", null), "synthetic-fixture");
        Saas = Product("synthetic-saas", Producer);
        Dedicated = Product("synthetic-dedicated", Consumer, mode: "dedicated");
        Linked = Product("synthetic-linked", Producer);
        Subscription = new(Guid.NewGuid(), Consumer, Linked.Key, Guid.NewGuid(), "active",
            DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1), 10, 2);
        PutSubscription(Subscription);
        var foreignDedicated = Product("synthetic-foreign-dedicated", Producer, mode: "dedicated");
        var unpublished = Product("synthetic-unpublished", Producer, published: false);
        _ = Product("synthetic-no-grant", Producer);
        var inactiveOffer = Product("synthetic-inactive-offer", Producer);
        var expiredGrant = Product("synthetic-expired-grant", Producer);
        var expiredOffer = Product("synthetic-expired-offer", Producer);
        var otherSubject = Product("synthetic-other-subject", Producer);
        var otherConsumer = Product("synthetic-other-consumer-product", Producer);
        if (legacySchema)
        {
            RegistrySql("CREATE TABLE platform_application_grants(product TEXT NOT NULL,tenant TEXT NOT NULL,subject TEXT NOT NULL,expires TEXT,PRIMARY KEY(product,tenant,subject))");
            RegistrySql("CREATE TABLE platform_application_offers(product TEXT NOT NULL,tenant TEXT NOT NULL,data TEXT NOT NULL,PRIMARY KEY(product,tenant))");
            foreach (var product in new[] { Saas, Dedicated, Linked, foreignDedicated, unpublished, inactiveOffer, expiredGrant, expiredOffer })
            {
                Offer(product, subscriptionId: product == Linked ? Subscription.Id : null);
                Grant(product, Consumer, Subject);
            }
            Offer(inactiveOffer, active: false);
            Offer(expiredOffer, expires: DateTimeOffset.UtcNow.AddMinutes(-1));
            Grant(expiredGrant, Consumer, Subject, DateTimeOffset.UtcNow.AddMinutes(-1));
            Offer(otherSubject);
            Grant(otherSubject, Consumer, Guid.NewGuid().ToString("D"));
            Offer(otherConsumer, tenant: OtherConsumer);
            Grant(otherConsumer, OtherConsumer, Subject);
        }
        rsa = RSA.Create(2048);
        signingKey = new RsaSecurityKey(rsa) { KeyId = "synthetic-catalog-test-key" };
        var oidc = new OpenIdConnectConfiguration { Issuer = Issuer };
        oidc.SigningKeys.Add(signingKey);
        builder.Services.AddSingleton(Registry);
        builder.Services.AddSingleton(Database);
        builder.Services.AddSingleton(Access);
        builder.Services.AddSingleton<IAccessManagement>(Access);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddControllers().AddApplicationPart(typeof(PlatformApi).Assembly);
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options => options.Cookie.Name = "synthetic-catalog-cookie")
            .AddJwtBearer("PlatformBearer", options =>
            {
                options.MapInboundClaims = false;
                options.RequireHttpsMetadata = true;
                options.IncludeErrorDetails = false;
                options.Configuration = oidc;
                options.TokenValidationParameters = new()
                {
                    ValidateIssuer = true, ValidIssuer = Issuer, ValidateAudience = true, ValidAudience = Audience,
                    ValidateIssuerSigningKey = true, RequireSignedTokens = true, ValidateLifetime = true,
                    RequireExpirationTime = true, ClockSkew = TimeSpan.Zero,
                    ValidTypes = ["JWT", "at+jwt"], NameClaimType = "sub"
                };
            });
        builder.Services.AddAuthorization(options => options.AddPolicy("PlatformService", policy =>
            policy.AddAuthenticationSchemes("PlatformBearer").RequireAuthenticatedUser().RequireClaim("azp", DeveloperClient)
                .RequireAssertion(context => context.User.FindFirst("scope")?.Value.Split(' ').Contains("platform.registry") == true)));
        app = builder.Build();
        app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.MapControllers();
        app.MapGet("/synthetic-login", async (HttpContext context) =>
        {
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", Subject), new Claim("permission", "platform.admin")], "synthetic-cookie")));
            return Results.NoContent();
        }).AllowAnonymous();
    }
    public static async Task<Fixture> Start(bool enabled = true, bool legacySchema = true)
    {
        var result = new Fixture(enabled, legacySchema);
        await result.app.StartAsync();
        var address = result.app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        result.client = new HttpClient(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false })
            { BaseAddress = new Uri(address) };
        return result;
    }
    public string TenantsPath(string subject = Subject) => "/api/platform/application-tenants?subject=" + subject;
    public string ProductsPath(Guid? tenant = null, string? subject = null)
        => "/api/platform/products?subject=" + (subject ?? Subject) + "&tenant=" + (tenant ?? Consumer).ToString("D");
    public async Task<HttpResponseMessage> Send(string path, string? token = null, string? header = null, HttpMethod? method = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Get, path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (header is not null) request.Headers.TryAddWithoutValidation(header, Subject);
        if (method == HttpMethod.Post) request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        return await client.SendAsync(request);
    }
    public string Token(string? client = null, string? scope = null, string? audience = null,
        bool badSignature = false, bool expired = false, Claim[]? extra = null)
    {
        var now = DateTime.UtcNow;
        var claims = new List<Claim>
        {
            new("sub", "synthetic-service-subject"), new("azp", client ?? ReaderId), new("scope", scope ?? PublishedServiceCatalog.ReadScope)
        };
        if (extra is not null) claims.AddRange(extra);
        using var other = badSignature ? RSA.Create(2048) : null;
        var key = other is null ? signingKey : new RsaSecurityKey(other) { KeyId = signingKey.KeyId };
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(Issuer, audience ?? Audience, claims,
            notBefore: now.AddMinutes(-2), expires: expired ? now.AddMinutes(-1) : now.AddMinutes(5),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.RsaSha256)));
    }
    private PlatformProduct Product(string key, Guid owner, string mode = "saas", bool published = true, string repository = "PRIVATE-repository")
    {
        var product = Registry.Create(new(Guid.NewGuid(), key, "Synthetic " + key, "synthetic." + key, "fanasa.rayan",
            "https://synthetic.fanasa.net.local/" + key, repository, owner, "PRIVATE-owner",
            new() { ["private.credential"] = "PRIVATE-attribute" }), "synthetic-fixture");
        ChangeProduct(product, value => { value["publishAsApplication"] = published; value["tenancyMode"] = mode; value["origin"] = "managed"; });
        return product;
    }
    public void ChangeProduct(PlatformProduct product, Action<JsonObject> change)
    {
        using var connection = Open(registryPath);
        using var read = connection.CreateCommand();
        read.CommandText = "SELECT data FROM platform_products WHERE key=$key";
        read.Parameters.AddWithValue("$key", product.Key);
        var value = JsonNode.Parse((string)read.ExecuteScalar()!)!.AsObject();
        change(value);
        using var write = connection.CreateCommand();
        write.CommandText = "UPDATE platform_products SET data=$data WHERE key=$key";
        write.Parameters.AddWithValue("$key", product.Key);
        write.Parameters.AddWithValue("$data", value.ToJsonString(Json));
        write.ExecuteNonQuery();
    }
    public void Offer(PlatformProduct product, bool active = true, DateTimeOffset? expires = null, Guid? subscriptionId = null, Guid? tenant = null)
        => RegistrySql("INSERT INTO platform_application_offers(product,tenant,data) VALUES($product,$tenant,$data) ON CONFLICT(product,tenant) DO UPDATE SET data=excluded.data",
            ("$product", product.Key), ("$tenant", (tenant ?? Consumer).ToString("D")),
            ("$data", JsonSerializer.Serialize(new { productKey = product.Key, tenantId = tenant ?? Consumer, active,
                seatLimit = 10, plan = "synthetic-plan", expiresAt = expires, subscriptionId }, Json)));
    public void Grant(PlatformProduct product, Guid tenant, string subject, DateTimeOffset? expires = null)
        => RegistrySql("INSERT INTO platform_application_grants(product,tenant,subject,expires) VALUES($product,$tenant,$subject,$expires) ON CONFLICT(product,tenant,subject) DO UPDATE SET expires=excluded.expires",
            ("$product", product.Key), ("$tenant", tenant.ToString("D")), ("$subject", subject), ("$expires", expires?.ToString("O", CultureInfo.InvariantCulture)));
    public void SetGrantExpiry(PlatformProduct product, string expiry)
        => RegistrySql("UPDATE platform_application_grants SET expires=$expires WHERE product=$product AND tenant=$tenant AND subject=$subject",
            ("$product", product.Key), ("$tenant", Consumer.ToString("D")), ("$subject", Subject), ("$expires", expiry));
    public void ChangeOffer(PlatformProduct product, Action<JsonObject> change)
    {
        using var connection = Open(registryPath);
        using var read = connection.CreateCommand();
        read.CommandText = "SELECT data FROM platform_application_offers WHERE product=$product AND tenant=$tenant";
        read.Parameters.AddWithValue("$product", product.Key);
        read.Parameters.AddWithValue("$tenant", Consumer.ToString("D"));
        var value = JsonNode.Parse((string)read.ExecuteScalar()!)!.AsObject();
        change(value);
        using var write = connection.CreateCommand();
        write.CommandText = "UPDATE platform_application_offers SET data=$data WHERE product=$product AND tenant=$tenant";
        write.Parameters.AddWithValue("$product", product.Key);
        write.Parameters.AddWithValue("$tenant", Consumer.ToString("D"));
        write.Parameters.AddWithValue("$data", value.ToJsonString(Json));
        write.ExecuteNonQuery();
    }
    public void RemoveGrant(PlatformProduct product, Guid tenant, string subject)
        => RegistrySql("DELETE FROM platform_application_grants WHERE product=$product AND tenant=$tenant AND subject=$subject",
            ("$product", product.Key), ("$tenant", tenant.ToString("D")), ("$subject", subject));
    public void PutClient(ProductClient value) => Put("client", value.Id, value);
    public void PutMember(TenantUser value) => Put("membership", value.Id, value);
    public void PutSubscription(TenantSubscription value) => Put("subscription", value.Id, value);
    private void Put<T>(string kind, Guid id, T value) => Database.Transaction(() => { Database.Put(kind, id.ToString("D"), value); return true; });
    public void SetTenantActive(Guid id, bool active) => Put("tenant", id, Access.GetTenants().Single(x => x.Id == id) with { IsActive = active });
    private void RegistrySql(string sql, params (string Key, string? Value)[] parameters)
    {
        using var connection = Open(registryPath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var parameter in parameters) command.Parameters.AddWithValue(parameter.Key, (object?)parameter.Value ?? DBNull.Value);
        command.ExecuteNonQuery();
    }
    private static SqliteConnection Open(string path, bool readOnly = false)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        connection.Open();
        return connection;
    }
    public string Snapshot() => SnapshotDatabase(fabricPath) + "|" + SnapshotDatabase(registryPath);
    private static string SnapshotDatabase(string path)
    {
        using var connection = Open(path, readOnly: true);
        var evidence = new List<string>();
        var tables = new List<string>();
        using (var schema = connection.CreateCommand())
        {
            schema.CommandText = "SELECT type,name,tbl_name,sql FROM sqlite_master ORDER BY type,name";
            using var reader = schema.ExecuteReader();
            while (reader.Read())
            {
                evidence.Add(JsonSerializer.Serialize(Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null : reader.GetString(i))));
                if (reader.GetString(0) == "table") tables.Add(reader.GetString(1));
            }
        }
        foreach (var table in tables)
        {
            var rows = new List<string>();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT * FROM \"" + table.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                rows.Add(JsonSerializer.Serialize(Enumerable.Range(0, reader.FieldCount)
                    .Select(i => reader.IsDBNull(i) ? null : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture))));
            evidence.Add(table + ":" + string.Join("\n", rows.Order(StringComparer.Ordinal)));
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", evidence))));
    }
    public async ValueTask DisposeAsync()
    {
        client.Dispose();
        await app.StopAsync(); await app.DisposeAsync();
        Database.Dispose(); rsa.Dispose(); SqliteConnection.ClearAllPools();
        var target = Path.GetFullPath(directory);
        var parent = Path.GetFullPath(Path.GetTempPath());
        if (!target.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(target).StartsWith("synthetic-consumer-catalog-", StringComparison.Ordinal))
            throw new InvalidOperationException("Synthetic cleanup escaped its temporary directory.");
        Directory.Delete(target, recursive: true);
    }
}
