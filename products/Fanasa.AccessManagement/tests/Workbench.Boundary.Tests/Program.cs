using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Fanasa.AccessManagement.Web.Api;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

// Real Kestrel, cookie authorization and antiforgery; the fixed loopback transport is an isolated double.
var checks = 0;
void Check(bool valid, string label)
{
    if (!valid) throw new InvalidOperationException("FAILED: " + label);
    checks++;
    Console.WriteLine("PASS: " + label);
}

var directory = Path.Combine(Path.GetTempPath(), "fanasa-workbench-boundary-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(directory, "Workbench"));
await File.WriteAllTextAsync(Path.Combine(directory, "Workbench", "index.html"),
    "<!doctype html><html lang=\"fa\" dir=\"rtl\"><head><title>Laboratory</title><link href=\"assets/vazirmatn.css\"><link href=\"styles.css\"><script src=\"workbench.js\"></script></head><body><a href=\"#contracts\">synthetic</a><svg><use href=\"#i-check\"></use></svg></body></html>");
var transport = new LaboratoryTransport();
using var upstream = new HttpClient(transport);
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    EnvironmentName = "Development", ContentRootPath = directory,
    ApplicationName = typeof(WorkbenchApi).Assembly.FullName
});
builder.WebHost.UseUrls("http://127.0.0.1:0");
builder.Logging.ClearProviders();
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(directory, "keys")));
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(options =>
{
    options.Events.OnRedirectToLogin = context => { context.Response.StatusCode = 401; return Task.CompletedTask; };
    options.Events.OnRedirectToAccessDenied = context => { context.Response.StatusCode = 403; return Task.CompletedTask; };
});
builder.Services.AddAuthorization(options => options.AddPolicy("PlatformAdmin", policy =>
    policy.RequireAuthenticatedUser().RequireClaim("permission", "platform.admin")));
builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
builder.Services.AddControllers().AddApplicationPart(typeof(WorkbenchApi).Assembly)
    .ConfigureApplicationPartManager(manager => manager.FeatureProviders.Add(new WorkbenchOnlyControllers()))
    .AddControllersAsServices();
builder.Services.AddRazorPages().AddApplicationPart(typeof(WorkbenchApi).Assembly);
builder.Services.Replace(ServiceDescriptor.Transient(typeof(WorkbenchApi), services =>
    typeof(WorkbenchApi).GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
        [typeof(ILogger<WorkbenchApi>), typeof(HttpClient)], null)!.Invoke(
        [services.GetRequiredService<ILogger<WorkbenchApi>>(), upstream])));
await using var app = builder.Build();
app.UseRouting(); app.UseAuthentication(); app.UseAuthorization();
app.MapGet("/test/login/{role}", async (string role, HttpContext context) =>
{
    var claims = new List<Claim> { new("sub", "synthetic-operator") };
    if (role == "admin") claims.Add(new("permission", "platform.admin"));
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
        new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
    return Results.Ok();
}).AllowAnonymous();
app.MapGet("/test/token", (HttpContext context, IAntiforgery antiforgery) =>
    Results.Json(new { token = antiforgery.GetAndStoreTokens(context).RequestToken })).RequireAuthorization();
app.MapControllers(); app.MapRazorPages();
await app.StartAsync();
var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
HttpClient Browser() => new(new HttpClientHandler { CookieContainer = new CookieContainer(), AllowAutoRedirect = false })
    { BaseAddress = new Uri(address) };
