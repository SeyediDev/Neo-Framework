using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
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
    [InlineData("8e64a895-d089-4bbe-850d-a19be7be98e8", "d1b79f17-a937-484a-981f-b30105b827ce")]
    [InlineData(null, null)]
    [InlineData("invalid", "d1b79f17-a937-484a-981f-b30105b827ce")]
    [InlineData("8e64a895-d089-4bbe-850d-a19be7be98e8", "invalid")]
    [InlineData("00000000-0000-0000-0000-000000000000", "d1b79f17-a937-484a-981f-b30105b827ce")]
    [InlineData("8e64a895-d089-4bbe-850d-a19be7be98e8", "00000000-0000-0000-0000-000000000000")]
    public async Task Root_routes_valid_defaults_or_workspace_selection_without_calling_api(
        string? organization, string? workspace)
    {
        await using var web = new WebApplicationFactory<WebHost>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OrchestrationApi:DefaultOrganizationId"] = organization,
                ["OrchestrationApi:DefaultWorkspaceId"] = workspace
            }));
            b.ConfigureServices(s => s.AddScoped(_ => new OrchestrationClient(
                new HttpClient(new UnexpectedApiHandler()) { BaseAddress = new Uri("http://localhost") })));
        });
        using var client = web.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var response = await client.GetAsync("/", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/Workspace", response.Headers.Location?.OriginalString);
        // Selecting a workspace must not bypass authentication outside local development.
        var selector = await client.GetAsync("/Workspace", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.Redirect, selector.StatusCode);
        Assert.Contains("/Login", selector.Headers.Location!.OriginalString);
    }

    private sealed class UnexpectedApiHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new InvalidOperationException("Root navigation must not call the API.");
    }
}
