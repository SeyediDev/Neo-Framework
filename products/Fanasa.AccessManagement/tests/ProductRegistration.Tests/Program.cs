using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fanasa.AccessManagement.Web.Pages;
using Fanasa.AccessManagement.Web.Platform;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Data.Sqlite;

var checks = 0;
void Check(bool valid, string name) { if (!valid) throw new InvalidOperationException(name); checks++; Console.WriteLine("PASS " + name); }
var root = Path.Combine(Path.GetTempPath(), "fanasa-registration-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    EnvironmentName = "Development", ContentRootPath = root,
    ApplicationName = typeof(ProductRegistrationModel).Assembly.FullName
});
builder.Configuration["Platform:RegistryPath"] = Path.Combine(root, "registry.db");
builder.WebHost.UseUrls("http://127.0.0.1:0"); builder.Logging.ClearProviders();
var registry = new PlatformRegistry(builder.Configuration);
builder.Services.AddSingleton(registry);
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(root, "keys")));
builder.Services.AddRazorPages().AddApplicationPart(typeof(ProductRegistrationModel).Assembly);
builder.Services.AddAntiforgery();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
{
    o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
    o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
});
builder.Services.AddAuthorization(o => o.AddPolicy("PlatformAdmin", p => p.RequireAuthenticatedUser().RequireClaim("permission", "platform.admin")));
await using var app = builder.Build(); app.UseRouting(); app.UseAuthentication(); app.UseAuthorization();
// Disposable test host only: this code is not referenced by the shipped Web host.
app.MapGet("/test/login/{role}", async (string role, HttpContext context) =>
{
    var claims = new List<Claim> { new("sub", "synthetic-" + role) };
    if (role is "admin" or "other-admin") claims.Add(new("permission", "platform.admin"));
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)));
    return Results.Ok();
}).AllowAnonymous();
app.MapRazorPages(); await app.StartAsync();
var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
HttpClient Browser() => new(new HttpClientHandler { CookieContainer = new(), AllowAutoRedirect = false }) { BaseAddress = new Uri(address) };
Dictionary<string, string> Fields(string html) => Regex.Matches(html, "<input\\b[^>]*name=\"([^\"]+)\"[^>]*value=\"([^\"]*)\"[^>]*>", RegexOptions.IgnoreCase)
    .ToDictionary(m => WebUtility.HtmlDecode(m.Groups[1].Value), m => WebUtility.HtmlDecode(m.Groups[2].Value));
async Task<string> Token(HttpClient client) => Fields(await client.GetStringAsync("/ProductRegistration"))["__RequestVerificationToken"];
Dictionary<string, string> Input(string key, string token) => new()
{
    ["Input.Key"] = key, ["Input.DisplayName"] = "سامانه نمونه", ["Input.Audience"] = "synthetic-system",
    ["Input.Center"] = "fanasa.rayan", ["Input.TenantId"] = registry.Tenants().Single().Id.ToString(),
    ["Input.Owner"] = "platform", ["Input.Url"] = "https://sample.fanasa.net.local/",
    ["Input.BoardUrl"] = "https://sample.fanasa.net.local/boards/1", ["__RequestVerificationToken"] = token
};
async Task<HttpResponseMessage> Post(HttpClient client, string handler, Dictionary<string, string> fields) =>
    await client.PostAsync("/ProductRegistration?handler=" + handler, new FormUrlEncodedContent(fields));
