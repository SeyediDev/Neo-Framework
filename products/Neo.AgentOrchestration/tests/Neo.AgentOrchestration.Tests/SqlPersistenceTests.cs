using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Neo.AgentOrchestration.Application.Work;
using Neo.AgentOrchestration.Domain.Agents;
using Neo.AgentOrchestration.Domain.Projects;
using Neo.AgentOrchestration.Domain.Work;
using Neo.AgentOrchestration.Domain.Workflows;
using Neo.AgentOrchestration.Infrastructure.Persistence;
using Xunit;

namespace Neo.AgentOrchestration.Tests;

public sealed class SqlPersistenceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Model_and_migration_have_scope_keys_concurrency_and_unique_claims()
    {
        using var db = new OrchestrationDesignFactory().CreateDbContext([]);
        var model = db.GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>().Model;
        Assert.All(model.GetEntityTypes(), x => Assert.Equal("nao", x.GetSchema()));
        var item = model.FindEntityType(typeof(WorkItem))!;
        Assert.True(item.FindProperty(nameof(WorkItem.Version))!.IsConcurrencyToken);
        Assert.True(item.FindProperty("RowVersion")!.IsConcurrencyToken);
        Assert.Contains(item.GetIndexes(), x => x.IsUnique && x.GetFilter()?.Contains("[Status] = 3") == true);
        var sql = db.GetService<IMigrator>().GenerateScript(options: MigrationsSqlGenerationOptions.Idempotent);
        Assert.Contains("CREATE TABLE [nao].[WorkItems]", sql);
        Assert.Contains("__EFMigrationsHistory", sql);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Theory]
    [InlineData("WorkManagement", "WorkManagement")]
    [InlineData("master", "master")]
    [InlineData("WorkManagement", "NeoAgentOrchestration")]
    [InlineData("NeoAgentOrchestration", "NeoAgentOrchestration_other")]
    public void Provisioner_refuses_legacy_system_and_mismatched_catalogs(string catalog, string expected)
        => Assert.Throws<ArgumentException>(() => OrchestrationProvisioner.ValidateDestination(
            $"Server=localhost;Database={catalog};Integrated Security=true", expected));

    [Fact]
    public async Task Sql_roundtrip_keeps_subtasks_evidence_time_and_history_after_reopening_context()
    {
        var f = await Fixture.Create();
        var parent = await f.CreateItem("parent");
        var child = await f.Handlers.Handle(new CreateWorkItem(f.Scope, f.Project.Id, "child", "Child", "domain", f.Actor,
            ParentWorkItemId: parent.Id, EstimatedSeconds: 100), Ct);
        await f.Change(child.Item.Id, new StatusChange(WorkItemStatus.Ready));
        await f.Claim(child.Item.Id);
        f.Clock.Advance(25);
        var sha = new string('a', 40);
        await f.Change(child.Item.Id, new EvidenceChange(EvidenceKind.Commit, sha, EvidenceOutcome.NotApplicable));
        await f.Change(child.Item.Id, new EvidenceChange(EvidenceKind.Test, "roundtrip", EvidenceOutcome.Passed, CommitSha: sha));
        await f.Change(child.Item.Id, new StatusChange(WorkItemStatus.Review));
        await f.Change(child.Item.Id, new StatusChange(WorkItemStatus.Done));
        var details = await f.Handlers.Handle(new GetWorkItem(f.Scope, child.Item.Id), Ct);
        Assert.Equal(25, details.Item.ElapsedSeconds);
        Assert.Equal(25m, details.Item.BudgetUsedPercent);
        Assert.Equal("Done", details.Item.Status);
        Assert.Equal(2, details.Evidence.Count);
        Assert.Equal(sha, details.Evidence.Single(x => x.Kind == "Test").CommitSha);
        Assert.Contains(details.Logs, x => x.Kind == "Claimed");
        Assert.Single((await f.Handlers.Handle(new GetWorkItem(f.Scope, parent.Id), Ct)).Children);
        await OrchestrationProvisioner.MigrateAsync(f.Connection, "NeoAgentOrchestration_Verification", Ct);
        Assert.Equal(details.Item.Version, (await f.Handlers.Handle(new GetWorkItem(f.Scope, child.Item.Id), Ct)).Item.Version);
    }

    [Fact]
    public async Task Concurrent_sql_claims_allow_exactly_one_task_for_a_role()
    {
        var f = await Fixture.Create();
        var first = await f.CreateItem("first"); var second = await f.CreateItem("second");
        var a = await f.Change(first.Id, new StatusChange(WorkItemStatus.Ready));
        var b = await f.Change(second.Id, new StatusChange(WorkItemStatus.Ready));
        async Task<bool> TryClaim(Guid id, Guid version, string agent)
        {
            try
            {
                await f.Handlers.Handle(new ClaimWorkItem(f.Scope, id, f.Role.Id, new(agent, agent), version), Ct);
                return true;
            }
            catch (WorkItemConflictException) { return false; }
        }
        var results = await Task.WhenAll(TryClaim(first.Id, a.Item.Version, "agent-1"), TryClaim(second.Id, b.Item.Version, "agent-2"));
        Assert.Single(results, x => x);
        await using var db = f.Factory.CreateDbContext();
        Assert.Equal(1, await db.WorkItems.CountAsync(x => x.WorkspaceId == f.Scope.WorkspaceId && x.Status == WorkItemStatus.InProgress, Ct));
        Assert.Equal(1, await db.Set<WorkItemTimeEntry>().CountAsync(x => (x.WorkItemId == first.Id || x.WorkItemId == second.Id) && x.EndedAtUtc == null, Ct));
        var remaining = await db.WorkItems.SingleAsync(x => x.WorkspaceId == f.Scope.WorkspaceId && x.Status == WorkItemStatus.Ready, Ct);
        remaining.Claim(f.Scope, f.Role, new("bypass-agent", "bypass-chat"), null, f.Clock.GetUtcNow());
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct)); // SQL index also enforces exclusivity.
    }

    [Fact]
    public async Task Sql_failures_roll_back_and_stale_updates_cannot_overwrite()
    {
        var f = await Fixture.Create(); var created = await f.CreateItem("rollback");
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Store.ExecuteAsync<int>(f.Scope, async (session, token) =>
        {
            var item = (await session.GetItemAsync(created.Id, token))!;
            item.AddLog(f.Scope, f.Actor, "Must roll back", f.Clock.GetUtcNow());
            throw new InvalidOperationException("Deliberate rollback");
        }, Ct));
        var restored = await f.Handlers.Handle(new GetWorkItem(f.Scope, created.Id), Ct);
        Assert.Equal(created.Version, restored.Item.Version);
        Assert.DoesNotContain(restored.Logs, x => x.Message == "Must roll back");
        await using var a = f.Factory.CreateDbContext();
        await using var b = f.Factory.CreateDbContext();
        var first = await a.WorkItems.SingleAsync(x => x.Id == created.Id, Ct);
        var stale = await b.WorkItems.SingleAsync(x => x.Id == created.Id, Ct);
        first.SetEstimate(f.Scope, f.Actor, 100, f.Clock.GetUtcNow()); await a.SaveChangesAsync(Ct);
        stale.SetEstimate(f.Scope, f.Actor, 200, f.Clock.GetUtcNow());
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => b.SaveChangesAsync(Ct));
        await Assert.ThrowsAsync<WorkItemConflictException>(() => f.Handlers.Handle(new UpdateWorkItem(f.Scope,
            created.Id, f.Actor, created.Version, new EstimateChange(300)), Ct));
    }

    [Fact]
    public async Task Sql_scope_checks_and_foreign_keys_reject_cross_workspace_records()
    {
        var f = await Fixture.Create(); var item = await f.CreateItem("scope");
        var other = await Fixture.Create();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => f.Handlers.Handle(new GetWorkItem(other.Scope, item.Id), Ct));
        await using var db = f.Factory.CreateDbContext();
        var invalid = WorkItem.Create(f.Scope, f.Project, "foreign", "Foreign", "domain", f.Actor, f.Clock.GetUtcNow());
        db.Add(invalid);
        db.Entry(invalid).Property(x => x.WorkspaceId).CurrentValue = other.Scope.WorkspaceId;
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
        await using var verify = f.Factory.CreateDbContext();
        Assert.False(await verify.WorkItems.AnyAsync(x => x.Id == invalid.Id, Ct));
    }

    [Fact]
    public async Task Sql_workflows_and_approvals_roundtrip_with_immutable_scope_links()
    {
        var f = await Fixture.Create(); var item = await f.CreateItem("workflow");
        await f.Change(item.Id, new StatusChange(WorkItemStatus.Ready)); await f.Claim(item.Id);
        await f.Change(item.Id, new StatusChange(WorkItemStatus.Review));
        Guid flowId;
        await using (var db = f.Factory.CreateDbContext())
        {
            var work = await db.WorkItems.SingleAsync(x => x.Id == item.Id, Ct);
            var flow = WorkflowDefinition.Create(f.Scope, f.Project, "main", "Main");
            var transition = flow.ConfigureTransition(f.Scope, "complete", f.Role, WorkItemStatus.Review, null,
                WorkItemStatus.Done, new(RequireApproval: true));
            var approval = WorkflowApproval.Record(f.Scope, flow, transition.Id, work, new("reviewer", "review-chat"), true,
                "Verified", f.Clock.GetUtcNow());
            db.Add(flow); db.Add(approval); await db.SaveChangesAsync(Ct); flowId = flow.Id;
        }
        await using var read = f.Factory.CreateDbContext();
        var loaded = await read.Workflows.Include(x => x.Transitions).SingleAsync(x => x.Id == flowId, Ct);
        Assert.Single(loaded.Transitions);
        var recorded = await read.Approvals.SingleAsync(x => x.WorkflowDefinitionId == flowId, Ct);
        Assert.Equal(loaded.Version, recorded.WorkflowVersion);
        Assert.Equal(f.Project.Id, recorded.ProjectId);
    }

    internal sealed class Fixture
    {
        public required string Connection { get; init; }
        public required Factory Factory { get; init; }
        public required SqlWorkspaceWorkStore Store { get; init; }
        public required WorkItemHandlers Handlers { get; init; }
        public required WorkspaceScope Scope { get; init; }
        public required Project Project { get; init; }
        public required RoleProfile Role { get; init; }
        public required ManualClock Clock { get; init; }
        public WorkActor Actor { get; } = new("sql-agent", "sql-chat");
        public static async Task<Fixture> Create()
        {
            var connection = Environment.GetEnvironmentVariable("NEO_ORCHESTRATION_TEST_SQL");
            Assert.SkipUnless(!string.IsNullOrWhiteSpace(connection), "Set an isolated SQL verification connection to run live persistence tests.");
            OrchestrationProvisioner.ValidateDestination(connection!, "NeoAgentOrchestration_Verification");
            await OrchestrationProvisioner.MigrateAsync(connection!, "NeoAgentOrchestration_Verification", Ct);
            var factory = new Factory(connection!);
            await using var db = factory.CreateDbContext();
            var org = Organization.Create(Guid.NewGuid().ToString("N"), "SQL verification");
            var workspace = Workspace.Create(org, "work", "Verification workspace");
            var project = Project.Create(workspace, "project", "Verification project");
            var role = RoleProfile.Create(workspace, "developer", "Developer");
            db.AddRange(org, workspace, project, role); await db.SaveChangesAsync(Ct);
            var store = new SqlWorkspaceWorkStore(factory); var clock = new ManualClock();
            return new Fixture { Connection = connection!, Factory = factory, Store = store,
                Handlers = new WorkItemHandlers(store, clock), Scope = workspace.Scope, Project = project, Role = role, Clock = clock };
        }
        public async Task<Neo.AgentOrchestration.Contracts.WorkItemView> CreateItem(string key)
            => (await Handlers.Handle(new CreateWorkItem(Scope, Project.Id, key, key, "domain", Actor), Ct)).Item;
        public async Task<Neo.AgentOrchestration.Contracts.WorkItemDetails> Change(Guid id, WorkItemChange change)
        {
            var current = await Handlers.Handle(new GetWorkItem(Scope, id), Ct);
            return await Handlers.Handle(new UpdateWorkItem(Scope, id, Actor, current.Item.Version, change), Ct);
        }
        public async Task Claim(Guid id)
        {
            var current = await Handlers.Handle(new GetWorkItem(Scope, id), Ct);
            await Handlers.Handle(new ClaimWorkItem(Scope, id, Role.Id, Actor, current.Item.Version), Ct);
        }
    }

    internal sealed class Factory(string connection) : IDbContextFactory<OrchestrationDbContext>
    {
        public OrchestrationDbContext CreateDbContext() => new(new DbContextOptionsBuilder<OrchestrationDbContext>().UseSqlServer(connection).Options);
    }
}
