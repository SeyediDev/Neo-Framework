using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Neo.AgentOrchestration.Application.Work;
using Neo.AgentOrchestration.Infrastructure.Persistence;
using Neo.AgentOrchestration.Contracts;
using Xunit;
using static Neo.AgentOrchestration.Tests.SqlPersistenceTests;

namespace Neo.AgentOrchestration.Tests;

public sealed class SqlApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Authenticated_http_configuration_and_approval_persist_in_the_independent_database()
    {
        var f = await Fixture.Create();
        // Exercise the host's real AddOrchestrationSql registration and catalog
        // guard, not only an injected store implementation.
        await using var api = new ApiFixture(clock: f.Clock, sql: f.Connection); using var client = api.Client(f.Scope);
        using (var services = api.Factory.Services.CreateScope())
            Assert.IsType<SqlWorkspaceWorkStore>(services.ServiceProvider.GetRequiredService<IWorkspaceWorkStore>());
        var root = ApiFixture.Root(f.Scope);
        var catalog = await ApiFixture.Post<WorkspaceCatalog>(client, root + "/projects", new CreateProjectRequest("http", "HTTP project"));
        var project = Assert.Single(catalog.Projects, x => x.Key == "HTTP");
        catalog = await Put<WorkspaceCatalog>(client, root + "/projects/" + project.Id, new RenameProjectRequest("Renamed project"));
        Assert.Equal("Renamed project", catalog.Projects.Single(x => x.Id == project.Id).Name);
        catalog = await ApiFixture.Post<WorkspaceCatalog>(client, root + "/roles", new CreateRoleProfileRequest("reviewer", "Reviewer", "Review API"));
        var role = Assert.Single(catalog.Roles, x => x.Key == "REVIEWER");
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(root + "/roles", new CreateRoleProfileRequest("reviewer", "Duplicate"), Ct)).StatusCode);
        catalog = await Put<WorkspaceCatalog>(client, root + "/roles/" + role.Id, new UpdateRoleProfileRequest("Reviewer updated", "Verified scope"));
        catalog = await ApiFixture.Post<WorkspaceCatalog>(client, root + "/agents", new CreateAgentProfileRequest(role.Id, "review-agent", "Review agent", "fake",
            Instructions: "Review the task evidence.", SkillPath: ".agents/skills/review/SKILL.md"));
        var agent = Assert.Single(catalog.Agents, x => x.Key == "REVIEW-AGENT");
        catalog = await Put<WorkspaceCatalog>(client, root + "/agents/" + agent.Id, new UpdateAgentProfileRequest("Agent updated", "fake", null, "Updated instructions", null));
        catalog = await Put<WorkspaceCatalog>(client, root + $"/agents/{agent.Id}/enabled", new SetProfileEnabledRequest(false));
        Assert.False(catalog.Agents.Single(x => x.Id == agent.Id).IsEnabled);
        catalog = await Put<WorkspaceCatalog>(client, root + $"/agents/{agent.Id}/enabled", new SetProfileEnabledRequest(true));
        catalog = await Put<WorkspaceCatalog>(client, root + $"/roles/{role.Id}/enabled", new SetProfileEnabledRequest(false));
        Assert.False(catalog.Roles.Single(x => x.Id == role.Id).IsEnabled);
        catalog = await Put<WorkspaceCatalog>(client, root + $"/roles/{role.Id}/enabled", new SetProfileEnabledRequest(true));
        catalog = await ApiFixture.Post<WorkspaceCatalog>(client, root + "/workflows", new CreateWorkflowRequest(project.Id, "main", "Main flow"));
        var flow = Assert.Single(catalog.Workflows, x => x.ProjectId == project.Id);
        var transitionBody = new ConfigureTransitionRequest(flow.Version, f.Role.Id, "Review", role.Id, "Ready", RequireApproval: true);
        catalog = await Put<WorkspaceCatalog>(client, root + $"/workflows/{flow.Id}/transitions/review", transitionBody);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(root + $"/workflows/{flow.Id}/transitions/review", transitionBody, Ct)).StatusCode);
        flow = catalog.Workflows.Single(x => x.Id == flow.Id); var transition = Assert.Single(flow.Transitions);
        var item = await ApiFixture.Post<WorkItemDetails>(client, root + "/items", new CreateWorkItemRequest(project.Id, "http-task", "HTTP task", "api"), HttpStatusCode.Created);
        var itemUrl = root + "/items/" + item.Item.Id;
        item = await ApiFixture.Post<WorkItemDetails>(client, itemUrl + "/status", new ChangeStatusRequest(item.Item.Version, "Ready"));
        item = await ApiFixture.Post<WorkItemDetails>(client, itemUrl + "/claim", new ClaimWorkItemRequest(item.Item.Version, f.Role.Id, "develop"));
        f.Clock.Advance(15);
        item = await ApiFixture.Post<WorkItemDetails>(client, itemUrl + "/status", new ChangeStatusRequest(item.Item.Version, "Review"));
        var previewBody = new PreviewWorkflowRequest(item.Item.Id, transition.Id);
        var previewUrl = root + $"/workflows/{flow.Id}/preview";
        var preview = await ApiFixture.Post<WorkflowPlan>(client, previewUrl, previewBody);
        Assert.False(preview.GatesSatisfied); Assert.Contains("approval-required", preview.UnmetConditions);
        var approvalBody = new ApproveWorkflowRequest(flow.Version, item.Item.Id, item.Item.Version, transition.Id, true, "Verified API evidence");
        var approvalsUrl = root + $"/workflows/{flow.Id}/approvals";
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(approvalsUrl, approvalBody, Ct)).StatusCode);
        using var reviewer = api.Client(f.Scope, ["read", "approve"], "reviewer-subject");
        var approval = await ApiFixture.Post<WorkflowApprovalView>(reviewer, approvalsUrl, approvalBody);
        Assert.Equal("reviewer-subject", approval.ReviewerAgentId);
        preview = await ApiFixture.Post<WorkflowPlan>(client, previewUrl, previewBody);
        Assert.True(preview.GatesSatisfied); Assert.Equal(agent.Id, preview.AgentProfileId);
        Assert.Single((await client.GetFromJsonAsync<WorkflowApprovalView[]>(approvalsUrl + "?workItemId=" + item.Item.Id, Ct))!);
        await using (var db = f.Factory.CreateDbContext())
        {
            Assert.Equal(1, await db.Approvals.CountAsync(x => x.WorkItemId == item.Item.Id, Ct));
            Assert.Equal(2, await db.Roles.CountAsync(x => x.WorkspaceId == f.Scope.WorkspaceId, Ct));
            Assert.Equal(0, await db.OutboxMessages.CountAsync(x => x.TenantKey == f.Scope.WorkspaceId.ToString("N"), Ct));
        }
        // Preview/approval never dispatches. A changed item invalidates its approval.
        item = await ApiFixture.Post<WorkItemDetails>(client, itemUrl + "/logs", new AppendLogRequest(item.Item.Version, "Changed after review"));
        Assert.False((await ApiFixture.Post<WorkflowPlan>(client, previewUrl, previewBody)).GatesSatisfied);
        Assert.Equal(HttpStatusCode.Conflict, (await reviewer.PostAsJsonAsync(approvalsUrl, approvalBody, Ct)).StatusCode);
        var saved = await client.GetFromJsonAsync<WorkItemDetails>(itemUrl, Ct);
        Assert.Equal(15, saved!.Item.ElapsedSeconds); Assert.Equal("Review", saved.Item.Status);
    }

    [Fact]
    public async Task Sql_api_cannot_read_or_reference_resources_in_another_authorized_scope()
    {
        var a = await Fixture.Create(); var b = await Fixture.Create(); var foreign = await b.CreateItem("foreign");
        await using var api = new ApiFixture(a.Store, a.Clock); using var client = api.Client(a.Scope);
        var root = ApiFixture.Root(a.Scope);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(root + "/items/" + foreign.Id, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(root + "/items?projectId=" + b.Project.Id, Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(root + "/items",
            new CreateWorkItemRequest(b.Project.Id, "cross", "Cross", "api"), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(root + "/items",
            new CreateWorkItemRequest(a.Project.Id, "cross-parent", "Cross parent", "api", ParentWorkItemId: foreign.Id), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync(root + "/agents",
            new CreateAgentProfileRequest(b.Role.Id, "cross-agent", "Cross agent", "fake"), Ct)).StatusCode);
        await using var db = a.Factory.CreateDbContext();
        Assert.False(await db.WorkItems.AnyAsync(x => x.WorkspaceId == a.Scope.WorkspaceId, Ct));
        Assert.False(await db.Agents.AnyAsync(x => x.WorkspaceId == a.Scope.WorkspaceId, Ct));
    }

    private static async Task<T> Put<T>(HttpClient client, string url, object body)
    {
        using var response = await client.PutAsJsonAsync(url, body, Ct); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<T>(Ct))!;
    }
}