using var anonymous = Browser(); using var member = Browser(); using var admin = Browser(); using var otherAdmin = Browser();
await member.GetAsync("/test/login/member"); await admin.GetAsync("/test/login/admin"); await otherAdmin.GetAsync("/test/login/other-admin");
try
{
    Check((await anonymous.GetAsync("/ProductRegistration")).StatusCode == HttpStatusCode.Unauthorized, "anonymous GET denied");
    Check((await member.GetAsync("/ProductRegistration")).StatusCode == HttpStatusCode.Forbidden, "member GET denied");
    Check((await anonymous.PostAsync("/ProductRegistration?handler=Register", new FormUrlEncodedContent([]))).StatusCode == HttpStatusCode.Unauthorized, "anonymous POST denied");
    Check((await member.PostAsync("/ProductRegistration?handler=Register", new FormUrlEncodedContent([]))).StatusCode == HttpStatusCode.Forbidden, "member POST denied");
    var valid = Input("sample-system", await Token(admin));
    var noCsrf = new Dictionary<string, string>(valid); noCsrf.Remove("__RequestVerificationToken");
    Check((await Post(admin, "Preview", noCsrf)).StatusCode == HttpStatusCode.BadRequest, "CSRF required for preview");
    Check((await Post(admin, "Register", noCsrf)).StatusCode == HttpStatusCode.BadRequest, "CSRF required for registration");
    Check((await Post(admin, "Register", valid)).StatusCode == HttpStatusCode.BadRequest, "preview required before registration");
    Check(registry.Products().Length == 0, "rejected requests do not mutate registry");
    using var preview = await Post(admin, "Preview", valid);
    Check(preview.StatusCode == HttpStatusCode.OK, "valid preview rendered");
    Check(registry.Products().Length == 0, "preview remains read-only");
    var fields = Fields(await preview.Content.ReadAsStringAsync());
    Check(fields.ContainsKey("PreviewReceipt") && fields.ContainsKey("__RequestVerificationToken"), "preview supplies protected receipt and CSRF");
    var changed = new Dictionary<string, string>(fields) { ["Input.DisplayName"] = "changed" };
    Check((await Post(admin, "Register", changed)).StatusCode == HttpStatusCode.BadRequest, "preview field tampering rejected");
    var invalidReceipt = new Dictionary<string, string>(fields) { ["PreviewReceipt"] = "not-authentic" };
    Check((await Post(admin, "Register", invalidReceipt)).StatusCode == HttpStatusCode.BadRequest, "invalid receipt rejected");
    var wrongActor = new Dictionary<string, string>(fields) { ["__RequestVerificationToken"] = await Token(otherAdmin) };
    Check((await Post(otherAdmin, "Register", wrongActor)).StatusCode == HttpStatusCode.BadRequest, "receipt cannot be used by another administrator");
    var snapshot = new ProductRegistrationInput { Key = valid["Input.Key"], DisplayName = valid["Input.DisplayName"],
        Audience = valid["Input.Audience"], Center = valid["Input.Center"], TenantId = Guid.Parse(valid["Input.TenantId"]),
        Owner = valid["Input.Owner"], Url = valid["Input.Url"], BoardUrl = valid["Input.BoardUrl"] };
    var expiredReceipt = app.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector(ProductRegistrationModel.ReceiptPurpose)
        .Protect(JsonSerializer.Serialize(new { Actor = "synthetic-admin", Fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(snapshot)))),
            Expires = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds() }));
    var expired = new Dictionary<string, string>(fields) { ["PreviewReceipt"] = expiredReceipt };
    Check((await Post(admin, "Register", expired)).StatusCode == HttpStatusCode.BadRequest, "expired preview receipt rejected");
    Check(registry.Products().Length == 0 && registry.Grants().Length == 0, "tampering grants no access or product");
    Check((await Post(admin, "Register", fields)).StatusCode == HttpStatusCode.Redirect, "confirmed registration redirects to receipt");
    var created = registry.Products().Single();
    Check(created.Attributes?["entrypoint.kanban.url"] == valid["Input.BoardUrl"], "board entrypoint retained on the same product");
    Check(registry.Grants().Length == 0, "registration does not grant user permissions");
    Check((await Post(admin, "Register", fields)).StatusCode == HttpStatusCode.Conflict, "repeated confirmation does not overwrite existing product");
    Check(JsonSerializer.Serialize(registry.Products().Single()) == JsonSerializer.Serialize(created), "duplicate preserves product ID and metadata");
    var restarted = new PlatformRegistry(builder.Configuration);
    Check(restarted.Products().Single().Id == created.Id, "product survives registry restart");
    using (var connection = new SqliteConnection("Data Source=" + Path.Combine(root, "registry.db")))
    {
        connection.Open(); using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM platform_audit WHERE operation='product.register' AND actor='synthetic-admin'";
        Check(Convert.ToInt64(command.ExecuteScalar()) == 1, "registration audit records the authenticated actor once");
    }
    foreach (var (field, value) in new[]
    {
        ("Input.Url", "http://sample.fanasa.net.local/"), ("Input.Url", "https://public.example/"),
        ("Input.Url", "https://user:secret@sample.fanasa.net.local/"), ("Input.Url", "https://sample.fanasa.net.local/?token=secret"),
        ("Input.BoardUrl", "https://other.fanasa.net.local/boards/1"), ("Input.Center", "invented-center"),
        ("Input.TenantId", Guid.NewGuid().ToString()), ("Input.Key", "developer-reserved"), ("Input.DisplayName", "")
    })
    {
        var rejected = Input("invalid-system", await Token(admin)); rejected[field] = value;
        Check((await Post(admin, "Preview", rejected)).StatusCode == HttpStatusCode.BadRequest, "invalid field rejected: " + field + "/" + value);
    }
    var optional = Input("no-board-system", await Token(admin)); optional.Remove("Input.BoardUrl");
    Check((await Post(admin, "Preview", optional)).StatusCode == HttpStatusCode.OK, "board URL is optional");
    var absent = new Dictionary<string, string> { ["__RequestVerificationToken"] = await Token(admin) };
    Check((await Post(admin, "Preview", absent)).StatusCode == HttpStatusCode.BadRequest, "missing fields return validation, not server error");
    var simultaneous = await Task.WhenAll(Enumerable.Range(0, 2).Select(i => Task.Run(() =>
    {
        try { registry.Create(created with { Id = Guid.Empty, Key = "concurrent-system", DisplayName = "candidate-" + i }, "synthetic-admin"); return true; }
        catch (InvalidOperationException) { return false; }
    })));
    Check(simultaneous.Count(x => x) == 1 && registry.Products().Count(x => x.Key == "concurrent-system") == 1, "concurrent creates yield one product without overwriting");
    using (var connection = new SqliteConnection("Data Source=" + Path.Combine(root, "registry.db")))
    {
        connection.Open(); using var command = connection.CreateCommand();
        command.CommandText = "CREATE TRIGGER reject_registration_audit BEFORE INSERT ON platform_audit BEGIN SELECT RAISE(ABORT, 'synthetic audit failure'); END";
        command.ExecuteNonQuery();
        try { registry.Create(created with { Id = Guid.Empty, Key = "rollback-system" }, "synthetic-admin"); throw new Exception("audit failure should abort creation"); }
        catch (SqliteException) { Check(!registry.Products().Any(x => x.Key == "rollback-system"), "audit failure rolls back product creation"); }
    }
    Console.WriteLine($"{checks} checks passed");
}
finally
{
    await app.StopAsync(); SqliteConnection.ClearAllPools();
    if (Path.GetFullPath(root).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase))
        Directory.Delete(root, true);
}
