using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Neo.AgentOrchestration.Application.Delivery;
using Neo.AgentOrchestration.Infrastructure.Delivery;
using Neo.AgentOrchestration.Application.Work;
using Neo.AgentOrchestration.Domain.Agents;
using Neo.AgentOrchestration.Domain.Projects;
using Neo.AgentOrchestration.Domain.Work;
using Neo.AgentOrchestration.Domain.Workflows;
using Neo.AgentOrchestration.Domain.Runs;
using Neo.AgentOrchestration.Application.Runs;
using Neo.AgentOrchestration.Contracts;
using Neo.AgentOrchestration.Application.Templates;
using Neo.AgentOrchestration.Domain.Templates;
using Neo.Infrastructure.Data.Repository.Ef;

namespace Neo.AgentOrchestration.Infrastructure.Persistence;

public sealed class SqlWorkspaceWorkStore(IDbContextFactory<OrchestrationDbContext> factory) : IWorkspaceWorkStore
{
    public async Task<T> ExecuteAsync<T>(WorkspaceScope scope,
        Func<IWorkItemSession, CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(operation);
        await using var db = await factory.CreateDbContextAsync(ct);
        // The transaction-owned workspace application lock serializes every
        // business read/write in this scope. Serializable key-range locks can
        // overlap empty ranges of DIFFERENT workspaces and deadlock on inserts.
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        // Workspace serialization covers role claims AND graph changes, including
        // an empty candidate set. The lock is transaction-owned and cross-process.
        await WorkspaceTransaction.LockAsync(db, scope, ct);
        try
        {
            var result = await operation(new Session(db, scope), ct);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return result;
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new WorkItemConflictException("The stored resource changed; reload before retrying.");
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            throw new WorkItemConflictException("A key, role assignment or open interval already exists.");
        }
        // Disposal rolls back any uncommitted state on error/cancellation. The
        // context is never reused. Operations are not automatically replayed.
    }

