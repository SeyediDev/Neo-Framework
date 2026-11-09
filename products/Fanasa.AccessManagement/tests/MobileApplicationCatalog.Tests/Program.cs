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


using Fanasa.AccessManagement.Web.MobileCatalog;

// Real JWT middleware and production controllers; all identities and databases are synthetic.
var checks = 0;
void Check(bool value, string label)
{
    if (!value) throw new InvalidOperationException("FAILED mobile catalog: " + label);
    checks++;
    Console.WriteLine("PASS mobile catalog: " + label);
}
async Task<string> Expect(Fixture f, string path, HttpStatusCode status, string label,
    string? token = null, string? header = null)
{
    var before = f.Snapshot();
    using var response = await f.Send(path, token, header);
    var body = await response.Content.ReadAsStringAsync();
    Check(response.StatusCode == status, label + " HTTP " + (int)response.StatusCode);
    Check(f.Snapshot() == before, label + " preserves both databases and schemas");
    if (status != HttpStatusCode.Unauthorized)
        Check(response.Headers.CacheControl?.NoStore == true, label + " no-store");
    return body;
}
async Task Keys(Fixture f, string[] expected, string label, string? token = null)
{
    var body = await Expect(f, f.ProductsPath(), HttpStatusCode.OK, label, token ?? f.Token());
    using var json = JsonDocument.Parse(body);
    Check(json.RootElement.EnumerateArray().Select(x => x.GetProperty("key").GetString()!).Order(StringComparer.Ordinal)
        .SequenceEqual(expected.Order(StringComparer.Ordinal)), label + " exact entitled keys");
}
await using (var disabled = await Fixture.Start(enabled: false))
{
    Check(disabled.Configuration["Platform:MobileCatalog:Enabled"] is null, "feature defaults off");
    await Expect(disabled, disabled.TenantsPath(), HttpStatusCode.ServiceUnavailable, "disabled reader", disabled.Token());
}
await using (var f = await Fixture.Start())
{
    var tenants = f.TenantsPath();
    var apps = f.ProductsPath();
    foreach (var request in new RegisterClientRequest[]
    {
        new("other-mobile", "Other Mobile", "public", [MobileCatalogAuthorization.AndroidRedirectUri], [MobileCatalogAuthorization.ReadScope]),
        new(MobileCatalogAuthorization.AndroidClientId, "Service", "service", [MobileCatalogAuthorization.AndroidRedirectUri], [MobileCatalogAuthorization.ReadScope]),
        new(MobileCatalogAuthorization.AndroidClientId, "No Scope", "public", [MobileCatalogAuthorization.AndroidRedirectUri], []),
        new(MobileCatalogAuthorization.AndroidClientId, "Other Callback", "public", ["other:/callback"], [MobileCatalogAuthorization.ReadScope]),
        new(MobileCatalogAuthorization.AndroidClientId, "Several Callbacks", "public", [MobileCatalogAuthorization.AndroidRedirectUri, "https://synthetic.fanasa.net.local/callback"], [MobileCatalogAuthorization.ReadScope])
    })
    {
        var before = f.Snapshot();
        var rejected = false;
        try { f.Access.RegisterClient(f.Saas.Id, request); }
        catch (ArgumentException) { rejected = true; }
        Check(rejected && f.Snapshot() == before, "native callback exception stays limited to exact approved public registration");
    }
    var httpsRegistration = f.Access.RegisterClient(f.Saas.Id,
        new("synthetic-https-client", "Existing HTTPS Client", "service", ["https://synthetic.fanasa.net.local/callback"], []));
    Check(httpsRegistration.RedirectUris.SequenceEqual(["https://synthetic.fanasa.net.local/callback"]), "ordinary HTTPS client registration remains accepted");
    await Expect(f, tenants, HttpStatusCode.Unauthorized, "anonymous reader");
    await Expect(f, apps, HttpStatusCode.Unauthorized, "invalid signature", f.Token(badSignature: true));
    await Expect(f, apps, HttpStatusCode.Unauthorized, "wrong API audience", f.Token(audience: "fanasa-mobile-android"));
    await Expect(f, apps, HttpStatusCode.Unauthorized, "expired token", f.Token(expired: true));
    using (var login = await f.Send("/synthetic-login")) Check(login.IsSuccessStatusCode, "synthetic admin cookie");
    await Expect(f, tenants, HttpStatusCode.Unauthorized, "cookie cannot substitute bearer");
    await Expect(f, apps, HttpStatusCode.Forbidden, "unapproved azp", f.Token(client: "different-android"));
    await Expect(f, apps, HttpStatusCode.Forbidden, "additional audience cannot broaden exact audience", f.Token(extra: [new("aud", "unrelated-api")]));
    await Expect(f, apps, HttpStatusCode.Unauthorized, "duplicate signed clients", f.Token(extra: [new("azp", "fanasa-mobile-android")]));
    await Expect(f, apps, HttpStatusCode.Forbidden, "additional audience cannot broaden exact audience", f.Token(extra: [new("aud", "unrelated-api")]));
    await Expect(f, apps, HttpStatusCode.Unauthorized, "duplicate signed clients", f.Token(extra: [new("azp", "fanasa-mobile-android")]));
    await Expect(f, apps, HttpStatusCode.Forbidden, "additional audience cannot broaden exact audience", f.Token(extra: [new("aud", "unrelated-api")]));
    await Expect(f, apps, HttpStatusCode.Unauthorized, "duplicate signed clients", f.Token(extra: [new("azp", "fanasa-mobile-android")]));
    await Expect(f, apps, HttpStatusCode.Forbidden, "service catalog scope cannot substitute self scope", f.Token(scope: "platform.catalog"));
    await Expect(f, apps, HttpStatusCode.Forbidden, "developer scope cannot substitute self scope", f.Token(scope: "platform.registry"));
    await Expect(f, apps, HttpStatusCode.Forbidden, "invalid subject", f.Token(subject: "invalid-subject"));
    await Expect(f, apps, HttpStatusCode.Unauthorized, "duplicate signed subjects", f.Token(extra: [new("sub", Fixture.UnentitledSubject)]));
    await Expect(f, apps, HttpStatusCode.Forbidden, "conflicting signed client", f.Token(extra: [new("client_id", "different-android")]));
    await Expect(f, apps, HttpStatusCode.Forbidden, "inactive signed token", f.Token(extra: [new("active", "false")]));
    var tenantBody = await Expect(f, tenants, HttpStatusCode.OK, "self memberships", f.Token());
    using (var json = JsonDocument.Parse(tenantBody))
        Check(json.RootElement.GetArrayLength() == 1 && json.RootElement[0].GetProperty("id").GetGuid() == f.Consumer,
            "only own active consumer tenant");
    await Keys(f, [f.Saas.Key, f.Dedicated.Key, f.Linked.Key], "self applications");
    foreach (var publicUrl in new[] { "https://sample.app.fanasacloud.ir/board", "https://app.fanasacloud.ir/" })
    {
        f.ChangeProduct(f.Saas, value => value["url"] = publicUrl);
        var publicApps = await Expect(f, apps, HttpStatusCode.OK, "approved public launch URL", f.Token());
        using var publicJson = JsonDocument.Parse(publicApps);
        Check(publicJson.RootElement.EnumerateArray().Single(x => x.GetProperty("key").GetString() == f.Saas.Key)
            .GetProperty("url").GetString() == publicUrl, "catalog preserves approved public launch URL");
    }
    foreach (var unsafeUrl in new[] { "https://sample.app.fanasacloud.ir.evil.test/", "https://nested.sample.app.fanasacloud.ir/", "https://fanasacloud.ir/" })
    {
        f.ChangeProduct(f.Saas, value => value["url"] = unsafeUrl);
        await Expect(f, apps, HttpStatusCode.ServiceUnavailable, "foreign or nested public launch URL fails closed", f.Token());
    }
    f.ChangeProduct(f.Saas, value => value["url"] = f.Saas.Url);
    await Keys(f, [], "second user's token cannot see first user's apps", f.Token(subject: Fixture.UnentitledSubject));
    var appBody = await Expect(f, apps, HttpStatusCode.OK, "redacted app transport", f.Token());
    using (var json = JsonDocument.Parse(appBody))
    {
        var saas = json.RootElement.EnumerateArray().Single(x => x.GetProperty("key").GetString() == f.Saas.Key);
        Check(saas.GetProperty("tenantId").GetGuid() == f.Producer && f.Producer != f.Consumer, "SaaS producer is not consumer filter");
        Check(saas.GetProperty("repository").ValueKind == JsonValueKind.Null && saas.GetProperty("owner").GetString() == ""
            && saas.GetProperty("attributes").ValueKind == JsonValueKind.Null && !appBody.Contains("PRIVATE", StringComparison.Ordinal),
            "private registry metadata redacted");
    }
    foreach (var suffix in new[] { "?subject=" + Fixture.UnentitledSubject, "?tenant=" + f.OtherConsumer, "?page=1", "?subject=a&subject=b" })
        await Expect(f, apps + suffix, HttpStatusCode.BadRequest, "query cannot delegate identity", f.Token());
    foreach (var header in new[] { "X-Subject", "X-Subject-Id", "X-User-Id", "X-Tenant", "X-Tenant-Id", "X-Consumer-Tenant-Id" })
        await Expect(f, apps, HttpStatusCode.BadRequest, "identity header rejected " + header, f.Token(), header);
    foreach (var invalidTenant in new[] { Guid.Empty.ToString("D"), f.Consumer.ToString("D").ToUpperInvariant(), "invalid" })
        await Expect(f, "/api/mobile/catalog/tenants/" + invalidTenant + "/apps", HttpStatusCode.BadRequest, "noncanonical tenant", f.Token());
    await Expect(f, f.ProductsPath(f.OtherConsumer), HttpStatusCode.Forbidden, "cross-consumer tenant", f.Token());
    foreach (var registration in new[]
    {
        f.ReaderClient with { IsActive = false }, f.ReaderClient with { ClientType = "service" },
        f.ReaderClient with { AllowedScopes = [] }, f.ReaderClient with { RedirectUris = ["other:/callback"] }
    })
    {
        f.PutClient(registration);
        await Expect(f, apps, HttpStatusCode.Forbidden, "current public registration required", f.Token());
    }
    f.PutClient(f.ReaderClient);
    f.SetTenantActive(f.ControlTenant, false);
    await Expect(f, apps, HttpStatusCode.Forbidden, "mobile product owner active", f.Token());
    f.SetTenantActive(f.ControlTenant, true);
    f.PutMember(f.Member with { IsActive = false });
    await Expect(f, apps, HttpStatusCode.Forbidden, "revoked membership", f.Token());
    Check(await Expect(f, tenants, HttpStatusCode.OK, "revoked membership absent", f.Token()) == "[]", "no revoked tenant");
    f.PutMember(f.Member);
    f.RemoveGrant(f.Saas, f.Consumer, Fixture.Subject);
    await Keys(f, [f.Dedicated.Key, f.Linked.Key], "revoked app grant immediately hidden");
    f.Grant(f.Saas, f.Consumer, Fixture.Subject);
    f.ChangeProduct(f.Saas, value => value.Remove("publishAsApplication"));
    var outage = await Expect(f, apps, HttpStatusCode.ServiceUnavailable, "malformed registry fails closed", f.Token());
    Check(!outage.Contains(f.Saas.Key, StringComparison.Ordinal) && !outage.Contains("Sqlite", StringComparison.Ordinal), "no partial data or storage diagnostics");
    f.ChangeProduct(f.Saas, value => value["publishAsApplication"] = true);
    f.ChangeProduct(f.Saas, value => value["url"] = "http://untrusted.example/app");
    await Expect(f, apps, HttpStatusCode.ServiceUnavailable, "unsafe launch URL fails closed", f.Token());
    f.ChangeProduct(f.Saas, value => value["url"] = f.Saas.Url);
    f.ChangeProduct(f.Saas, value => value["url"] = "http://untrusted.example/app");
    await Expect(f, apps, HttpStatusCode.ServiceUnavailable, "unsafe launch URL fails closed", f.Token());
    f.ChangeProduct(f.Saas, value => value["url"] = f.Saas.Url);
    f.ChangeProduct(f.Saas, value => value["url"] = "http://untrusted.example/app");
    await Expect(f, apps, HttpStatusCode.ServiceUnavailable, "unsafe launch URL fails closed", f.Token());
    f.ChangeProduct(f.Saas, value => value["url"] = f.Saas.Url);
    await Expect(f, "/api/platform/application-tenants?subject=" + Fixture.Subject, HttpStatusCode.Forbidden,
        "public self client cannot delegate subjects", f.Token());
    f.Configuration["Platform:MobileCatalog:ClientProductKey"] = "unregistered-portal";
    await Expect(f, apps, HttpStatusCode.Forbidden, "unknown central product", f.Token());
}
await using (var missing = await Fixture.Start(legacySchema: false))
    await Expect(missing, missing.ProductsPath(), HttpStatusCode.ServiceUnavailable, "schema never manufactured", missing.Token());
