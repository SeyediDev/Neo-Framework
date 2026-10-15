using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fanasa.UnifiedPortal.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// Test-assembly-only synthetic transport and identities. No central grants or login endpoints.
static class CatalogHttpChecks
{
    public static async Task<int> RunAsync(PlatformOrganizationView organization, PlatformProductView permitted, PublishedService service)
    {
        var checks = 0;
        void Check(bool valid, string message) { if (!valid) throw new Exception(message); Console.WriteLine("PASS local HTTP: " + message); checks++; }
        var contentRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Fanasa.UnifiedPortal.Web"));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ApplicationName = typeof(PortalHost).Assembly.GetName().Name, ContentRootPath = contentRoot, EnvironmentName = "Development"
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddRazorPages().AddApplicationPart(typeof(PortalHost).Assembly);
        builder.Services.AddAuthorization();
        builder.Services.AddSingleton<IPlatformCatalogClient>(new WorkspaceSource(new(CatalogStatus.Ready, [organization], [permitted], organization.Id)));
        var denied = service with { ProductId = Guid.NewGuid(), Key = "cloud", DisplayName = "ابر سازمانی", Center = "fanasa.cloud" };
        builder.Services.AddSingleton<IServiceDiscoveryClient>(new DiscoverySource(new(CatalogStatus.Ready, [service, denied])));
        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote))
            { context.Response.StatusCode = 403; return; }
            if (context.Request.Query["synthetic"] != "guest")
                context.User = new(new ClaimsIdentity([new Claim("sub", "synthetic-catalog-reader")], "TestOnly"));
            await next();
        });
        app.UseStaticFiles();
        app.UseRouting();
        app.UseAuthorization();
        app.MapRazorPages();
        await app.StartAsync();
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(60) };
            var html = await client.GetStringAsync("/");
            var permittedCard = Regex.Match(html, "<section class=\"service-card\" data-service-id=\"" + service.ProductId + "\">(?<card>[\\s\\S]*?)</section>").Groups["card"].Value;
            var deniedCard = Regex.Match(html, "<section class=\"service-card\" data-service-id=\"" + denied.ProductId + "\">(?<card>[\\s\\S]*?)</section>").Groups["card"].Value;
            Check(permittedCard.Contains("class=\"service-launch\"") && permittedCard.Contains("href=\"" + permitted.Url + "\""), "server-rendered granted service has launch");
            Check(deniedCard.Length > 0 && !deniedCard.Contains("service-launch") && deniedCard.Contains("service-request"), "server-rendered ungranted service has request, no launch");
            Check(html.Contains("data-center=\"fanasa.cloud\"") && html.Contains("data-services=\"fanasa.cloud\""), "cloud service has the official center mount");
            using var signedIn = await client.GetAsync("/?handler=Workspace&tenantId=" + organization.Id);
            var signedInJson = await signedIn.Content.ReadAsStringAsync();
            using var signedInDocument = JsonDocument.Parse(signedInJson);
            var signedInCard = signedInDocument.RootElement.GetProperty("products")[0];
            Check(signedIn.Headers.CacheControl?.NoStore == true && signedInCard.EnumerateObject().Select(x => x.Name).ToHashSet()
                .SetEquals(["id", "key", "displayName", "center", "url"]), "authenticated HTTP workspace returns only no-store browser card fields");
            Check(!signedInJson.Contains("private-repository") && !signedInJson.Contains("private-owner") && !signedInJson.Contains("do-not-expose")
                && signedInCard.GetProperty("url").GetString() == permitted.Url,
                "real loopback HTTP serialization excludes private product metadata while preserving authorized launch");
            using var response = await client.GetAsync("/Index?handler=Workspace&synthetic=guest");
            var wire = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
            Check(response.Headers.CacheControl?.NoStore == true && wire.GetProperty("organizations").GetArrayLength() == 0, "anonymous HTTP projection has no private organizations and is no-store");
            var services = wire.GetProperty("serviceCatalog").GetProperty("services");
            Check(services.GetArrayLength() == 2 && services.EnumerateArray().All(x => x.GetProperty("launchUrl").ValueKind == JsonValueKind.Null), "anonymous AJAX sees both published services without any launch URL");
            using var tampered = JsonDocument.Parse(await client.GetStringAsync("/?handler=Workspace&tenantId=" + Guid.NewGuid()));
            Check(tampered.RootElement.GetProperty("serviceCatalog").GetProperty("services").EnumerateArray().All(x => x.GetProperty("launchUrl").ValueKind == JsonValueKind.Null), "tampered consumer cannot get a launch URL over HTTP");
            Check((await client.GetStringAsync("/service-catalog.js")).Contains("FanasaServiceCatalog"), "catalog script is actually served");
        }
        finally { await app.StopAsync(); }
        return checks;
    }
}