using var anonymous = Browser(); using var member = Browser(); using var admin = Browser();
await member.GetAsync("/test/login/member"); await admin.GetAsync("/test/login/admin");
try
{
    using (var response = await anonymous.GetAsync("/api/workbench"))
        Check(response.StatusCode == HttpStatusCode.Unauthorized, "anonymous cannot read the laboratory");
    using (var response = await member.GetAsync("/api/workbench"))
        Check(response.StatusCode == HttpStatusCode.Forbidden, "authenticated non-admin cannot read the laboratory");
    using (var response = await anonymous.GetAsync("/api/workbench/readiness"))
        Check(response.StatusCode == HttpStatusCode.Unauthorized, "database readiness remains protected from anonymous reads");
    using (var response = await member.GetAsync("/api/workbench/readiness"))
        Check(response.StatusCode == HttpStatusCode.Forbidden, "database readiness requires platform administration");
    using (var response = await anonymous.GetAsync("/PolicyWorkbench"))
        Check(response.StatusCode == HttpStatusCode.Unauthorized, "anonymous cannot read the Razor shell");
    using (var response = await member.GetAsync("/PolicyWorkbench"))
        Check(response.StatusCode == HttpStatusCode.Forbidden, "non-admin cannot read the Razor shell");
    using (var response = await admin.GetAsync("/PolicyWorkbench"))
    {
        var html = await response.Content.ReadAsStringAsync();
        Check(response.StatusCode == HttpStatusCode.OK && html.Contains("href=\"/workbench/assets/vazirmatn.css\"")
            && html.Contains("href=\"/workbench/styles.css\"") && html.Contains("src=\"/workbench/workbench.js\"")
            && html.Contains("name=\"wb-csrf\""), "authenticated shell supplies absolute assets and antiforgery token");
        Check(!html.Contains("<base ") && html.Contains("href=\"#contracts\"") && html.Contains("href=\"#i-check\""),
            "hash navigation and SVG fragments retain the authenticated document URL");
        Check(response.Headers.CacheControl?.NoStore == true, "shell carrying request token is never cached");
    }
    using (var response = await admin.PostAsJsonAsync("/api/workbench/draft", new { author = "lab-author" }))
        Check(response.StatusCode == HttpStatusCode.BadRequest && transport.Calls == 0,
            "missing antiforgery token is rejected before the engine");
    var tokenData = await admin.GetFromJsonAsync<JsonElement>("/test/token");
    var token = tokenData.GetProperty("token").GetString()!;
    async Task<HttpResponseMessage> Post(string operation, string body, string? contentType = "application/json")
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/workbench/" + operation);
        request.Headers.Add("X-CSRF-TOKEN", token);
        request.Headers.Add("Authorization", "Bearer attacker-supplied");
        request.Headers.Add("X-Workbench-Operator", "forged");
        request.Headers.Add("Origin", "https://untrusted.invalid");
        request.Content = new StringContent(body, Encoding.UTF8, contentType);
        return await admin.SendAsync(request);
    }
    using (var response = await Post("draft", "{\"author\":\"lab-author\"}"))
        Check(response.StatusCode == HttpStatusCode.OK && transport.Calls == 1,
            "admin with valid antiforgery token reaches the engine");
    Check(transport.LastUri == "http://127.0.0.1:4187/api/workbench/draft"
        && transport.LastOrigin == "http://127.0.0.1:4186", "destination and lab origin are fixed server values");
    Check(!transport.HasAuthorization && !transport.HasCookie && !transport.HasOperator
        && !transport.HasAntiforgery, "authentication and arbitrary caller headers never reach the engine");
    using (var response = await Post("invented", "{}"))
        Check(response.StatusCode == HttpStatusCode.BadRequest && transport.Calls == 1,
            "unlisted operation cannot widen the proxy");
    using (var response = await admin.GetAsync("/api/workbench?url=https://untrusted.invalid"))
        Check(response.StatusCode == HttpStatusCode.BadRequest && transport.Calls == 1,
            "caller query cannot choose an upstream");
    using (var response = await Post("draft", "{}", "text/plain"))
        Check(response.StatusCode == HttpStatusCode.UnsupportedMediaType && transport.Calls == 1,
            "mutation requires JSON");
    using (var response = await Post("draft", new string('x', WorkbenchApi.MaximumRequestBytes + 1)))
        Check(response.StatusCode == HttpStatusCode.RequestEntityTooLarge && transport.Calls == 1,
            "oversized request is rejected before the engine");
    using (var response = await admin.GetAsync("/api/workbench/readiness"))
        Check(response.StatusCode == HttpStatusCode.OK && transport.LastUri == "http://127.0.0.1:4187/api/workbench/readiness",
            "administrator can retrieve server database and runtime readiness");
    var callsBeforeValidation = transport.Calls;
    using (var response = await admin.PostAsJsonAsync("/api/workbench/validate", new { }))
        Check(response.StatusCode == HttpStatusCode.BadRequest && transport.Calls == callsBeforeValidation,
            "configuration validation also requires antiforgery before reaching the engine");
    using (var response = await Post("validate", "{}"))
        Check(response.StatusCode == HttpStatusCode.OK && transport.LastUri == "http://127.0.0.1:4187/api/workbench/validate"
            && !transport.HasCookie && !transport.HasAuthorization, "configuration validation uses the bounded fixed authenticated seam");
    transport.Unavailable = true;
    using (var response = await admin.GetAsync("/api/workbench"))
        Check(response.StatusCode == HttpStatusCode.ServiceUnavailable
            && (await response.Content.ReadAsStringAsync()).Contains("workbench-unavailable"),
            "unavailable engine returns an explicit 503");
    transport.Unavailable = false; transport.Status = HttpStatusCode.Conflict;
    using (var response = await Post("bind", "{}"))
        Check(response.StatusCode == HttpStatusCode.Conflict, "engine conflict status is preserved");
    transport.Status = HttpStatusCode.Redirect;
    using (var response = await admin.GetAsync("/api/workbench"))
        Check(response.StatusCode == HttpStatusCode.ServiceUnavailable,
            "upstream redirect cannot redirect the authenticated user or widen trust");
    Console.WriteLine($"RESULT: {checks} passed, 0 failed");
}
finally
{
    await app.StopAsync();
    // Only this explicitly created synthetic directory is removed.
    if (!Path.GetFullPath(directory).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException("Unexpected synthetic test directory.");
    Directory.Delete(directory, true);
}

sealed class LaboratoryTransport : HttpMessageHandler
{
    public int Calls { get; private set; }
    public bool Unavailable { get; set; }
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
    public string? LastUri { get; private set; }
    public string? LastOrigin { get; private set; }
    public bool HasAuthorization { get; private set; }
    public bool HasCookie { get; private set; }
    public bool HasOperator { get; private set; }
    public bool HasAntiforgery { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++; LastUri = request.RequestUri?.AbsoluteUri;
        LastOrigin = request.Headers.TryGetValues("Origin", out var origin) ? origin.Single() : null;
        HasAuthorization = request.Headers.Contains("Authorization"); HasCookie = request.Headers.Contains("Cookie");
        HasOperator = request.Headers.Contains("X-Workbench-Operator"); HasAntiforgery = request.Headers.Contains("X-CSRF-TOKEN");
        if (Unavailable) throw new HttpRequestException("isolated transport unavailable");
        return Task.FromResult(new HttpResponseMessage(Status)
            { Content = new StringContent("{\"laboratory\":true}", Encoding.UTF8, "application/json") });
    }
}

sealed class WorkbenchOnlyControllers : IApplicationFeatureProvider<ControllerFeature>
{
    public void PopulateFeature(IEnumerable<ApplicationPart> parts, ControllerFeature feature)
    {
        foreach (var controller in feature.Controllers.Where(type => type.AsType() != typeof(WorkbenchApi)).ToArray())
            feature.Controllers.Remove(controller);
    }
}
