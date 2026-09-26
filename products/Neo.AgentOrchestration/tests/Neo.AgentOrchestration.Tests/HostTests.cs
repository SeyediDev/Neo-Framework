using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Neo.AgentOrchestration.Contracts;
using Neo.AgentOrchestration.Web;
using Xunit;

namespace Neo.AgentOrchestration.Tests;

public sealed class HostTests
{
    [Theory]
    [InlineData("Testing")]
    [InlineData("Development")]
    public async Task Api_starts_independently_and_reports_legacy_retention(string environment)
    {
        await using var app = new WebApplicationFactory<ApiHost>().WithWebHostBuilder(b => b.UseEnvironment(environment));
        using var client = app.CreateClient();
        var info = await client.GetFromJsonAsync<ProductInfo>("/api/orchestration/v1/system", TestContext.Current.CancellationToken);
        Assert.NotNull(info);
        Assert.Equal("Foundation", info.Stage);
        Assert.True(info.LegacyRetained);
        Assert.True(info.CleanupRequiresApproval);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live", TestContext.Current.CancellationToken)).StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Web_renders_with_a_separate_api_and_handles_unavailability(bool available)
    {
        await using var api = new WebApplicationFactory<ApiHost>().WithWebHostBuilder(b => b.UseEnvironment("Testing"));
        await using var web = new WebApplicationFactory<WebHost>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.ConfigureServices(s => s.AddScoped(_ => new OrchestrationClient(available
                ? api.CreateClient() : new HttpClient(new UnavailableHandler()) { BaseAddress = new Uri("http://localhost") })));
        });
        using var client = web.CreateClient();
        var response = await client.GetAsync("/", TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        Assert.Contains("lang=\"fa\" dir=\"rtl\"", html);
        Assert.Contains("Hyper", html);
        Assert.Contains(available ? "Foundation" : "role=\"status\"", html);
    }

    private sealed class UnavailableHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
    }
}
