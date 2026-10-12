using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Fanasa.UnifiedPortal.Web;
using Fanasa.UnifiedPortal.Web.Pages;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

var checks = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); Console.WriteLine("PASS " + message); checks++; }
var org = new PlatformOrganizationView(Guid.NewGuid(), "مصرف‌کننده", "consumer");
var otherOrg = Guid.NewGuid();
var service = new PublishedService(Guid.NewGuid(), "work", "مدیریت کار", "fanasa.rayan", "توضیح خدمت",
    "https://platform.test/services/work", "https://platform.test/request/work", true);
var allowed = new PlatformProductView(service.ProductId, service.Key, service.DisplayName, "app", service.Center,
    "https://work.test/", "private-repository", Guid.NewGuid(), "private-owner", new() { ["secret"] = "do-not-expose" });
var discovery = new ServiceDiscovery(CatalogStatus.Ready, [service]);
var guest = ServiceCatalogPresentation.Create(discovery, CatalogStatus.SignInRequired, null, []);
Check(guest.Services.Count == 1 && guest.Services[0].LaunchUrl is null && guest.Services[0].RequestAccessUrl is not null, "guest sees published description/request but no launch");
var denied = ServiceCatalogPresentation.Create(discovery, CatalogStatus.Ready, org.Id, []);
Check(denied.Services[0].AccessState == "requestRequired" && denied.Services[0].LaunchUrl is null, "no grant keeps catalog service visible without launch");
var permitted = ServiceCatalogPresentation.Create(discovery, CatalogStatus.Ready, org.Id, [allowed]);
Check(permitted.Services[0].AccessState == "allowed" && permitted.Services[0].LaunchUrl == allowed.Url, "verified cross-owner consumer can launch");
Check(!JsonSerializer.Serialize(permitted).Contains("private-") && !JsonSerializer.Serialize(permitted).Contains("do-not-expose"), "projection does not leak internal product fields");
foreach (var state in new[] { CatalogStatus.Forbidden, CatalogStatus.Unavailable, CatalogStatus.NotConfigured, CatalogStatus.SignInRequired })
    Check(ServiceCatalogPresentation.Create(discovery, state, org.Id, [allowed]).Services[0].LaunchUrl is null, state + " cannot reuse launch grants");
Check(ServiceCatalogPresentation.Create(discovery, CatalogStatus.Ready, null, [allowed]).Services[0].LaunchUrl is null, "unselected consumer never launches");
Check(ServiceCatalogPresentation.Create(discovery, CatalogStatus.Ready, org.Id, [allowed with { Id = Guid.NewGuid() }]).Services[0].LaunchUrl is null, "matching name/key alone is not a grant");
Check(ServiceCatalogPresentation.Create(discovery, CatalogStatus.Ready, org.Id, [allowed with { Center = "fanasa.dana" }]).Services[0].LaunchUrl is null, "mismatched central identity cannot launch");
Check(ServiceCatalogPresentation.Create(discovery with { Services = [service with { Published = false }] }, CatalogStatus.Ready, org.Id, [allowed]).Services.Count == 0, "unpublished services hidden even when permitted");
Check(ServiceCatalogCenters.All.Single(x => x.AssetKey == "fanasa-cloud").Id == "fanasa.cloud" && ServiceCatalogCenters.All.Count == 14, "fourteen official centers use the real cloud audience");
var unsafeProjection = ServiceCatalogPresentation.Create(new(CatalogStatus.Ready, [service with { DetailsUrl = "javascript:alert(1)", RequestAccessUrl = "https://user:pass@platform.test/" }]), CatalogStatus.Ready, org.Id, [allowed with { Url = "javascript:alert(2)" }]);
Check(unsafeProjection.Services[0].DetailsUrl is null && unsafeProjection.Services[0].RequestAccessUrl is null && unsafeProjection.Services[0].LaunchUrl is null, "all action URLs reject unsafe schemes/credentials");

