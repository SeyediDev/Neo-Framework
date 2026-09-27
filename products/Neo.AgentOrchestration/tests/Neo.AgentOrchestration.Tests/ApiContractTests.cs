using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Neo.AgentOrchestration.Application.Work;
using Neo.AgentOrchestration.Contracts;
using Neo.AgentOrchestration.Domain.Projects;
using Xunit;

namespace Neo.AgentOrchestration.Tests;

public sealed class ApiContractTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("missing")]
    [InlineData("expired")]
    [InlineData("signature")]
    [InlineData("issuer")]
    [InlineData("audience")]
    public async Task Bearer_validation_rejects_invalid_tokens_before_storage(string invalid)
    {
        using var fixture = new WorkFixture();
        await using var api = new ApiFixture(fixture.Store, fixture.Clock);
        using var client = api.Client(fixture.Scope, invalid: invalid);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(ApiFixture.Root(fixture.Scope) + "/items", Ct)).StatusCode);
    }

    [Fact]
    public async Task Membership_is_an_exact_workspace_permission_grant_and_subject_cannot_be_forged()
    {
        using var fixture = new WorkFixture();
        await using var api = new ApiFixture(fixture.Store, fixture.Clock);
        var root = ApiFixture.Root(fixture.Scope);
        using var reader = api.Client(fixture.Scope, ["read"]);
        Assert.Equal(HttpStatusCode.OK, (await reader.GetAsync(root + "/items", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PostAsJsonAsync(root + "/items", new
            CreateWorkItemRequest(fixture.Project.Id, "blocked", "Blocked", "domain"), Ct)).StatusCode);
        using var writer = api.Client(fixture.Scope, ["read", "write"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await writer.PostAsJsonAsync(root + "/roles", new CreateRoleProfileRequest("blocked", "Blocked"), Ct)).StatusCode);
        var foreign = new WorkspaceScope(fixture.Scope.OrganizationId, Guid.NewGuid());
        Assert.Equal(HttpStatusCode.Forbidden, (await writer.GetAsync(ApiFixture.Root(foreign) + "/items", Ct)).StatusCode);
        using var combined = api.Client(foreign, ["read", "write"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await combined.GetAsync(root + "/items", Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await writer.PostAsJsonAsync(root + "/items", new {
            projectId = fixture.Project.Id, key = "spoof", title = "Spoof", domain = "domain", agentId = "someone-else" }, Ct)).StatusCode);
        writer.DefaultRequestHeaders.Remove("X-Orchestration-Chat");
        Assert.Equal(HttpStatusCode.BadRequest, (await writer.PostAsJsonAsync(root + "/items", new
            CreateWorkItemRequest(fixture.Project.Id, "no-chat", "No chat", "domain"), Ct)).StatusCode);
        Assert.Empty(fixture.Store.Items);
    }

    [Fact]
    public async Task Http_work_lifecycle_keeps_children_history_time_filters_and_versions()
    {
        using var f = new WorkFixture();
        await using var api = new ApiFixture(f.Store, f.Clock);
        using var client = api.Client(f.Scope);
        var root = ApiFixture.Root(f.Scope);
        var parent = await ApiFixture.Post<WorkItemDetails>(client, root + "/items", new CreateWorkItemRequest(f.Project.Id, "parent", "Parent", "orchestration"), HttpStatusCode.Created);
        var child = await ApiFixture.Post<WorkItemDetails>(client, root + "/items", new CreateWorkItemRequest(f.Project.Id, "child", "Child", "api",
            "Original task context", ParentWorkItemId: parent.Item.Id, EstimatedSeconds: 100), HttpStatusCode.Created);
        var itemUrl = root + "/items/" + child.Item.Id;
        child = await ApiFixture.Post<WorkItemDetails>(client, itemUrl + "/status", new ChangeStatusRequest(child.Item.Version, "Ready"));
        child = await ApiFixture.Post<WorkItemDetails>(client, itemUrl + "/claim", new ClaimWorkItemRequest(child.Item.Version, f.Role.Id, "develop"));
        Assert.Equal("agent-a", child.Item.OwnerAgentId);
        using var stranger = api.Client(f.Scope, subject: "different-agent");
        Assert.Equal(HttpStatusCode.Conflict, (await stranger.PostAsJsonAsync(itemUrl + "/logs", new AppendLogRequest(child.Item.Version, "Takeover"), Ct)).StatusCode);
        var stale = child.Item.Version;
        f.Clock.Advance(25);
        child = await ApiFixture.Post<WorkItemDetails>(client, itemUrl + "/time/stop", new VersionRequest(child.Item.Version));
        Assert.Equal(25, child.Item.ElapsedSeconds); Assert.Equal(25m, child.Item.BudgetUsedPercent);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(itemUrl + "/logs", new AppendLogRequest(stale, "Stale"), Ct)).StatusCode);
        child = await ApiFixture.Post<WorkItemDetails>(client, itemUrl + "/logs", new AppendLogRequest(child.Item.Version, "Verified context"));
        var sha = new string('a', 40);
        child = await ApiFixture.Post<WorkItemDetails>(client, itemUrl + "/evidence", new AddEvidenceRequest(child.Item.Version, "Commit", sha, "NotApplicable"));
        child = await ApiFixture.Post<WorkItemDetails>(client, itemUrl + "/evidence", new AddEvidenceRequest(child.Item.Version, "Test", "api-contract", "Passed", CommitSha: sha));
        var board = await client.GetFromJsonAsync<WorkBoard>(root + $"/items?projectId={f.Project.Id}&domain=api&roleId={f.Role.Id}&take=1", Ct);
        Assert.Single(board!.Items); Assert.Equal(25, board.Metrics.ElapsedSeconds); Assert.Equal(100, board.Metrics.EstimatedSeconds);
        Assert.Empty((await client.GetFromJsonAsync<WorkBoard>(root + "/items?domain=absent", Ct))!.Items);
        Assert.Empty((await client.GetFromJsonAsync<WorkBoard>(root + "/items?status=Done", Ct))!.Items);
        child = await ApiFixture.Post<WorkItemDetails>(client, itemUrl + "/status", new ChangeStatusRequest(child.Item.Version, "Review"));
        child = await ApiFixture.Post<WorkItemDetails>(client, itemUrl + "/status", new ChangeStatusRequest(child.Item.Version, "Done"));
        child = await ApiFixture.Post<WorkItemDetails>(client, itemUrl + "/archive", new VersionRequest(child.Item.Version));
        Assert.True(child.Item.IsArchived);
        Assert.Empty((await client.GetFromJsonAsync<WorkBoard>(root + "/items?domain=api", Ct))!.Items);
        Assert.Single((await client.GetFromJsonAsync<WorkBoard>(root + "/items?domain=api&includeArchived=true", Ct))!.Items);
        child = await ApiFixture.Post<WorkItemDetails>(client, itemUrl + "/restore", new VersionRequest(child.Item.Version));
        Assert.Contains(child.Logs, x => x.Message == "Verified context" && x.AgentId == "agent-a");
        Assert.Equal("Original task context", child.Item.Description);
        Assert.Equal(2, child.Evidence.Count); Assert.Single(child.TimeEntries);
        Assert.Single((await client.GetFromJsonAsync<WorkItemDetails>(root + "/items/" + parent.Item.Id, Ct))!.Children);
    }

    [Theory]
    [InlineData("?take=201")]
    [InlineData("?skip=-1")]
    [InlineData("?status=3")]
    [InlineData("?projectId=not-a-guid")]
    public async Task Invalid_filters_return_problem_details(string query)
    {
        using var f = new WorkFixture(); await using var api = new ApiFixture(f.Store, f.Clock); using var client = api.Client(f.Scope);
        var response = await client.GetAsync(ApiFixture.Root(f.Scope) + "/items" + query, Ct);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Unconfigured_storage_fails_closed_but_public_liveness_remains_available()
    {
        await using var api = new ApiFixture(); var scope = new WorkspaceScope(Guid.NewGuid(), Guid.NewGuid());
        using var client = api.Client(scope);
        var response = await client.GetAsync(ApiFixture.Root(scope) + "/items", Ct);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains("storage-unconfigured", await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health/live", Ct)).StatusCode);
    }

    [Fact]
    public async Task Generated_openapi_describes_the_canonical_routes_and_authentication()
    {
        await using var api = new ApiFixture(); using var client = api.Client(new WorkspaceScope(Guid.NewGuid(), Guid.NewGuid()));
        using var anonymous = api.Factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/swagger/v1/swagger.json", Ct)).StatusCode);
        var response = await client.GetAsync("/swagger/v1/swagger.json", Ct); response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var root = "/api/orchestration/v1/organizations/{organizationId}/workspaces/{workspaceId}";
        var paths = document.RootElement.GetProperty("paths");
        var create = paths.GetProperty(root + "/items").GetProperty("post");
        Assert.True(create.GetProperty("responses").TryGetProperty("201", out _));
        Assert.NotEqual(0, create.GetProperty("security").GetArrayLength());
        Assert.True(paths.TryGetProperty(root + "/items/{id}/time/start", out _));
        Assert.True(paths.TryGetProperty(root + "/workflows/{id}/approvals", out _));
        Assert.True(paths.TryGetProperty(root + "/roles/{id}/enabled", out _));
        Assert.DoesNotContain(paths.EnumerateObject(), x => x.Name.Contains("monitoring", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(create.GetProperty("parameters").EnumerateArray(), x =>
            x.GetProperty("name").GetString() == "X-Orchestration-Chat" && x.GetProperty("required").GetBoolean());
        Assert.Contains(create.GetProperty("x-workspace-permissions").EnumerateArray(), x => x.GetString() == "write");
    }
}

internal sealed class ApiFixture : IAsyncDisposable
{
    private const string Issuer = "https://issuer.neo.test";
    private const string Audience = "neo-orchestration-tests";
    private readonly SymmetricSecurityKey key = new(RandomNumberGenerator.GetBytes(64));
    public WebApplicationFactory<ApiHost> Factory { get; }
    public ApiFixture(IWorkspaceWorkStore? store = null, TimeProvider? clock = null, string? sql = null, bool simulation = false)
    {
        Factory = new WebApplicationFactory<ApiHost>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?> {
                ["NEO_ORCHESTRATION_SQL"] = "", ["ConnectionStrings:Orchestration"] = sql ?? "",
                ["Orchestration:SimulationEnabled"] = simulation.ToString(),
                ["Orchestration:DatabaseName"] = "NeoAgentOrchestration_Verification", ["OpenApi:Enabled"] = "true" }));
            builder.ConfigureServices(services =>
            {
                if (store is not null) services.AddSingleton(store);
                if (clock is not null) services.AddSingleton(clock);
                // Real JwtBearer validation with an ephemeral test-only key.
                // No test authentication handler or token bypass exists in the host.
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    options.Authority = null; options.MetadataAddress = ""; options.ConfigurationManager = null;
                    options.TokenValidationParameters = new TokenValidationParameters {
                        ValidateIssuer = true, ValidIssuer = Issuer, ValidateAudience = true, ValidAudience = Audience,
                        ValidateLifetime = true, RequireExpirationTime = true, RequireSignedTokens = true,
                        ValidateIssuerSigningKey = true, IssuerSigningKey = key, ClockSkew = TimeSpan.Zero };
                });
            });
        });
    }
    public HttpClient Client(WorkspaceScope scope, string[]? grants = null, string subject = "agent-a", string? invalid = null)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Orchestration-Chat", "chat-a");
        if (invalid == "missing") return client;
        var claims = new List<Claim> { new("sub", subject) };
        claims.AddRange((grants ?? ["read", "write", "configure", "approve"]).Select(x =>
            new Claim("nao_grant", $"{scope.OrganizationId:D}/{scope.WorkspaceId:D}/{x}")));
        var token = new JwtSecurityToken(invalid == "issuer" ? "https://wrong.test" : Issuer,
            invalid == "audience" ? "wrong-audience" : Audience, claims, DateTime.UtcNow.AddMinutes(-20),
            invalid == "expired" ? DateTime.UtcNow.AddMinutes(-10) : DateTime.UtcNow.AddMinutes(10),
            new SigningCredentials(invalid == "signature" ? new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(64)) : key, SecurityAlgorithms.HmacSha256));
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }
    public static string Root(WorkspaceScope scope) => $"/api/orchestration/v1/organizations/{scope.OrganizationId:D}/workspaces/{scope.WorkspaceId:D}";
    public static async Task<T> Post<T>(HttpClient client, string url, object body, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await client.PostAsJsonAsync(url, body, TestContext.Current.CancellationToken);
        if (response.StatusCode != expected)
            Assert.Fail($"Expected HTTP {expected}, received {response.StatusCode}: {await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)}");
        Assert.Equal(expected, response.StatusCode);
        if (expected == HttpStatusCode.Created)
        {
            Assert.NotNull(response.Headers.Location);
            using var read = await client.GetAsync(response.Headers.Location, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        }
        return (await response.Content.ReadFromJsonAsync<T>(TestContext.Current.CancellationToken))!;
    }
    public ValueTask DisposeAsync() => Factory.DisposeAsync();
}
