using System.Net;
using Fanasa.UnifiedPortal.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

// Only ephemeral loopback servers and synthetic credentials. Never contacts an IdP.
static class TransportRedirectChecks
{
    public static async Task<int> RunAsync(PlatformOrganizationView organization, PlatformProductView permitted, PublishedService service)
    {
        var checks = 0;
        void Check(bool valid, string message)
        {
            if (!valid) throw new Exception(message);
            Console.WriteLine("PASS redirect HTTP: " + message);
            checks++;
        }
        var sinkCalls = 0;
        var sinkSawSecret = false;
        var sinkSawSubject = false;
        async Task Sink(HttpContext context)
        {
            Interlocked.Increment(ref sinkCalls);
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync(context.RequestAborted);
            sinkSawSecret |= body.Contains("client_secret=redirect-test-only", StringComparison.Ordinal);
            sinkSawSubject |= context.Request.Query.ContainsKey("subject");
            await context.Response.WriteAsJsonAsync(new { access_token = "synthetic-sink-token" });
        }
        await using var sink = Host();
        sink.Map("/{**path}", Sink);
        await sink.StartAsync();
        await using var upstream = Host();
        var stage = "none";
        var statusCode = StatusCodes.Status307TemporaryRedirect;
        var destination = Address(sink) + "/redirect-target";
        var productCalls = 0;
        void Redirect(HttpContext context)
        {
            context.Response.StatusCode = statusCode;
            context.Response.Headers.Location = destination;
        }
        upstream.MapPost("/realms/fanasa/protocol/openid-connect/token", async context =>
        {
            if (stage == "token") Redirect(context);
            else await context.Response.WriteAsJsonAsync(new { access_token = "synthetic-token" });
        });
        upstream.MapGet("/api/platform/published-services", async context =>
        {
            if (stage == "discovery") Redirect(context);
            else await context.Response.WriteAsJsonAsync(new[] { service });
        });
        upstream.MapGet("/api/platform/application-tenants", async context =>
        {
            if (stage == "memberships") Redirect(context);
            else await context.Response.WriteAsJsonAsync(new[] { organization });
        });
        upstream.MapGet("/api/platform/products", async context =>
        {
            productCalls++;
            if (stage == "products") Redirect(context);
            else await context.Response.WriteAsJsonAsync(new[] { permitted });
        });
        upstream.Map("/redirect-target", Sink);
        await upstream.StartAsync();
        try
        {
            var origin = Address(upstream);
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PlatformControlCenter:BaseUrl"] = origin,
                ["PlatformControlCenter:Authority"] = origin + "/realms/fanasa",
                ["PlatformControlCenter:ClientId"] = "synthetic-client",
                ["PlatformControlCenter:ClientSecret"] = "redirect-test-only",
                ["PlatformControlCenter:DiscoveryPath"] = "/api/platform/published-services"
            }).Build();
            var registrations = new ServiceCollection();
            registrations.AddLogging();
            registrations.AddFanasaPlatformHttpClients();
            using var provider = registrations.BuildServiceProvider();
            var clients = provider.GetRequiredService<IHttpClientFactory>();
            var discovery = new ServiceDiscoveryClient(clients, configuration, NullLogger<ServiceDiscoveryClient>.Instance);
            var workspace = new PlatformCatalogClient(clients, configuration, NullLogger<PlatformCatalogClient>.Instance);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var ct = deadline.Token;

            // Control: prove this installed runtime replays a secret with the old default.
            stage = "token";
            using (var vulnerable = new HttpClient { Timeout = TimeSpan.FromSeconds(5) })
            using (var response = await vulnerable.PostAsync(origin + "/realms/fanasa/protocol/openid-connect/token",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["client_secret"] = "redirect-test-only" }), ct))
                Check(response.IsSuccessStatusCode && sinkCalls == 1 && sinkSawSecret,
                    "control reproduces default 307 POST credential replay to a second loopback origin");
            sinkCalls = 0;
            sinkSawSecret = sinkSawSubject = false;

            foreach (var code in new[] { StatusCodes.Status307TemporaryRedirect, StatusCodes.Status308PermanentRedirect })
            foreach (var sameOrigin in new[] { false, true })
            {
                statusCode = code;
                destination = (sameOrigin ? origin : Address(sink)) + "/redirect-target";
                var description = code + (sameOrigin ? " same-origin" : " cross-origin");
                stage = "token";
                var publicResult = await discovery.GetAsync(ct);
                var privateResult = await workspace.GetWorkspaceAsync("synthetic-subject", ct, organization.Id);
                Check(publicResult.Status == CatalogStatus.Unavailable && publicResult.Services.Count == 0
                    && privateResult.Status == CatalogStatus.Unavailable && privateResult.Organizations.Count == 0
                    && privateResult.Products.Count == 0 && sinkCalls == 0,
                    description + " token redirect fails closed for discovery and workspace; no secret replay");

                stage = "discovery";
                publicResult = await discovery.GetAsync(ct);
                Check(publicResult.Status == CatalogStatus.Unavailable && publicResult.Services.Count == 0 && sinkCalls == 0,
                    description + " discovery redirect cannot supply catalog metadata");

                stage = "memberships";
                var before = productCalls;
                privateResult = await workspace.GetWorkspaceAsync("synthetic-subject", ct, organization.Id);
                Check(privateResult.Status == CatalogStatus.Unavailable && privateResult.Organizations.Count == 0
                    && privateResult.Products.Count == 0 && productCalls == before && sinkCalls == 0,
                    description + " membership redirect does not forward the subject or query products");

                stage = "products";
                privateResult = await workspace.GetWorkspaceAsync("synthetic-subject", ct, organization.Id);
                Check(privateResult.Status == CatalogStatus.Unavailable && privateResult.Organizations.Count == 1
                    && privateResult.Products.Count == 0 && privateResult.ProductOrganizationId is null && sinkCalls == 0,
                    description + " product redirect preserves verified membership but never launch permission");
            }
            Check(!sinkSawSecret && !sinkSawSubject && sinkCalls == 0,
                "production registrations never send credentials or viewer queries to either redirect target");
            stage = "none";
            var readyDiscovery = await discovery.GetAsync(ct);
            var readyWorkspace = await workspace.GetWorkspaceAsync("synthetic-subject", ct, organization.Id);
            Check(readyDiscovery.Status == CatalogStatus.Ready && readyDiscovery.Services.Count == 1
                && readyWorkspace.Status == CatalogStatus.Ready && readyWorkspace.Products.Count == 1
                && readyWorkspace.ProductOrganizationId == organization.Id,
                "ordinary direct JSON responses still produce catalog and verified grants");
            foreach (var name in new[] { "platform-control-token", "platform-control-catalog", "platform-control-discovery" })
            {
                using var client = clients.CreateClient(name);
                Check(client.Timeout == TimeSpan.FromSeconds(name == "platform-control-discovery" ? 30 : 15),
                    name + " keeps its existing timeout");
            }
        }
        finally
        {
            await upstream.StopAsync();
            await sink.StopAsync();
        }
        return checks;
    }

    private static WebApplication Host()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.Use(async (context, next) =>
        {
            if (context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote))
                context.Response.StatusCode = 403;
            else await next();
        });
        return app;
    }

    private static string Address(WebApplication app) => app.Services.GetRequiredService<IServer>()
        .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
}
