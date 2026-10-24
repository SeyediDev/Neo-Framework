using Fanasa.AccessManagement.Web.Application.Tenancy;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using Fanasa.AccessManagement.Web;
using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Domain.Access;
using Fanasa.AccessManagement.Web.Persistence;
using Fanasa.AccessManagement.Web.Platform;
using Fanasa.AccessManagement.Web.ServiceCatalog;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

var count = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException(name);
    count++;
    Console.WriteLine("PASS " + name);
}
var directory = Path.Combine(Path.GetTempPath(), "fanasa-published-catalog-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(directory);
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    EnvironmentName = "Development", ContentRootPath = directory,
    ApplicationName = typeof(PublishedServicesApi).Assembly.FullName
});
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Logging.ClearProviders();
builder.Configuration["Platform:RegistryPath"] = Path.Combine(directory, "registry.db");
builder.Configuration["Platform:ServiceCatalog:ClientId"] = "fanasa-unified-portal-service";
builder.Configuration["Platform:ServiceCatalog:ClientProductKey"] = "synthetic-portal";
builder.Configuration["Platform:ServiceCatalog:OfficialLinkHosts:0"] = "platform.fanasa.net.local";
var registry = new PlatformRegistry(builder.Configuration);
var db = new FabricDatabase(Path.Combine(directory, "fabric.db"));
var access = new PersistentAccessManagement(db, new InMemoryAccessManagement(registry));
registry.SetTenantSource(access.GetTenants);
registry.SetMembershipSource(access.GetUsers);
var tenant = registry.Tenants().Single().Id;
var portal = registry.Create(new(Guid.Empty, "synthetic-portal", "Synthetic Portal", "portal",
    "fanasa.rayan", "https://portal.fanasa.net.local/", null, tenant, "platform"), "synthetic-admin");
var product = registry.Create(new(Guid.Empty, "synthetic-product", "Synthetic Product", "sample",
    "fanasa.rayan", "https://sample.fanasa.net.local/", "private-repository", tenant, "private-owner",
    new() { ["catalog.published"] = "true", ["private.credential"] = "private-test-value" }), "synthetic-admin");
var registration = access.RegisterClient(portal.Id, new RegisterClientRequest("fanasa-unified-portal-service",
    "Synthetic reader", "service", [], [PublishedServiceCatalog.ReadScope]));
builder.Services.AddSingleton(registry);
builder.Services.AddSingleton(db);
builder.Services.AddSingleton<IAccessManagement>(access);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddControllersWithViews().AddApplicationPart(typeof(PublishedServicesApi).Assembly);
builder.Services.AddAntiforgery(o => o.HeaderName = "X-CSRF-TOKEN");
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
        o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
    })
    .AddScheme<AuthenticationSchemeOptions, FixtureBearer>("PlatformBearer", _ => { });
builder.Services.AddAuthorization(o => o.AddPolicy("PlatformAdmin",
    p => p.RequireAuthenticatedUser().RequireClaim("permission", "platform.admin")));
