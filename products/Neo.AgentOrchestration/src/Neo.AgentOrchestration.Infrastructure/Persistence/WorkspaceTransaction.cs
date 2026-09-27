using Microsoft.EntityFrameworkCore;
using Neo.AgentOrchestration.Domain.Projects;

namespace Neo.AgentOrchestration.Infrastructure.Persistence;

internal static class WorkspaceTransaction
{
    public static async Task LockAsync(OrchestrationDbContext db, WorkspaceScope scope, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("A transaction is required.");
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
    }
}
