using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Neo.AgentOrchestration.Application.Delivery;
using Neo.AgentOrchestration.Application.Work;
using Neo.AgentOrchestration.Contracts;
using Neo.AgentOrchestration.Domain.Projects;
using Neo.AgentOrchestration.Domain.Runs;
using Neo.AgentOrchestration.Domain.Templates;
using Neo.AgentOrchestration.Domain.Work;
using Neo.AgentOrchestration.Domain.Workflows;
using Neo.AgentOrchestration.Infrastructure.Delivery;
using Neo.AgentOrchestration.Infrastructure.Persistence;
using Xunit;
using Fixture = Neo.AgentOrchestration.Tests.SqlPersistenceTests.Fixture;

namespace Neo.AgentOrchestration.Tests;

public sealed class ProjectDeletionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static SqlProjectDeletionStore Store(Fixture f) => new(f.Factory, f.Clock);
    private static DeleteProjectRequest Confirm(ProjectDeletionPreview preview, bool active = false)
        => new(preview.Snapshot, preview.Key, active);

    [Fact]
    public async Task Sql_cascade_removes_the_complete_graph_but_keeps_other_projects_and_shared_configuration()
    {
        var f = await Fixture.Create(); var setup = await SqlRunTests.Configure(f);
        var runItem = await SqlRunTests.Ready(f); var run = await SqlRunTests.Start(f, setup, runItem);
        await SqlRunTests.Drain(f, runItem.Id);
        var now = f.Clock.GetUtcNow(); Guid parentId; Guid otherId; Guid templateId;
        await using (var db = f.Factory.CreateDbContext())
        {
            var workspace = await db.Workspaces.SingleAsync(x => x.Id == f.Scope.WorkspaceId, Ct);
            var other = Project.Create(workspace, "other", "Keep this project");
            var otherWork = WorkItem.Create(f.Scope, other, "keep", "Keep this work", "tests", f.Actor, now);
            var parent = WorkItem.Create(f.Scope, f.Project, "story", "Story", "tests", f.Actor, now, type: WorkItemType.UserStory);
            var child = WorkItem.Create(f.Scope, f.Project, "child", "Child", "tests", f.Actor, now, parent: parent);
            var bug = WorkItem.Create(f.Scope, f.Project, "bug", "Bug", "tests", f.Actor, now, type: WorkItemType.Bug);
            var epic = WorkItem.Create(f.Scope, f.Project, "epic", "Epic", "tests", f.Actor, now, type: WorkItemType.Epic);
            child.AddDependency(f.Scope, f.Actor, bug, [parent, child, bug, epic], now);
            child.AddEvidence(f.Scope, f.Actor, EvidenceKind.Test, "fixture-test", EvidenceOutcome.Passed, null, now);
            bug.ChangeStatus(f.Scope, f.Actor, WorkItemStatus.Ready, now);
            bug.ChangeStatus(f.Scope, f.Actor, WorkItemStatus.Blocked, now); bug.Archive(f.Scope, f.Actor, now);
            var template = ProjectTemplate.Publish(f.Scope, "template", "Keep shared template", 1,
                """{"parameters":[],"roles":[],"items":[{"key":"seed","title":"Seed","domain":"tests","type":"Task","priority":"Normal","dependsOn":[]}],"workflows":[]}""", f.Actor, now);
            db.AddRange(other, otherWork, parent, child, bug, epic, template,
                TemplateInstantiation.Create(f.Scope, template, f.Project, Guid.NewGuid(), new string('A', 64), "{}", "{}", f.Actor, now),
                ProjectRepositoryBinding.Create(f.Scope, f.Project, "github", "https://github.com/example/fixture", "example/fixture", "master", "develop", null, null, now),
                TokenUsageReport.Create(f.Scope, Guid.NewGuid(), run.Run.Id, runItem.Id, "fake", null, 10, 2, null, null, "fixture", "fixture-report", now));
            await db.SaveChangesAsync(Ct); parentId = parent.Id; otherId = other.Id; templateId = template.Id;
        }
        await f.Change(parentId, new StatusChange(WorkItemStatus.Ready)); await f.Claim(parentId);
        await using (var db = f.Factory.CreateDbContext())
        {
            var parent = await db.WorkItems.SingleAsync(x => x.Id == parentId, Ct);
            parent.RecordTokenUsage(f.Scope, f.Actor, Guid.NewGuid(), "fixture", null, "fixture-meter", 10, 5, null, null, now);
            await db.SaveChangesAsync(Ct);
        }
        await f.Change(parentId, new StatusChange(WorkItemStatus.Review));
        await using (var db = f.Factory.CreateDbContext())
        {
            var parent = await db.WorkItems.SingleAsync(x => x.Id == parentId, Ct);
            var flow = WorkflowDefinition.Create(f.Scope, f.Project, "approved", "Approved flow");
            var transition = flow.ConfigureTransition(f.Scope, "done", f.Role, WorkItemStatus.Review, null, WorkItemStatus.Done, new(RequireApproval: true));
            db.AddRange(flow, WorkflowApproval.Record(f.Scope, flow, transition.Id, parent, new("reviewer", "other-chat"), true, "Fixture review", f.Clock.GetUtcNow()));
            await db.SaveChangesAsync(Ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO nao.WorkItemOwnerHistory (Id,ProjectId,WorkItemId,SourceWorkItemId,OriginalStatus,OwnerRole,OwnerAgent,OwnerChat,CreatedAtUtc) VALUES ({Guid.NewGuid()},{f.Project.Id},{parentId},{1L},{3},{"fixture-role"},{"fixture-agent"},{"fixture-chat"},{f.Clock.GetUtcNow()})", Ct);
        }
        await new SqlDurableWorkStore(f.Factory, f.Clock).ApplyInboxAsync(f.Scope,
            new(parentId, "fixture", "receipt", "{}"), (_, _) => Task.CompletedTask, Ct);
        var store = Store(f); var preview = await store.PreviewAsync(f.Scope, f.Project.Id, Ct);
        Assert.True(preview.CanDelete); Assert.Equal(5, preview.Records["WorkItems"]);
        Assert.Equal(1, preview.Subtasks); Assert.Equal(1, preview.WorkItemTypes["UserStory"]);
        Assert.Equal(1, preview.WorkItemTypes["Bug"]); Assert.Equal(1, preview.WorkItemTypes["Epic"]);
        Assert.All(preview.Records, row => Assert.True(row.Value > 0, row.Key));
        var result = await store.DeleteAsync(f.Scope, f.Project.Id, Confirm(preview), Ct);
        Assert.Equal(preview.Records, result.DeletedRecords);
        await using var verify = f.Factory.CreateDbContext();
        Assert.False(await verify.Projects.AnyAsync(x => x.Id == f.Project.Id, Ct));
        Assert.Empty(await verify.WorkItems.Where(x => x.ProjectId == f.Project.Id).ToArrayAsync(Ct));
        Assert.Empty(await verify.AgentRuns.Where(x => x.ProjectId == f.Project.Id).ToArrayAsync(Ct));
        Assert.Empty(await verify.Deliveries.Where(x => x.ProjectId == f.Project.Id).ToArrayAsync(Ct));
        Assert.True(await verify.Projects.AnyAsync(x => x.Id == otherId, Ct));
        Assert.Single(await verify.WorkItems.Where(x => x.ProjectId == otherId).ToArrayAsync(Ct));
        Assert.True(await verify.Set<ProjectTemplate>().AnyAsync(x => x.Id == templateId, Ct));
        Assert.Equal(2, await verify.Roles.CountAsync(x => x.WorkspaceId == f.Scope.WorkspaceId, Ct));
        Assert.Equal(2, await verify.Agents.CountAsync(x => x.WorkspaceId == f.Scope.WorkspaceId, Ct));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.PreviewAsync(f.Scope, f.Project.Id, Ct));
    }

    [Fact]
    public async Task Sql_rejects_stale_snapshot_wrong_key_and_foreign_scope_without_deletion()
    {
        var f = await Fixture.Create(); var item = await f.CreateItem("item"); var store = Store(f);
        var preview = await store.PreviewAsync(f.Scope, f.Project.Id, Ct);
        await Assert.ThrowsAsync<ArgumentException>(() => store.DeleteAsync(f.Scope, f.Project.Id, Confirm(preview) with { ConfirmProjectKey = "wrong" }, Ct));
        await f.Change(item.Id, new LogChange("A change after preview"));
        await Assert.ThrowsAsync<WorkItemConflictException>(() => store.DeleteAsync(f.Scope, f.Project.Id, Confirm(preview), Ct));
        var other = await Fixture.Create();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.PreviewAsync(other.Scope, f.Project.Id, Ct));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.DeleteAsync(other.Scope, f.Project.Id, Confirm(preview), Ct));
        Assert.Equal(item.Id, (await f.Handlers.Handle(new GetWorkItem(f.Scope, item.Id), Ct)).Item.Id);
    }

    [Fact]
    public async Task Sql_active_manual_work_needs_explicit_confirmation_not_an_owner_takeover()
    {
        var f = await Fixture.Create(); var item = await SqlRunTests.Ready(f); await f.Claim(item.Id);
        var store = Store(f); var preview = await store.PreviewAsync(f.Scope, f.Project.Id, Ct);
        Assert.Equal(1, preview.ActiveManualAssignments); Assert.True(preview.CanDelete);
        await Assert.ThrowsAsync<WorkItemConflictException>(() => store.DeleteAsync(f.Scope, f.Project.Id, Confirm(preview), Ct));
        await store.DeleteAsync(f.Scope, f.Project.Id, Confirm(preview, true), Ct);
        await using var db = f.Factory.CreateDbContext(); Assert.False(await db.Projects.AnyAsync(x => x.Id == f.Project.Id, Ct));
    }

    [Fact]
    public async Task Sql_cannot_force_delete_active_runs_or_pending_delivery_even_with_manual_confirmation()
    {
        var f = await Fixture.Create(); var setup = await SqlRunTests.Configure(f); var item = await SqlRunTests.Ready(f);
        await SqlRunTests.Start(f, setup, item);
        var store = Store(f); var preview = await store.PreviewAsync(f.Scope, f.Project.Id, Ct);
        Assert.False(preview.CanDelete); Assert.Equal(1, preview.ActiveRuns); Assert.Equal(1, preview.PendingDeliveries);
        await Assert.ThrowsAsync<WorkItemConflictException>(() => store.DeleteAsync(f.Scope, f.Project.Id, Confirm(preview, true), Ct));
        await using var db = f.Factory.CreateDbContext(); Assert.True(await db.Projects.AnyAsync(x => x.Id == f.Project.Id, Ct));
    }

    [Fact]
    public async Task Sql_unhandled_foreign_key_rolls_back_all_prior_child_deletions()
    {
        var f = await Fixture.Create(); var item = await f.CreateItem("must-survive");
        var table = "DeletionGuard_" + Guid.NewGuid().ToString("N");
        await using var db = f.Factory.CreateDbContext();
        var create = "CREATE TABLE nao.[" + table + "] (Id uniqueidentifier NOT NULL PRIMARY KEY, ProjectId uniqueidentifier NOT NULL REFERENCES nao.Projects(Id))";
        await db.Database.ExecuteSqlRawAsync(create, Ct);
        try
        {
            // Identifiers cannot be parameters; the only variable identifier is
            // our source-owned GUID table, never product/user input.
            var insert = "INSERT INTO nao.[" + table + "] (Id,ProjectId) VALUES (@id,@project)";
            await db.Database.ExecuteSqlRawAsync(insert, [new Microsoft.Data.SqlClient.SqlParameter("@id", Guid.NewGuid()),
                new Microsoft.Data.SqlClient.SqlParameter("@project", f.Project.Id)], Ct);
            var store = Store(f); var preview = await store.PreviewAsync(f.Scope, f.Project.Id, Ct);
            await Assert.ThrowsAsync<Microsoft.Data.SqlClient.SqlException>(() => store.DeleteAsync(f.Scope, f.Project.Id, Confirm(preview), Ct));
            var saved = await f.Handlers.Handle(new GetWorkItem(f.Scope, item.Id), Ct);
            Assert.NotEmpty(saved.Logs); Assert.Equal(item.Id, saved.Item.Id);
        }
        finally
        {
            var drop = "DROP TABLE nao.[" + table + "]";
            await db.Database.ExecuteSqlRawAsync(drop, Ct);
        }
    }

    [Theory]
    [InlineData("read")]
    [InlineData("write")]
    [InlineData("configure")]
    [InlineData("read|write")]
    [InlineData("read|configure")]
    public async Task Http_delete_requires_read_write_and_configure_together(string grant)
    {
        using var f = new WorkFixture(); await using var api = new ApiFixture(f.Store, f.Clock);
        using var client = api.Client(f.Scope, grant.Split('|'));
        var response = await Delete(client, ApiFixture.Root(f.Scope) + $"/projects/{f.Project.Id}", new(new string('A', 64), f.Project.Key));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task Sql_http_preview_delete_and_repeat_keep_scope_and_explicit_confirmation()
    {
        var f = await Fixture.Create(); await f.CreateItem("http");
        await using var api = new ApiFixture(clock: f.Clock, sql: f.Connection); using var client = api.Client(f.Scope);
        var url = ApiFixture.Root(f.Scope) + $"/projects/{f.Project.Id}";
        var preview = (await client.GetFromJsonAsync<ProjectDeletionPreview>(url + "/deletion-preview", Ct))!;
        Assert.Equal(HttpStatusCode.BadRequest, (await Delete(client, url, new("", f.Project.Key))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await Delete(client, url, Confirm(preview))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Delete(client, url, Confirm(preview))).StatusCode);
        Assert.DoesNotContain((await client.GetFromJsonAsync<WorkspaceCatalog>(ApiFixture.Root(f.Scope) + "/catalog", Ct))!.Projects, p => p.Id == f.Project.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sql_web_confirmation_is_antiforgery_protected_and_works_with_spa(bool spa)
    {
        var f = await Fixture.Create(); var item = await f.CreateItem("web");
        await using var web = await WebFixture.Create(f);
        if (spa) web.Client.DefaultRequestHeaders.Add("X-Neo-Navigation", "1");
        var url = web.Root + $"/projects/{f.Project.Id}/delete";
        Assert.Contains(url, await web.Html(web.Root + "/manage"));
        var html = WebUtility.HtmlDecode(await web.Html(url)); Assert.Contains("حذف دائمی", html); Assert.Contains("سطل بازیافت ندارد", html);
        Assert.Equal(HttpStatusCode.BadRequest, (await web.Client.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string> { ["ConfirmProjectKey"] = f.Project.Key }), Ct)).StatusCode);
        var result = await web.Submit(url, null, new() { ["ConfirmProjectKey"] = f.Project.Key });
        Assert.Equal(HttpStatusCode.Redirect, result.StatusCode);
        var catalog = await web.ApiRead<WorkspaceCatalog>("catalog"); Assert.DoesNotContain(catalog.Projects, p => p.Id == f.Project.Id);
        Assert.DoesNotContain($"data-item-id=\"{item.Id}\"", await web.Html(web.Root));
    }

    private static Task<HttpResponseMessage> Delete(HttpClient client, string url, DeleteProjectRequest body)
        => client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, url) { Content = JsonContent.Create(body) }, Ct);
}
