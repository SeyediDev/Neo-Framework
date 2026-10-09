using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Neo.AgentOrchestration.Application.Workspace;
using Neo.AgentOrchestration.Application.Work;
using Neo.AgentOrchestration.Contracts;
using Neo.AgentOrchestration.Domain.Projects;
using Neo.AgentOrchestration.Domain.Runs;
using Neo.AgentOrchestration.Domain.Work;
using Neo.Domain.Entities.Common;

namespace Neo.AgentOrchestration.Infrastructure.Persistence;

public sealed class SqlProjectDeletionStore(IDbContextFactory<OrchestrationDbContext> factory, TimeProvider clock)
    : IProjectDeletionStore
{
    // Fixed, source-owned identifiers, never caller-supplied SQL. Foreign keys
    // remain RESTRICT: an unhandled/custom dependent aborts the whole transaction.
    private const string Scoped = "t.ProjectId=@project AND t.WorkspaceId=@workspace AND t.OrganizationId=@organization";
    private const string ItemIds = "SELECT Id FROM nao.WorkItems WHERE ProjectId=@project AND WorkspaceId=@workspace AND OrganizationId=@organization";
    private const string FlowIds = "SELECT Id FROM nao.WorkflowDefinitions WHERE ProjectId=@project AND WorkspaceId=@workspace AND OrganizationId=@organization";
    private const string RunIds = "SELECT Id FROM nao.AgentRuns WHERE ProjectId=@project AND WorkspaceId=@workspace AND OrganizationId=@organization";
    private const string OutboxIds = "SELECT OutboxId FROM nao.Deliveries WHERE ProjectId=@project AND WorkspaceId=@workspace AND OrganizationId=@organization";
    private static readonly (string Table, string Predicate)[] Graph =
    [
        ("InboxReceipts", Scoped), ("Deliveries", Scoped), ("OutboxMessages", $"t.Id IN ({OutboxIds})"),
        ("TokenUsageReports", $"t.AgentRunId IN ({RunIds})"), ("WorkflowApprovals", $"t.ProjectId=@project AND t.WorkItemId IN ({ItemIds})"),
        ("AgentRuns", Scoped), ("WorkflowTransitions", $"t.WorkflowDefinitionId IN ({FlowIds})"), ("WorkflowDefinitions", Scoped),
        ("WorkItemDependencies", $"t.ProjectId=@project AND t.WorkItemId IN ({ItemIds})"),
        ("WorkItemOwnerHistory", $"t.ProjectId=@project AND t.WorkItemId IN ({ItemIds})"),
        ("WorkItemEvidence", $"t.WorkItemId IN ({ItemIds})"), ("WorkItemLogs", $"t.WorkItemId IN ({ItemIds})"),
        ("WorkItemTimeEntries", $"t.WorkItemId IN ({ItemIds})"), ("WorkTokenUsage", $"t.WorkItemId IN ({ItemIds})"),
        ("TemplateInstantiations", Scoped), ("ProjectRepositoryBindings", Scoped), ("WorkItems", Scoped),
        ("Projects", "t.Id=@project AND t.WorkspaceId=@workspace AND t.OrganizationId=@organization")
    ];

    public Task<ProjectDeletionPreview> PreviewAsync(WorkspaceScope scope, Guid projectId, CancellationToken ct)
        => Execute(scope, projectId, (db, preview, token) => Task.FromResult(preview), ct);

    public Task<ProjectDeletionResult> DeleteAsync(WorkspaceScope scope, Guid projectId, DeleteProjectRequest request, CancellationToken ct)
        => Execute(scope, projectId, async (db, preview, token) =>
        {
            if (!string.Equals(preview.Key, request.ConfirmProjectKey, StringComparison.Ordinal))
                throw new ArgumentException("Confirm the exact project key.");
            if (!string.Equals(preview.Snapshot, request.ExpectedSnapshot, StringComparison.OrdinalIgnoreCase))
                throw new WorkItemConflictException("The deletion graph changed; preview it again.");
            if (!preview.CanDelete)
                throw new WorkItemConflictException("Resolve active/uncertain runs and pending delivery leases before deletion.");
            if (preview.ActiveManualAssignments > 0 && !request.IncludeActiveManualAssignments)
                throw new WorkItemConflictException("Explicitly confirm deletion of active manual assignments.");

            // A parameterized command uses sp_executesql scope: create and consume
            // the temporary ID set in one batch. This retains no content/backup.
            var sql = new StringBuilder("SELECT OutboxId INTO #ProjectDeletionOutbox FROM nao.Deliveries WHERE ProjectId=@project AND WorkspaceId=@workspace AND OrganizationId=@organization;\n");
            foreach (var node in Graph)
            {
                var predicate = node.Table == "OutboxMessages" ? "t.Id IN (SELECT OutboxId FROM #ProjectDeletionOutbox)" : node.Predicate;
                sql.AppendLine($"DELETE t FROM nao.[{node.Table}] t WHERE {predicate};");
                sql.AppendLine($"IF @@ROWCOUNT <> @count{node.Table} THROW 51000, 'The deletion graph changed; no deletion was committed.', 1;");
            }
            sql.AppendLine("DROP TABLE #ProjectDeletionOutbox;");
            await using var command = Command(db, scope, projectId, sql.ToString());
            foreach (var node in Graph)
            {
                var count = command.CreateParameter(); count.ParameterName = "@count" + node.Table;
                count.DbType = DbType.Int32; count.Value = preview.Records[node.Table]; command.Parameters.Add(count);
            }
            try { await command.ExecuteNonQueryAsync(token); }
            catch (SqlException error) when (error.Number == 51000)
            { throw new WorkItemConflictException("The deletion graph changed; no deletion was committed."); }
            return new ProjectDeletionResult(projectId, preview.Key, preview.Records, clock.GetUtcNow());
        }, ct);

    private async Task<T> Execute<T>(WorkspaceScope scope, Guid projectId,
        Func<OrchestrationDbContext, ProjectDeletionPreview, CancellationToken, Task<T>> operation, CancellationToken ct)
    {
        if (projectId == Guid.Empty) throw new ArgumentException("Project ID is required.");
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var transaction = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await WorkspaceTransaction.LockAsync(db, scope, ct);
        var preview = await Snapshot(db, scope, projectId, ct);
        var result = await operation(db, preview, ct);
        await transaction.CommitAsync(ct);
        // Disposal rolls back every partial deletion on error/cancellation. No replay.
        return result;
    }

    private async Task<ProjectDeletionPreview> Snapshot(OrchestrationDbContext db, WorkspaceScope scope, Guid projectId, CancellationToken ct)
    {
        var project = await db.Projects.AsNoTracking().SingleOrDefaultAsync(x => x.Id == projectId &&
            x.WorkspaceId == scope.WorkspaceId && x.OrganizationId == scope.OrganizationId, ct)
            ?? throw new KeyNotFoundException("Project not found in this workspace.");
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes($"{scope.OrganizationId:D}/{scope.WorkspaceId:D}/{projectId:D}\n"));
        var counts = new Dictionary<string, int>();
        foreach (var node in Graph)
        {
            hash.AppendData(Encoding.UTF8.GetBytes(node.Table + "\n"));
            // Business graph writes share the workspace application lock.
            // Only delivery leases advance independently. Do not range-lock
            // every empty child table across unrelated workspaces.
            var leaseFence = node.Table == "OutboxMessages" ? "WITH (UPDLOCK, HOLDLOCK)" : "";
            // Hash full rows on SQL, including content and rowversions, without
            // returning payloads/history/secrets. Locks also fence Neo's independent
            // delivery lease acquisition through commit; counts alone aren't a version.
            await using var command = Command(db, scope, projectId, $"""
                SELECT CONVERT(varchar(50), t.Id), HASHBYTES('SHA2_256',
                    (SELECT t.* FOR JSON PATH, INCLUDE_NULL_VALUES, WITHOUT_ARRAY_WRAPPER))
                FROM nao.[{node.Table}] t {leaseFence}
                WHERE {node.Predicate} ORDER BY t.Id
                """);
            await using var reader = await command.ExecuteReaderAsync(ct);
            var count = 0;
            while (await reader.ReadAsync(ct))
            {
                hash.AppendData(Encoding.UTF8.GetBytes(reader.GetString(0) + ":"));
                hash.AppendData((byte[])reader.GetValue(1)); count++;
            }
            counts.Add(node.Table, count);
        }
        var items = db.WorkItems.Where(x => x.ProjectId == projectId && x.WorkspaceId == scope.WorkspaceId && x.OrganizationId == scope.OrganizationId);
        var types = await items.GroupBy(x => x.Type).Select(g => new { Type = g.Key, Count = g.Count() }).ToArrayAsync(ct);
        var activeManual = await items.CountAsync(x => x.Status == WorkItemStatus.InProgress ||
            db.Set<WorkItemTimeEntry>().Any(t => t.WorkItemId == x.Id && t.EndedAtUtc == null), ct);
        var activeRuns = await db.AgentRuns.CountAsync(x => x.ProjectId == projectId && x.WorkspaceId == scope.WorkspaceId && x.OrganizationId == scope.OrganizationId &&
            (x.Status == AgentRunStatus.Queued || x.Status == AgentRunStatus.AwaitingResult ||
                x.AllowExternalExecution && x.Status == AgentRunStatus.NeedsInput), ct);
        var terminal = new[] { OutboxState.Processed, OutboxState.Failed, OutboxState.Expired, OutboxState.Canceled, OutboxState.DuplicateIdempotencyKey };
        var pending = await db.Deliveries.CountAsync(x => x.ProjectId == projectId && x.WorkspaceId == scope.WorkspaceId && x.OrganizationId == scope.OrganizationId &&
            (!terminal.Contains(x.Outbox.OutboxState) || x.Outbox.DeliveryLeaseId != null), ct);
        return new(projectId, project.Key, project.Name, Convert.ToHexString(hash.GetHashAndReset()), counts,
            types.ToDictionary(x => x.Type.ToString(), x => x.Count), await items.CountAsync(x => x.ParentWorkItemId != null, ct), activeManual, activeRuns, pending);
    }

    private static DbCommand Command(OrchestrationDbContext db, WorkspaceScope scope, Guid projectId, string sql)
    {
        var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = sql; command.CommandTimeout = 30;
        command.Transaction = db.Database.CurrentTransaction!.GetDbTransaction();
        Add("@project", projectId); Add("@workspace", scope.WorkspaceId); Add("@organization", scope.OrganizationId);
        return command;
        void Add(string name, Guid value)
        {
            var parameter = command.CreateParameter(); parameter.ParameterName = name;
            parameter.DbType = DbType.Guid; parameter.Value = value; command.Parameters.Add(parameter);
        }
    }
}