await using var app = builder.Build();
app.UseRouting(); app.UseAuthentication(); app.UseAuthorization();
app.MapControllers();
// Synthetic sign-in and tokens exist only in this disposable test host.
app.MapGet("/test/login/{role}", async (string role, HttpContext context) =>
{
    var claims = new List<Claim> { new("sub", "synthetic-" + role) };
    if (role == "admin") claims.Add(new("permission", "platform.admin"));
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
        new(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
    return Results.Ok();
}).AllowAnonymous();
app.MapGet("/test/csrf", (HttpContext context, IAntiforgery antiforgery) =>
    Results.Text(antiforgery.GetAndStoreTokens(context).RequestToken!)).RequireAuthorization("PlatformAdmin");
await app.StartAsync();
var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
HttpClient Browser() => new(new HttpClientHandler { CookieContainer = new(), AllowAutoRedirect = false })
    { BaseAddress = new Uri(address) };
using var anonymous = Browser();
using var reader = Browser();
using var admin = Browser();
using var member = Browser();
reader.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "valid");
await admin.GetAsync("/test/login/admin");
await member.GetAsync("/test/login/member");
var csrf = await admin.GetStringAsync("/test/csrf");
var path = "/api/platform/service-publications/" + product.Id;
var command = new ServicePublicationCommand(0, true, "Approved public description", null, null, "Synthetic publication approval");
async Task<HttpResponseMessage> Save(HttpClient client, ServicePublicationCommand value, bool token = true, string? route = null)
{
    using var request = new HttpRequestMessage(HttpMethod.Put, route ?? path) { Content = JsonContent.Create(value) };
    if (token) request.Headers.Add("X-CSRF-TOKEN", csrf);
    return await client.SendAsync(request);
}
async Task<int> CatalogLength()
{
    using var response = await reader.GetAsync("/api/platform/published-services");
    response.EnsureSuccessStatusCode();
    using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
    return json.RootElement.GetArrayLength();
}
try
{
    Check((await anonymous.GetAsync("/api/platform/published-services")).StatusCode == HttpStatusCode.Unauthorized, "anonymous catalog denied");
    Check((await admin.GetAsync("/api/platform/published-services")).StatusCode == HttpStatusCode.Unauthorized, "admin cookie cannot replace reader bearer");
    Check((await anonymous.GetAsync(path)).StatusCode == HttpStatusCode.Unauthorized, "anonymous publication denied");
    using (var missingPublication = await admin.GetAsync(path))
    {
        Check(missingPublication.StatusCode == HttpStatusCode.NoContent, "missing administrator publication returns 204");
        Check((await missingPublication.Content.ReadAsByteArrayAsync()).Length == 0, "missing publication has no response content");
    }
    Check((await member.GetAsync(path)).StatusCode == HttpStatusCode.Forbidden, "member publication denied");
    Check((await reader.GetAsync(path)).StatusCode == HttpStatusCode.Unauthorized, "reader bearer cannot read administrator publication");
    Check((await Save(reader, command, false)).StatusCode == HttpStatusCode.Unauthorized, "reader bearer cannot publish");
    Check((await Save(admin, command, false)).StatusCode == HttpStatusCode.BadRequest, "publication requires antiforgery");
    Check(await CatalogLength() == 0, "registration and malicious Attributes do not publish products");
    foreach (var token in new[] { "wrong-client", "wrong-scope", "duplicate-client", "missing-sub", "missing-exp", "expired", "duplicate-exp", "future-nbf", "conflicting-client-id", "inactive-token" })
    {
        reader.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Check((await reader.GetAsync("/api/platform/published-services")).StatusCode == HttpStatusCode.Forbidden, "reader identity denied: " + token);
    }
    reader.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "valid");
    Check((await reader.GetAsync("/api/platform/published-services?subject=forged&tenant=" + tenant)).StatusCode == HttpStatusCode.BadRequest, "delegation query rejected");
    builder.Configuration["Platform:ServiceCatalog:ClientId"] = null;
    Check((await reader.GetAsync("/api/platform/published-services")).StatusCode == HttpStatusCode.ServiceUnavailable, "missing configuration fails closed");
    builder.Configuration["Platform:ServiceCatalog:ClientId"] = "fanasa-unified-portal-service";
    db.Transaction(() => { db.Put("client", registration.Id.ToString(), registration with { IsActive = false }); return true; });
    Check((await reader.GetAsync("/api/platform/published-services")).StatusCode == HttpStatusCode.Forbidden, "live client deactivation takes effect");
    db.Transaction(() => { db.Put("client", registration.Id.ToString(), registration with { AllowedScopes = [] }); return true; });
    Check((await reader.GetAsync("/api/platform/published-services")).StatusCode == HttpStatusCode.Forbidden, "live registered scope required");
    db.Transaction(() => { db.Put("client", registration.Id.ToString(), registration); return true; });
    foreach (var bad in new[]
    {
        "javascript:alert(1)", "http://platform.fanasa.net.local/service", "https://public.example/service",
        "https://user:secret@platform.fanasa.net.local/service", "https://platform.fanasa.net.local:8443/service",
        "https://platform.fanasa.net.local.evil.example/service", "https://platform.fanasa.net.local/service#fragment"
    })
        Check((await Save(admin, command with { DetailsUrl = bad })).StatusCode == HttpStatusCode.BadRequest, "unsafe link denied: " + bad);
    Check((await Save(admin, command with { Description = "" })).StatusCode == HttpStatusCode.BadRequest, "empty public description denied");
    Check((await Save(admin, command with { Description = new string('x', 1601) })).StatusCode == HttpStatusCode.BadRequest, "public description bounded");
    Check((await Save(admin, command with { Reason = "" })).StatusCode == HttpStatusCode.BadRequest, "publication reason required");
    Check((await Save(admin, command, route: "/api/platform/service-publications/" + Guid.NewGuid())).StatusCode == HttpStatusCode.NotFound, "unknown central product denied");
    Check(db.Outbox().Count(x => x.Kind == PublishedServiceCatalog.AuditKind) == 0, "rejected requests leave no publication audit");
    Check((await Save(admin, command)).StatusCode == HttpStatusCode.OK, "administrator explicitly publishes");
    using (var response = await reader.GetAsync("/api/platform/published-services"))
    {
        Check(response.Headers.CacheControl?.NoStore == true, "catalog response is no-store");
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        var item = json.RootElement[0];
        Check(json.RootElement.GetArrayLength() == 1 && item.GetProperty("productId").GetGuid() == product.Id, "published metadata references same central product");
        Check(item.EnumerateObject().Select(x => x.Name).Order().SequenceEqual(
            new[] { "productId", "key", "displayName", "center", "description", "detailsUrl", "requestAccessUrl", "published" }.Order()), "transport exposes only approved public fields");
        Check(item.GetProperty("detailsUrl").ValueKind == JsonValueKind.Null
            && item.GetProperty("requestAccessUrl").ValueKind == JsonValueKind.Null, "unestablished official links remain null");
        Check(!body.Contains("private-repository") && !body.Contains("private-owner") && !body.Contains("private-test-value"), "private registry metadata excluded");
    }
    Check(registry.Grants().Length == 0, "publication grants no product access");
    Check((await Save(admin, command)).StatusCode == HttpStatusCode.Conflict, "stale revision cannot overwrite publication");
    var catalog = new PublishedServiceCatalog(registry, db, builder.Configuration, TimeProvider.System);
    Check(catalog.Read(product.Id) is { Revision: 1, Actor: "synthetic-admin" }, "publication records authenticated actor");
    Check(db.Outbox().Count(x => x.Kind == PublishedServiceCatalog.AuditKind) == 1, "publication audit emitted exactly once");
    using (var restarted = new FabricDatabase(Path.Combine(directory, "fabric.db")))
        Check(new PublishedServiceCatalog(registry, restarted, builder.Configuration, TimeProvider.System).Catalog().Single().ProductId == product.Id, "publication survives database reopen");
    var withLinks = command with
    {
        ExpectedRevision = 1, DetailsUrl = "https://platform.fanasa.net.local/services/synthetic-product",
        RequestAccessUrl = "https://platform.fanasa.net.local/requests/synthetic-product"
    };
    Check((await Save(admin, withLinks)).StatusCode == HttpStatusCode.OK, "explicit approved host links accepted");
    using (var connection = new SqliteConnection("Data Source=" + Path.Combine(directory, "fabric.db")))
    {
        connection.Open();
        using var sql = connection.CreateCommand();
        sql.CommandText = "CREATE TRIGGER reject_catalog_audit BEFORE INSERT ON fabric_outbox WHEN NEW.kind='PlatformServicePublicationChanged' BEGIN SELECT RAISE(ABORT,'synthetic audit failure'); END";
        sql.ExecuteNonQuery();
        Check((await Save(admin, withLinks with { ExpectedRevision = 2, Published = false })).StatusCode == HttpStatusCode.ServiceUnavailable, "audit failure returns sanitized outage");
        Check(catalog.Read(product.Id) is { Revision: 2, Published: true }, "audit failure rolls back publication change");
        sql.CommandText = "DROP TRIGGER reject_catalog_audit";
        sql.ExecuteNonQuery();
    }
    Check((await Save(admin, withLinks with { ExpectedRevision = 2, Published = false, Reason = "Synthetic withdrawal" })).StatusCode == HttpStatusCode.OK, "administrator explicitly withdraws publication");
    Check(await CatalogLength() == 0, "withdrawn publication disappears immediately");
    Check(catalog.Read(product.Id) is { Revision: 3, Published: false }, "withdrawal remains durable administrator evidence");
    Check(db.Outbox().Count(x => x.Kind == PublishedServiceCatalog.AuditKind) == 3, "publication changes and withdrawal have atomic audit");
    Check((await Save(admin, command with { ExpectedRevision = 3, DetailsUrl = "https://platform.fanasa.net.local/service" })).StatusCode == HttpStatusCode.OK,
        "publish lifecycle fixture with an approved official link");
    var publicationOwner = access.GetTenants().Single(x => x.Id == tenant);
    db.Transaction(() => { db.Put("tenant", tenant.ToString(), publicationOwner with { IsActive = false }); return true; });
    Check((await admin.GetAsync(path)).StatusCode == HttpStatusCode.OK, "administrator can inspect publication after owner deactivation");
    Check((await Save(admin, command with { ExpectedRevision = 4, Published = false })).StatusCode == HttpStatusCode.OK,
        "administrator can withdraw publication after owner deactivation");
    Check(catalog.Read(product.Id) is { Revision: 5, Published: false, DetailsUrl: null, RequestAccessUrl: null },
        "withdrawal preserves identity and clears public links");
    db.Transaction(() => { db.Put("tenant", tenant.ToString(), publicationOwner); return true; });
    Check(await CatalogLength() == 0, "withdrawn inactive-owner publication stays hidden after owner restoration");
    Check((await Save(admin, command with { ExpectedRevision = 5, DetailsUrl = "https://platform.fanasa.net.local/service" })).StatusCode == HttpStatusCode.OK,
        "republish lifecycle fixture before official host retirement");
    builder.Configuration["Platform:ServiceCatalog:OfficialLinkHosts:0"] = null;
    Check((await reader.GetAsync("/api/platform/published-services")).StatusCode == HttpStatusCode.ServiceUnavailable,
        "retired official host fails closed while publication is active");
    Check((await admin.GetAsync(path)).StatusCode == HttpStatusCode.OK, "administrator can inspect publication after official host retirement");
    Check((await Save(admin, command with { ExpectedRevision = 6, Published = false })).StatusCode == HttpStatusCode.OK,
        "administrator can withdraw publication after official host retirement");
    Check(await CatalogLength() == 0 && catalog.Read(product.Id) is { Revision: 7, Published: false, DetailsUrl: null },
        "retired host cannot leave the withdrawn catalog unavailable");
    Check(db.Outbox().Count(x => x.Kind == PublishedServiceCatalog.AuditKind) == 7,
        "lifecycle withdrawals retain one atomic audit per change");
    Console.WriteLine(count + " checks passed");
}
finally
{
    await app.StopAsync();
    db.Dispose();
    SqliteConnection.ClearAllPools();
    var full = Path.GetFullPath(directory);
    if (full.StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
        Directory.Delete(full, true);
}

sealed class FixtureBearer(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var authorization = Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith("Bearer ", StringComparison.Ordinal))
            return Task.FromResult(AuthenticateResult.NoResult());
        var token = authorization[7..];
        var claims = new List<Claim>
        {
            new("azp", token == "wrong-client" ? "unapproved-client" : "fanasa-unified-portal-service"),
            new("scope", token == "wrong-scope" ? "platform.registry" : "platform.catalog")
        };
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (token != "missing-exp")
            claims.Add(new("exp", (token == "expired" ? now - 1 : now + 300).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        if (token == "duplicate-exp") claims.Add(new("exp", (now + 300).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        if (token == "future-nbf") claims.Add(new("nbf", (now + 60).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        if (token == "conflicting-client-id") claims.Add(new("client_id", "unapproved-client"));
        if (token == "inactive-token") claims.Add(new("active", "false"));
        if (token != "missing-sub") claims.Add(new("sub", "synthetic-reader"));
        if (token == "duplicate-client") claims.Add(new("azp", "unapproved-client"));
        var user = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(user, Scheme.Name)));
    }
}
