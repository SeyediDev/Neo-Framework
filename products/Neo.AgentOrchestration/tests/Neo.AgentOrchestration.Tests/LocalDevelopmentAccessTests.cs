using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Neo.AgentOrchestration.Api;
using Neo.AgentOrchestration.Web;
using Xunit;

namespace Neo.AgentOrchestration.Tests;

public sealed class LocalDevelopmentAccessTests
{
    [Theory]
    [InlineData("Development", "true", "127.0.0.1", false, true)]
    [InlineData("Development", "true", "::1", false, true)]
    [InlineData("Development", "true", "192.0.2.1", false, false)]
    [InlineData("Development", "false", "127.0.0.1", false, false)]
    [InlineData("Production", "true", "127.0.0.1", false, false)]
    [InlineData("Testing", "true", "127.0.0.1", false, false)]
    [InlineData("Development", "true", null, false, false)]
    [InlineData("Development", "true", "127.0.0.1", true, false)]
    public async Task Web_and_api_require_explicit_development_and_direct_loopback(
        string environment, string enabled, string? address, bool forwarded, bool allowed)
    {
        using var services = Services(environment, enabled);
        var context = Context(services, address);
        if (forwarded) context.Request.Headers["Forwarded"] = "for=192.0.2.1";
        Assert.Equal(allowed, LocalDevelopmentAccess.IsAllowed(context));
        Assert.Equal(allowed, WorkspaceSecurity.IsLocalDevelopment(context));
        var authorization = services.GetRequiredService<IAuthorizationService>();
        context.Request.Headers["X-Orchestration-Local"] = "true";
        Assert.Equal(allowed, (await authorization.AuthorizeAsync(context.User, context, WorkspaceSecurity.Read)).Succeeded);
        context.Request.Headers.Remove("X-Orchestration-Local");
        Assert.False((await authorization.AuthorizeAsync(context.User, context, WorkspaceSecurity.Read)).Succeeded);
    }

    [Fact]
    public async Task Unsigned_claims_never_grant_api_access()
    {
        using var services = Services("Production", "false");
        var context = Context(services, "127.0.0.1");
        var org = context.Request.RouteValues["organizationId"];
        var workspace = context.Request.RouteValues["workspaceId"];
        Claim[] claims = [new("sub", "agent"), new("nao_grant", $"{org}/{workspace}/read")];
        var authorization = services.GetRequiredService<IAuthorizationService>();
        Assert.False((await authorization.AuthorizeAsync(new ClaimsPrincipal(new ClaimsIdentity(claims)), context, WorkspaceSecurity.Read)).Succeeded);
        Assert.True((await authorization.AuthorizeAsync(new ClaimsPrincipal(new ClaimsIdentity(claims, "test-verified")), context, WorkspaceSecurity.Read)).Succeeded);
    }

    [Theory]
    [InlineData("http://127.0.0.1:5180", true)]
    [InlineData("https://api.example.test", false)]
    public async Task Local_client_never_forwards_local_privilege_to_remote_api(string destination, bool allowed)
    {
        using var services = Services("Development", "true");
        var context = Context(services, "127.0.0.1");
        var handler = new CaptureHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri(destination) };
        var client = new OrchestrationClient(http, new HttpContextAccessor { HttpContext = context });
        if (allowed)
        {
            var result = await client.SendAsync<Dictionary<string, bool>>(Guid.NewGuid(), Guid.NewGuid(), "items", TestContext.Current.CancellationToken);
            Assert.True(result["ok"]);
            Assert.True(handler.LocalHeader);
        }
        else
        {
            var error = await Assert.ThrowsAsync<WebApiException>(() => client.SendAsync<object>(Guid.NewGuid(), Guid.NewGuid(), "items", TestContext.Current.CancellationToken));
            Assert.Equal(401, error.Status);
            Assert.Equal(0, handler.Calls);
        }
    }

    [Theory]
    [InlineData("Development", "true", "127.0.0.1", true)]
    [InlineData("Development", "true", "192.0.2.1", false)]
    [InlineData("Production", "true", "127.0.0.1", false)]
    [InlineData("Development", "false", "127.0.0.1", false)]
    public async Task Protected_web_routes_apply_boundary_per_request(string environment, string enabled, string address, bool allowed)
    {
        await using var host = new WebApplicationFactory<WebHost>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment(environment);
            b.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> { ["NEO_LOCAL_DEVELOPMENT"] = enabled }));
            b.ConfigureServices(s => s.AddSingleton<IStartupFilter>(new RemoteAddressFilter(address)));
        });
        using var client = host.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new Uri("https://localhost") });
        client.DefaultRequestHeaders.Add("X-Orchestration-Local", "true");
        var response = await client.GetAsync("/Workspace", TestContext.Current.CancellationToken);
        Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Redirect, response.StatusCode);
        if (!allowed)
        {
            Assert.Contains("/Login", response.Headers.Location!.OriginalString);
            var board = await client.GetAsync($"/work/{Guid.NewGuid()}/{Guid.NewGuid()}", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.Redirect, board.StatusCode);
            Assert.Contains("/Login", board.Headers.Location!.OriginalString);
        }
    }

    private static ServiceProvider Services(string environment, string enabled)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestEnvironment { EnvironmentName = environment });
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["NEO_LOCAL_DEVELOPMENT"] = enabled }).Build());
        services.AddAuthentication("Cookies").AddCookie("Cookies");
        services.AddAuthorization(WorkspaceSecurity.AddPolicies);
        return services.BuildServiceProvider();
    }
    private static DefaultHttpContext Context(IServiceProvider services, string? address)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Connection.RemoteIpAddress = address is null ? null : IPAddress.Parse(address);
        context.Request.RouteValues["organizationId"] = Guid.NewGuid().ToString("D");
        context.Request.RouteValues["workspaceId"] = Guid.NewGuid().ToString("D");
        return context;
    }
    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "LocalSecurityTests";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
    private sealed class CaptureHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public bool LocalHeader { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LocalHeader = request.Headers.TryGetValues("X-Orchestration-Local", out var values) && values.Single() == "true";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { ok = true }) });
        }
    }
    private sealed class RemoteAddressFilter(string address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, continuation) => { context.Connection.RemoteIpAddress = IPAddress.Parse(address); return continuation(context); });
            next(app);
        };
    }
}
