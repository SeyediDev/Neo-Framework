using System.Data;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Neo.AgentOrchestration.Application.Work;
using Neo.AgentOrchestration.Domain.Agents;
using Neo.AgentOrchestration.Domain.Projects;
using Neo.AgentOrchestration.Domain.Work;
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
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, ct);
        // Workspace serialization covers role claims AND graph changes, including
        // an empty candidate set. The lock is transaction-owned and cross-process.
        var resource = $"neo-orchestration:workspace:{scope.WorkspaceId:D}";
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock @Resource={resource}, @LockMode='Exclusive',
                @LockOwner='Transaction', @LockTimeout=15000;
            IF @result < 0 THROW 51001, 'Workspace transaction lock unavailable.', 1;
            """, ct);
        if (!await db.Workspaces.AnyAsync(w => w.Id == scope.WorkspaceId && w.OrganizationId == scope.OrganizationId &&
            w.IsEnabled && db.Organizations.Any(o => o.Id == w.OrganizationId && o.IsEnabled), ct))
            throw new KeyNotFoundException("Enabled workspace not found.");
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

    private sealed class Session(OrchestrationDbContext db, WorkspaceScope scope) : IWorkItemSession
    {
        private readonly WorkItemCommandRepository commands = new(db);
        private readonly WorkItemQueryRepository queries = new(db);
        private IQueryable<WorkItem> Items => queries.Query().Where(x =>
            x.OrganizationId == scope.OrganizationId && x.WorkspaceId == scope.WorkspaceId)
            .Include(x => x.Logs).Include(x => x.Evidence).Include(x => x.TimeEntries).Include(x => x.Dependencies).AsSplitQuery();
        public Task<Project?> GetProjectAsync(Guid id, CancellationToken ct) => db.Projects.SingleOrDefaultAsync(x =>
            x.Id == id && x.OrganizationId == scope.OrganizationId && x.WorkspaceId == scope.WorkspaceId, ct);
        public Task<RoleProfile?> GetRoleAsync(Guid id, CancellationToken ct) => db.Roles.SingleOrDefaultAsync(x =>
            x.Id == id && x.OrganizationId == scope.OrganizationId && x.WorkspaceId == scope.WorkspaceId, ct);
        public Task<WorkItem?> GetItemAsync(Guid id, CancellationToken ct) => Items.SingleOrDefaultAsync(x => x.Id == id, ct);
        public async Task<IReadOnlyList<WorkItem>> GetProjectItemsAsync(Guid projectId, CancellationToken ct)
            => await Items.Where(x => x.ProjectId == projectId).ToArrayAsync(ct);
        public Task<bool> IsRoleBusyAsync(Guid roleId, Guid exceptItemId, CancellationToken ct) => db.WorkItems.AnyAsync(x =>
            x.OrganizationId == scope.OrganizationId && x.WorkspaceId == scope.WorkspaceId && x.Id != exceptItemId &&
            x.OwnerRoleId == roleId && x.Status == WorkItemStatus.InProgress, ct);
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
    {
        services.AddDbContextFactory<OrchestrationDbContext>(o => o.UseSqlServer(connectionString));
        services.AddScoped<IWorkspaceWorkStore, SqlWorkspaceWorkStore>();
        return services;
    }
}