var settings = new Dictionary<string, string?>
{
    ["PlatformControlCenter:BaseUrl"] = "https://platform.test",
    ["Authentication:Authority"] = "https://identity.test/realms/fanasa",
    ["PlatformControlCenter:ClientId"] = "portal", ["PlatformControlCenter:ClientSecret"] = "test-only",
    ["PlatformControlCenter:DiscoveryPath"] = "/api/platform/service-catalog"
};
var requests = new List<(string Url, string? Bearer, string Body)>();
ServiceDiscoveryClient Reader(Func<HttpRequestMessage, Task<HttpResponseMessage>> handler, Dictionary<string, string?>? config = null)
    => new(new Clients(new Handler(handler)), new ConfigurationBuilder().AddInMemoryCollection(config ?? settings).Build(), NullLogger<ServiceDiscoveryClient>.Instance);
HttpResponseMessage Json(object data) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(data, new JsonSerializerOptions(JsonSerializerDefaults.Web)), System.Text.Encoding.UTF8, "application/json") };
HttpResponseMessage Raw(string data) => new(HttpStatusCode.OK) { Content = new StringContent(data, System.Text.Encoding.UTF8, "application/json") };
var reader = Reader(async request =>
{
    requests.Add((request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), request.Content is null ? "" : await request.Content.ReadAsStringAsync()));
    return request.Method == HttpMethod.Post ? Json(new { access_token = "test-only-token" }) : Json(new[] { service, service with { ProductId = Guid.NewGuid(), Key = "draft", Published = false } });
});
var result = await reader.GetAsync(default);
Check(result.Status == CatalogStatus.Ready && result.Services.Count == 1, "adapter only exposes explicitly published metadata");
Check(requests[0].Body.Contains("scope=platform.catalog") && requests[1].Bearer == "Bearer test-only-token", "adapter uses limited service credentials");
Check(requests[1].Url == "https://platform.test/api/platform/service-catalog" && !requests[1].Url.Contains("subject") && !requests[1].Url.Contains("tenant"), "discovery does not submit a guessed viewer or tenant");
var calls = 0;
foreach (var path in new string?[] { null, "//evil.test/api", "/api/platform/../products", "/api/platform/%2e%2e/products", "/api/platform/catalog?subject=other" })
{
    var config = new Dictionary<string, string?>(settings) { ["PlatformControlCenter:DiscoveryPath"] = path };
    Check((await Reader(_ => { calls++; return Task.FromResult(Json(new { })); }, config).GetAsync(default)).Status == CatalogStatus.NotConfigured, "unapproved/invalid path stays disconnected: " + path);
}
Check(calls == 0, "invalid configuration makes no network calls");
foreach (var raw in new[] { "null", "{}", "[{}]", "not-json", JsonSerializer.Serialize(new[] { service, service }, new JsonSerializerOptions(JsonSerializerDefaults.Web)) })
{
    result = await Reader(request => Task.FromResult(request.Method == HttpMethod.Post ? Json(new { access_token = "test" }) : Raw(raw))).GetAsync(default);
    Check(result.Status == CatalogStatus.Unavailable && result.Services.Count == 0, "malformed discovery is not a successful empty list");
}
result = await Reader(request => Task.FromResult(request.Method == HttpMethod.Post ? Json(new { access_token = "test" }) : Json(Array.Empty<PublishedService>()))).GetAsync(default);
Check(result.Status == CatalogStatus.Ready && result.Services.Count == 0, "valid empty published catalog is distinct from outage");
result = await Reader(request => Task.FromResult(request.Method == HttpMethod.Post ? Json(new { access_token = "test" }) : new HttpResponseMessage(HttpStatusCode.Forbidden))).GetAsync(default);
Check(result.Status == CatalogStatus.Forbidden && result.Services.Count == 0, "discovery read denial does not expose metadata");
result = await Reader(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest))).GetAsync(default);
Check(result.Status == CatalogStatus.NotConfigured, "rejected scope is configuration error");
var unsafeService = service with { DetailsUrl = "data:text/html,test", RequestAccessUrl = "https://u:p@platform.test/" };
result = await Reader(request => Task.FromResult(request.Method == HttpMethod.Post ? Json(new { access_token = "test" }) : Json(new[] { unsafeService }))).GetAsync(default);
Check(result.Services.Single().DetailsUrl is null && result.Services.Single().RequestAccessUrl is null, "transport sanitizes metadata links");
result = await Reader(request => Task.FromResult(request.Method == HttpMethod.Post ? Json(new { access_token = "test" }) : Json(new[] { service with { Center = "fanasa.unknown" } }))).GetAsync(default);
Check(result.Status == CatalogStatus.Unavailable, "unknown capability center fails closed");
result = await Reader(request => Task.FromResult(request.Method == HttpMethod.Post ? Json(new { access_token = "test" }) : Raw(new string('x', 2 * 1024 * 1024 + 1)))).GetAsync(default);
Check(result.Status == CatalogStatus.Unavailable && result.Services.Count == 0, "oversized metadata response is rejected, not silently truncated");
result = await Reader(request => Task.FromResult(request.Method == HttpMethod.Post ? Json(new { access_token = "test" })
    : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("[]", System.Text.Encoding.UTF8, "text/html") })).GetAsync(default);
