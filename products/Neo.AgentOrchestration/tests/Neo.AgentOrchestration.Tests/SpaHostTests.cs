using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Neo.AgentOrchestration.Web;
using Xunit;
namespace Neo.AgentOrchestration.Tests;
public sealed class SpaHostTests
{
    [Fact]
    public async Task Async_unauthenticated_requests_return_401_not_identity_redirect()
    {
        await using var app = new WebApplicationFactory<WebHost>().WithWebHostBuilder(b => b.UseEnvironment("Testing"));
        using var client = app.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new("https://localhost") });
        client.DefaultRequestHeaders.Add("X-Neo-Navigation", "1");
        var denied = await client.GetAsync("/Workspace", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        Assert.Null(denied.Headers.Location);
        var page = await client.GetAsync("/product", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("workbench.js?v=", html);
        Assert.Contains("id=\"spa-message\"", html);
        var csp = Assert.Single(page.Headers.GetValues("Content-Security-Policy"));
        Assert.Contains("script-src 'self'", csp);
        Assert.Contains("connect-src 'self'", csp);
        Assert.DoesNotContain("unsafe-inline", csp);
        Assert.DoesNotContain("unsafe-eval", csp);
    }
}
