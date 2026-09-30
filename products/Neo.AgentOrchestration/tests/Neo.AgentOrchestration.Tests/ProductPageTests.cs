using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Neo.AgentOrchestration.Web;
using Xunit;
namespace Neo.AgentOrchestration.Tests;

public sealed class ProductPageTests
{
    [Fact]
    public async Task Introduction_is_public_static_and_does_not_fetch_private_work()
    {
        await using var host = new WebApplicationFactory<WebHost>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.ConfigureServices(s => s.AddScoped(_ => new OrchestrationClient(
                new HttpClient(new ForbiddenApiHandler()) { BaseAddress = new Uri("http://localhost") })));
        });
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.GetAsync("/product", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Contains("Seeking The Best", html);
        Assert.Contains("نمونهٔ نمایشی", html);
        Assert.Contains("در نقشهٔ راه", html);
        Assert.Contains("product.css?v=", html);
        Assert.Single(Regex.Matches(html, "<h1\\b"));
        Assert.DoesNotContain("data-item-id", html);
        Assert.DoesNotContain("<form", html);
        var workspace = await client.GetAsync("/Workspace", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, workspace.StatusCode);
        Assert.Contains("/Login", workspace.Headers.Location!.OriginalString);
    }
    private sealed class ForbiddenApiHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new InvalidOperationException("Public introduction must not call the private API.");
    }
}