Check(result.Status == CatalogStatus.Unavailable, "HTML sign-in response is not a successful JSON catalog");
using (var canceled = new CancellationTokenSource())
{
    canceled.Cancel();
    try { await Reader(_ => throw new OperationCanceledException(canceled.Token)).GetAsync(canceled.Token); throw new Exception("Expected caller cancellation"); }
    catch (OperationCanceledException) { Check(true, "caller cancellation propagates"); }
}

IndexModel Page(PlatformWorkspace workspace, bool signedIn)
{
    var page = new IndexModel(new WorkspaceSource(workspace), new DiscoverySource(discovery)) { PageContext = new() { HttpContext = new DefaultHttpContext() } };
    if (signedIn) page.HttpContext.User = new(new ClaimsIdentity([new Claim("sub", "test-subject")], "Test"));
    return page;
}
var page = Page(new(CatalogStatus.Ready, [org], [allowed], org.Id), true);
await page.OnGetAsync(org.Id, default);
Check(page.ServiceCatalog.Services.Single().LaunchUrl == allowed.Url && page.Response.Headers.CacheControl == "no-store", "page joins metadata with its verified workspace and does not cache grants");
page = Page(new(CatalogStatus.Ready, [org], [allowed], otherOrg), true);
await page.OnGetAsync(org.Id, default);
Check(page.ServiceCatalog.Services.Single().LaunchUrl is null && page.Products.Count == 0, "mismatched consumer envelope never enables catalog launch");
page = Page(new(CatalogStatus.Ready, [org], [allowed], org.Id), true);
await page.OnGetAsync(Guid.NewGuid(), default);
Check(page.ServiceCatalog.Services.Single().LaunchUrl is null, "tampered tenant retains metadata but not launch");
page = Page(new(CatalogStatus.Ready, [org], [allowed], org.Id), false);
await page.OnGetAsync(null, default);
Check(page.ServiceCatalog.Services.Count == 1 && page.ServiceCatalog.Services.Single().LaunchUrl is null && page.Organizations.Count == 0, "anonymous page does not expose private organization grants");
if (args.Contains("--http")) checks += await CatalogHttpChecks.RunAsync(org, allowed, service);
Console.WriteLine($"{checks} service-discovery checks passed; API transport is stubbed, not deployed central acceptance.");

sealed class Clients(HttpMessageHandler handler) : IHttpClientFactory { public HttpClient CreateClient(string name) => new(handler, false); }
sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> handle) : HttpMessageHandler
{ protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => handle(request); }
sealed class DiscoverySource(ServiceDiscovery discovery) : IServiceDiscoveryClient
{ public Task<ServiceDiscovery> GetAsync(CancellationToken cancellationToken) => Task.FromResult(discovery); }
sealed class WorkspaceSource(PlatformWorkspace workspace) : IPlatformCatalogClient
{ public Task<PlatformWorkspace> GetWorkspaceAsync(string subject, CancellationToken cancellationToken, Guid? organizationId = null) => Task.FromResult(workspace); }
