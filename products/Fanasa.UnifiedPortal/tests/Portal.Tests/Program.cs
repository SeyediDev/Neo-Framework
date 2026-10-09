using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Fanasa.UnifiedPortal.Web;
using Fanasa.UnifiedPortal.Web.Pages;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.DependencyInjection;

var checks = 0;
void Check(bool valid, string name) { if (!valid) throw new Exception(name); Console.WriteLine("PASS " + name); checks++; }
var first = new PlatformOrganizationView(Guid.NewGuid(), "سازمان اول", "one");
var second = new PlatformOrganizationView(Guid.NewGuid(), "سازمان دوم", "two");
PlatformProductView Product(Guid tenant) => new(Guid.NewGuid(), "product", "سامانه", "app", "fanasa.rayan", "https://example.com/", null, tenant, "owner", null);
var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    ["PlatformControlCenter:BaseUrl"] = "https://registry.test",
    ["PlatformControlCenter:Authority"] = "", ["Authentication:Authority"] = "https://identity.test/realms/fanasa",
    ["PlatformControlCenter:ClientId"] = "portal", ["PlatformControlCenter:ClientSecret"] = "test-only"
}).Build();
var requests = new List<(string Url, string? Authorization, string Body)>();
var handler = new StubHandler(async request =>
{
    requests.Add((request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), request.Content is null ? "" : await request.Content.ReadAsStringAsync()));
    return request.RequestUri.AbsolutePath switch
    {
        "/realms/fanasa/protocol/openid-connect/token" => Json(new { access_token = "test-token" }),
        "/api/platform/memberships" => Json(new[] { first, second }),
        "/api/platform/products" => Json(new[] { Product(first.Id), Product(second.Id), Product(Guid.NewGuid()) }),
        _ => new(HttpStatusCode.NotFound)
    };
});
var client = new PlatformCatalogClient(new StubClients(handler), settings, NullLogger<PlatformCatalogClient>.Instance);
var workspace = await client.GetWorkspaceAsync("subject&other=value", default);
Check(workspace.Status == CatalogStatus.Ready && workspace.Organizations.Count == 2, "membership API supplies organization options");
Check(workspace.Products.Count == 2, "products outside authorized memberships are removed");
Check(requests[0].Url.StartsWith("https://identity.test/realms/fanasa/"), "blank service authority falls back to login authority");
Check(requests[0].Body.Contains("scope=platform.registry"), "service requests registry scope");
Check(requests.Skip(1).All(x => x.Authorization == "Bearer test-token"), "access_token wire field mapped for both authorized requests");
Check(requests.Skip(1).All(x => x.Url.Contains("subject%26other%3Dvalue")), "subject query is escaped");
Check(IndexModel.SelectOrganization([first], null) == first.Id, "single organization selected automatically");
Check(IndexModel.SelectOrganization([first, second], null) is null, "multiple organizations require a choice");
Check(IndexModel.SelectOrganization([first], second.Id) is null, "unavailable or revoked organization cannot be selected");
Check(IndexModel.SafeProductUrl("javascript:alert(1)") is null && IndexModel.SafeProductUrl("https://user:password@example.com") is null, "unsafe product links rejected");
Check(IndexModel.SafeProductUrl("https://example.com/app") is not null, "canonical HTTPS product link allowed");
var model = new IndexModel(new WorkspaceStub(workspace)) { PageContext = new() { HttpContext = new DefaultHttpContext() } };
model.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "existing-cookie-subject")], "Cookies"));
await model.OnGetAsync(first.Id, default);
Check(model.Products.Count == 1 && model.Products.All(p => p.TenantId == first.Id), "existing mapped cookie subject works and products remain tenant scoped");
Check(model.Response.Headers.CacheControl == "no-store", "personal workspace is not cached");
await model.OnGetAsync(Guid.NewGuid(), default);
Check(model.ActiveOrganizationId is null && model.Products.Count == 0, "tampered organization hides all links");
var anonymous = new IndexModel(new WorkspaceStub(workspace)) { PageContext = new() { HttpContext = new DefaultHttpContext() } };
await anonymous.OnGetAsync(null, default);
Check(anonymous.Status == CatalogStatus.SignInRequired && anonymous.Organizations.Count == 0, "anonymous visitor receives sign-in state without tenant information");
var forbidden = new PlatformCatalogClient(new StubClients(new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized)))), settings, NullLogger<PlatformCatalogClient>.Instance);
Check((await forbidden.GetWorkspaceAsync("subject", default)).Status == CatalogStatus.Forbidden, "service 401 is distinguishable from empty membership");
var invalidScope = new PlatformCatalogClient(new StubClients(new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)))), settings, NullLogger<PlatformCatalogClient>.Instance);
Check((await invalidScope.GetWorkspaceAsync("subject", default)).Status == CatalogStatus.NotConfigured, "rejected OAuth scope is a configuration state");
var offline = new PlatformCatalogClient(new StubClients(new StubHandler(_ => throw new HttpRequestException("offline"))), settings, NullLogger<PlatformCatalogClient>.Instance);
Check((await offline.GetWorkspaceAsync("subject", default)).Status == CatalogStatus.Unavailable, "network outage becomes retryable state");
var missing = new PlatformCatalogClient(new StubClients(handler), new ConfigurationBuilder().Build(), NullLogger<PlatformCatalogClient>.Instance);
Check((await missing.GetWorkspaceAsync("subject", default)).Status == CatalogStatus.NotConfigured, "missing configuration is not reported as no membership");
Check(anonymous.Presentation.CountLabel == "ورود لازم است" && anonymous.Presentation.NeedsSignIn, "anonymous workspace does not claim zero products");
Check(model.Presentation.CountLabel == "انتخاب سازمان", "invalid organization asks for selection rather than claiming zero products");
foreach (var failure in new[] { CatalogStatus.NotConfigured, CatalogStatus.Forbidden, CatalogStatus.Unavailable })
{
    var failedModel = new IndexModel(new WorkspaceStub(new(failure, [], []))) { PageContext = new() { HttpContext = new DefaultHttpContext() } };
    failedModel.HttpContext.User = model.User;
    await failedModel.OnGetAsync(null, default);
    Check(!failedModel.Presentation.CountLabel.Contains("سامانه") && failedModel.Presentation.OrganizationPlaceholder == "فهرست سازمان‌ها دریافت نشده است",
        failure + " has unknown count and unavailable list, not empty membership");
}
Check(WorkspacePresentation.Create(CatalogStatus.Ready, 0, false, 0).CountLabel == "بدون سازمان", "successful empty membership is distinct from connection failure");
Check(WorkspacePresentation.Create(CatalogStatus.Ready, 1, true, 0).CountLabel == "۰ سامانه", "zero products is shown only for a successfully loaded selected organization");
Check(WorkspacePresentation.Create(CatalogStatus.Ready, 2, false, 0).EmptyTitle == "سازمان خود را انتخاب کنید", "multi-organization empty state guides selection");
var partialFailure = new IndexModel(new WorkspaceStub(new(CatalogStatus.Unavailable, [first], [Product(first.Id)]))) { PageContext = new() { HttpContext = new DefaultHttpContext() } };
partialFailure.HttpContext.User = model.User;
var workspaceResult = (Microsoft.AspNetCore.Mvc.JsonResult)await partialFailure.OnGetWorkspaceAsync(first.Id, default);
Check(partialFailure.Products.Count == 0 && partialFailure.Presentation.CountLabel == "دریافت ناموفق", "failed workspace never exposes stale product links even with retained organizations");
var workspaceJson = JsonSerializer.Serialize(workspaceResult.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
using var workspaceDocument = JsonDocument.Parse(workspaceJson);
Check(workspaceDocument.RootElement.GetProperty("presentation").GetProperty("countLabel").GetString() == partialFailure.Presentation.CountLabel,
    "AJAX and initial HTML share the same state presentation");
Console.WriteLine($"{checks} checks passed.");

// Explicit local-only preview of authorized UI states. Never part of the web product.
if (args.Contains("--preview"))
{
    var contentRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Fanasa.UnifiedPortal.Web"));
    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        ContentRootPath = contentRoot, ApplicationName = typeof(PortalHost).Assembly.GetName().Name, EnvironmentName = "Development"
    });
    builder.Services.AddSingleton<IPlatformCatalogClient>(new WorkspaceStub(workspace));
    builder.Services.AddRazorPages().AddApplicationPart(typeof(PortalHost).Assembly);
    var app = builder.Build();
    app.UseStaticFiles();
    app.Use(async (context, next) =>
    {
        context.User = new ClaimsPrincipal(new ClaimsIdentity([
            new Claim("sub", "preview-user"), new Claim("preferred_username", "پیش‌نمایش آزمایشی")
        ], "Preview"));
        await next();
    });
    app.MapRazorPages();
    var previewPort = int.TryParse(Environment.GetEnvironmentVariable("PORTAL_PREVIEW_PORT"), out var port) && port is > 1024 and <= 65535 ? port : 5183;
    await app.RunAsync($"http://127.0.0.1:{previewPort}");
}

static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)), System.Text.Encoding.UTF8, "application/json") };
sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
}
sealed class StubClients(HttpMessageHandler handler) : IHttpClientFactory
{
    public HttpClient CreateClient(string name) => new(handler, false);
}
sealed class WorkspaceStub(PlatformWorkspace workspace) : IPlatformCatalogClient
{
    public Task<PlatformWorkspace> GetWorkspaceAsync(string subject, CancellationToken cancellationToken) => Task.FromResult(workspace);
}