Console.WriteLine($"PASS mobile catalog: {checks} checks");

sealed class Fixture : IAsyncDisposable
{
    public const string Subject = "11111111-aaaa-4aaa-8aaa-111111111111";
    public const string UnentitledSubject = "22222222-bbbb-4bbb-8bbb-222222222222";
    public const string DeveloperSubject = "33333333-cccc-4ccc-8ccc-333333333333";
    public const string DeveloperClient = "fanasa-developer-service";
    private const string ReaderId = "fanasa-mobile-android";
    private const string Issuer = "https://synthetic-idp.invalid/realms/fanasa";
    private const string Audience = MobileCatalogAuthorization.Audience;
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
            ["Platform:ConsumerCatalog:Enabled"] = "true",
            ["Platform:MobileCatalog:Enabled"] = enabled ? "true" : null,
            ["Platform:MobileCatalog:ClientId"] = ReaderId,
            ["Platform:MobileCatalog:ClientProductKey"] = "synthetic-portal"
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
        ReaderClient = Access.RegisterClient(portal.Id, new(ReaderId, "Synthetic Android Public Client", "public", [MobileCatalogAuthorization.AndroidRedirectUri], [MobileCatalogAuthorization.ReadScope]));
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
        builder.Services.AddTransient<IClaimsTransformation, Fanasa.AccessManagement.Web.Security.FabricClaimsTransformation>();
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
    public string TenantsPath() => "/api/mobile/catalog/tenants";
    public string ProductsPath(Guid? tenant = null)
        => "/api/mobile/catalog/tenants/" + (tenant ?? Consumer).ToString("D") + "/apps";
    public async Task<HttpResponseMessage> Send(string path, string? token = null, string? header = null, HttpMethod? method = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Get, path);
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (header is not null) request.Headers.TryAddWithoutValidation(header, Subject);
        if (method == HttpMethod.Post) request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
        return await client.SendAsync(request);
    }
    public string Token(string? client = null, string? scope = null, string? audience = null,
        bool badSignature = false, bool expired = false, Claim[]? extra = null, string? subject = null)
    {
        var now = DateTime.UtcNow;
        var claims = new List<Claim>
        {
            new("sub", subject ?? Subject), new("azp", client ?? ReaderId), new("scope", scope ?? MobileCatalogAuthorization.ReadScope)
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