    internal sealed class Session(OrchestrationDbContext db, WorkspaceScope scope) : IRunSession, ITemplateSession
    {
        public async Task<IReadOnlyList<ProjectTemplate>> GetTemplatesAsync(CancellationToken ct)
            => await db.Set<ProjectTemplate>().Where(x => x.OrganizationId == scope.OrganizationId && x.WorkspaceId == scope.WorkspaceId)
                .OrderBy(x => x.Key).ThenByDescending(x => x.Revision).ToArrayAsync(ct);
        public async Task<IReadOnlyList<TemplateInstantiation>> GetTemplateInstantiationsAsync(CancellationToken ct)
            => await db.Set<TemplateInstantiation>().Where(x => x.OrganizationId == scope.OrganizationId && x.WorkspaceId == scope.WorkspaceId)
                .OrderByDescending(x => x.CreatedAtUtc).ToArrayAsync(ct);
        public void Add(ProjectTemplate template) { template.RequireScope(scope); db.Add(template); }
        public void Add(TemplateInstantiation receipt) { scope.Require(receipt.OrganizationId, receipt.WorkspaceId); db.Add(receipt); }
        public async Task<AgentRun?> GetRunAsync(Guid id, CancellationToken ct)
            => db.AgentRuns.Local.SingleOrDefault(x => x.Id == id && x.OrganizationId == scope.OrganizationId && x.WorkspaceId == scope.WorkspaceId)
                ?? await db.AgentRuns.SingleOrDefaultAsync(x => x.Id == id && x.OrganizationId == scope.OrganizationId && x.WorkspaceId == scope.WorkspaceId, ct);
        public async Task<IReadOnlyList<AgentRun>> GetRunsAsync(Guid workItemId, CancellationToken ct)
            => await db.AgentRuns.Where(x => x.OrganizationId == scope.OrganizationId && x.WorkspaceId == scope.WorkspaceId &&
                x.WorkItemId == workItemId).OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id).ToArrayAsync(ct);
        public async Task<IReadOnlyList<RunDeliveryView>> GetRunDeliveriesAsync(Guid runId, CancellationToken ct)
        {
            var rows = await db.Deliveries.Where(x => x.OrganizationId == scope.OrganizationId && x.WorkspaceId == scope.WorkspaceId &&
                x.AgentRunId == runId).Include(x => x.Outbox).OrderBy(x => x.OutboxId).ToArrayAsync(ct);
            return rows.Select(x => new RunDeliveryView(x.Id, x.OutboxId, x.Kind.ToString(), x.Outbox.OutboxState.ToString(),
                x.Outbox.PublishTryCount ?? 0, x.Outbox.ProcessTryCount ?? 0, x.Outbox.ProcessError ?? x.Outbox.PublishError, x.Outbox.NextAttemptAtUtc)).ToArray();
        }
        public void Add(AgentRun run) { scope.Require(run.OrganizationId, run.WorkspaceId); db.AgentRuns.Add(run); }
        public async Task StageMessageAsync(WorkDeliveryRequest request, WorkDeliveryKind kind, TimeProvider clock, CancellationToken ct)
            => _ = await SqlDurableWorkStore.StageAsync(db, scope, request, (_, _) => Task.CompletedTask, false, kind, clock, ct);
        public async Task<bool> StageInboxAsync(WorkDeliveryRequest request, Func<IWorkItemSession, CancellationToken, Task> mutation,
            WorkDeliveryKind? followUp, TimeProvider clock, CancellationToken ct)
            => (await SqlDurableWorkStore.StageAsync(db, scope, request, mutation, true, followUp, clock, ct)).Duplicate;
        public Task<Workspace> GetWorkspaceAsync(CancellationToken ct) => db.Workspaces.SingleAsync(x =>
            x.Id == scope.WorkspaceId && x.OrganizationId == scope.OrganizationId, ct);
        public async Task<IReadOnlyList<Project>> GetProjectsAsync(CancellationToken ct) => await db.Projects.Where(x =>
            x.OrganizationId == scope.OrganizationId && x.WorkspaceId == scope.WorkspaceId).OrderBy(x => x.Key).ToArrayAsync(ct);
        public async Task<IReadOnlyList<RoleProfile>> GetRolesAsync(CancellationToken ct) => await db.Roles.Where(x =>
            x.OrganizationId == scope.OrganizationId && x.WorkspaceId == scope.WorkspaceId).OrderBy(x => x.Key).ToArrayAsync(ct);
        public async Task<IReadOnlyList<AgentProfile>> GetAgentsAsync(CancellationToken ct) => await db.Agents.Where(x =>
            x.OrganizationId == scope.OrganizationId && x.WorkspaceId == scope.WorkspaceId).OrderBy(x => x.Key).ToArrayAsync(ct);
        public async Task<IReadOnlyList<WorkflowDefinition>> GetWorkflowsAsync(CancellationToken ct) => await db.Workflows.Where(x =>
            x.OrganizationId == scope.OrganizationId && x.WorkspaceId == scope.WorkspaceId).Include(x => x.Transitions).OrderBy(x => x.Key).ToArrayAsync(ct);
        public async Task<IReadOnlyList<WorkflowApproval>> GetApprovalsAsync(Guid workflowId, Guid workItemId, CancellationToken ct)
            => await db.Approvals.Where(x => x.WorkflowDefinitionId == workflowId && x.WorkItemId == workItemId &&
                db.Workflows.Any(w => w.Id == x.WorkflowDefinitionId && w.OrganizationId == scope.OrganizationId &&
                    w.WorkspaceId == scope.WorkspaceId)).OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id).ToArrayAsync(ct);
        public void Add(Project project) { project.RequireScope(scope); db.Projects.Add(project); }
        public void Add(RoleProfile role) { role.RequireScope(scope); db.Roles.Add(role); }
        public void Add(AgentProfile agent) { agent.RequireScope(scope); db.Agents.Add(agent); }
        public void Add(WorkflowDefinition workflow) { workflow.RequireScope(scope); db.Workflows.Add(workflow); }
        public void Add(WorkflowApproval approval) => db.Approvals.Add(approval); // Handler validates both scoped parents.
        private readonly WorkItemCommandRepository commands = new(db);
        private readonly WorkItemQueryRepository queries = new(db);
        private IQueryable<WorkItem> Items => queries.Query().Where(x =>
            x.OrganizationId == scope.OrganizationId && x.WorkspaceId == scope.WorkspaceId)
            .Include(x => x.Logs).Include(x => x.Evidence).Include(x => x.TimeEntries).Include(x => x.Dependencies)
            .Include(x => x.OwnerHistory).AsSplitQuery();
        public Task<Project?> GetProjectAsync(Guid id, CancellationToken ct) => db.Projects.SingleOrDefaultAsync(x =>
            x.Id == id && x.OrganizationId == scope.OrganizationId && x.WorkspaceId == scope.WorkspaceId, ct);
        public Task<RoleProfile?> GetRoleAsync(Guid id, CancellationToken ct) => db.Roles.SingleOrDefaultAsync(x =>
            x.Id == id && x.OrganizationId == scope.OrganizationId && x.WorkspaceId == scope.WorkspaceId, ct);
        public Task<WorkItem?> GetItemAsync(Guid id, CancellationToken ct) => Items.SingleOrDefaultAsync(x => x.Id == id, ct);
        public async Task<WorkHistoryPage> GetHistoryPageAsync(Guid itemId, int skip, int take, Guid? snapshotVersion, CancellationToken ct)
        {
            var root = await db.WorkItems.AsNoTracking().Where(x => x.Id == itemId &&
                x.OrganizationId == scope.OrganizationId && x.WorkspaceId == scope.WorkspaceId)
                .Select(x => new { x.Version }).SingleOrDefaultAsync(ct)
                ?? throw new KeyNotFoundException("Work item not found.");
            if (snapshotVersion.HasValue && snapshotVersion != root.Version)
                throw new WorkItemConflictException("Work item changed; reload before reading the next history page.");
            var query = db.Set<WorkItemLog>().AsNoTracking().Where(x => x.WorkItemId == itemId);
            var total = await query.CountAsync(ct);
            var logs = await query.OrderBy(x => x.CreatedAtUtc).ThenBy(x => x.Id).Skip(skip).Take(take)
                .Select(x => new WorkLogView(x.Id, x.AgentId, x.ChatId, x.Kind, x.Message, x.CreatedAtUtc)).ToArrayAsync(ct);
            return new(root.Version, logs, total, (long)skip + logs.Length < total ? skip + logs.Length : null);
        }
        public async Task<IReadOnlyList<WorkItem>> GetProjectItemsAsync(Guid projectId, CancellationToken ct)
            => await Items.Where(x => x.ProjectId == projectId).ToArrayAsync(ct);
        public async Task<bool> IsRoleBusyAsync(Guid roleId, int capacity, Guid exceptItemId, CancellationToken ct)
            => await db.WorkItems.CountAsync(x => x.OrganizationId == scope.OrganizationId && x.WorkspaceId == scope.WorkspaceId &&
                x.Id != exceptItemId && x.OwnerRoleId == roleId && x.Status == WorkItemStatus.InProgress, ct) >= capacity;
        public void Add(WorkItem item)
        {
            item.RequireScope(scope);
            commands.Add(item);
        }
    }
}

internal sealed class WorkItemCommandRepository(OrchestrationDbContext context)
    : EfCommandRepository<WorkItem, Guid, OrchestrationDbContext>(context);
internal sealed class WorkItemQueryRepository(OrchestrationDbContext context)
    : EfQueryRepository<WorkItem, Guid, OrchestrationDbContext>(context);

public static class PersistenceRegistration
{
    // Opt-in: registering persistence does not create/seed/migrate a database.
    public static IServiceCollection AddOrchestrationSql(this IServiceCollection services, string connectionString)
        => services.AddOrchestrationSql(_ => connectionString);

    public static IServiceCollection AddOrchestrationSql(this IServiceCollection services, Func<IServiceProvider, string> connectionString)
    {
        services.AddDbContextFactory<OrchestrationDbContext>((provider, options) => options.UseSqlServer(connectionString(provider)));
        services.AddScoped<IWorkspaceWorkStore, SqlWorkspaceWorkStore>();
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IDurableWorkStore, SqlDurableWorkStore>();
        services.AddScoped<SqlWorkDeliveryExecutor>();
        return services;
    }
}
