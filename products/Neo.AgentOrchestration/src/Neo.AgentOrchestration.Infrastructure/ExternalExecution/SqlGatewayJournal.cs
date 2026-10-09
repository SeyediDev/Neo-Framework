using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Neo.AgentOrchestration.Application.ExternalAgents;
using Neo.AgentOrchestration.Application.ExternalExecution;
using Neo.AgentOrchestration.Domain.ExternalExecution;

namespace Neo.AgentOrchestration.Infrastructure.ExternalExecution;

public sealed class SqlGatewayJournal(IDbContextFactory<GatewayDbContext> factory) : IGatewayJournal
{
    public Task<GatewayRun> ReserveAsync(GatewayRun candidate, CancellationToken ct) => Execute(async db =>
    {
        var saved = await db.Runs.SingleOrDefaultAsync(x => x.RunId == candidate.RunId, ct);
        if (saved is not null) { saved.RequireDuplicate(candidate); return saved; }
        db.Runs.Add(candidate); return candidate;
    }, ct);

    public async Task<GatewayRun> ReadAsync(ExternalAgentScope scope, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        return await Scoped(db, scope).AsNoTracking().SingleOrDefaultAsync(ct) ?? throw new GatewayConflictException("gateway-run-not-found");
    }

    public Task<GatewayRun> AcquireAsync(ExternalAgentScope scope, Guid lease, DateTimeOffset now, CancellationToken ct) => Execute(async db =>
    {
        var run = await Scoped(db, scope).SingleOrDefaultAsync(ct) ?? throw new GatewayConflictException("gateway-run-not-found");
        // Initial global executor capacity is deliberately ONE, across tenants
        // and engines. A timed-out worker does not free a possibly-live sandbox.
        if (!run.CapacityHeld && run.Phase != GatewayPhase.CallbackReady &&
            await db.Runs.AnyAsync(x => x.CapacityHeld && x.RunId != run.RunId, ct))
            throw new GatewayConflictException("gateway-capacity-held");
        run.Acquire(lease, now, TimeSpan.FromMinutes(2)); return run;
    }, ct);

    public Task<GatewayRun> ChangeAsync(ExternalAgentScope scope, Guid expectedVersion,
        Action<GatewayRun> change, CancellationToken ct) => Execute(async db =>
    {
        var run = await Scoped(db, scope).SingleOrDefaultAsync(ct) ?? throw new GatewayConflictException("gateway-run-not-found");
        if (run.Version != expectedVersion) throw new GatewayConflictException("gateway-version-conflict");
        change(run); return run;
    }, ct);

    public async Task SaveUsageAsync(ExternalAgentScope scope, Guid lease, DateTimeOffset now,
        IReadOnlyList<ExternalAgentUsage> reports, CancellationToken ct)
    {
        if (reports.Count > 100) throw new GatewayConflictException("gateway-usage-invalid");
        await Execute(async db =>
        {
            var run = await Scoped(db, scope).SingleOrDefaultAsync(ct) ?? throw new GatewayConflictException("gateway-run-not-found");
            run.RequireLease(lease, now);
            foreach (var report in reports)
            {
                Validate(report);
                var body = JsonSerializer.Serialize(report); var hash = GatewayRun.Hash(body);
                // Local includes reports added earlier in this same transaction.
                var existing = db.Usage.Local.SingleOrDefault(x => x.RunId == run.RunId && x.ReportId == report.ReportId)
                    ?? await db.Usage.SingleOrDefaultAsync(x => x.RunId == run.RunId && x.ReportId == report.ReportId, ct);
                if (existing is not null)
                { if (existing.BodyHash != hash) throw new GatewayConflictException("gateway-usage-conflict"); continue; }
                db.Usage.Add(new() { RunId = run.RunId, OrganizationId = run.OrganizationId,
                    WorkspaceId = run.WorkspaceId, ProjectId = run.ProjectId, ReportId = report.ReportId,
                    Body = body, BodyHash = hash, ObservedAtUtc = now });
            }
            return run;
        }, ct);
    }

    private static IQueryable<GatewayRun> Scoped(GatewayDbContext db, ExternalAgentScope s) => db.Runs.Where(x =>
        x.RunId == s.RunId && x.OrganizationId == s.OrganizationId && x.WorkspaceId == s.WorkspaceId && x.ProjectId == s.ProjectId);

    private async Task<T> Execute<T>(Func<GatewayDbContext, Task<T>> action, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Every journal mutation, including capacity/duplicate decisions, is
        // serialized across gateway processes. No network call under this lock.
        await db.Database.ExecuteSqlRawAsync("""
            DECLARE @result int;
            EXEC @result = sys.sp_getapplock @Resource='fanasa-agentic:gateway-journal',
                @LockMode='Exclusive', @LockOwner='Transaction', @LockTimeout=15000;
            IF @result < 0 THROW 51002, 'Gateway journal lock unavailable.', 1;
            """, ct);
        var result = await action(db);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return result;
    }
    private static void Validate(ExternalAgentUsage r)
    {
        static bool Invalid(string s, int max) => string.IsNullOrWhiteSpace(s) || s.Length > max || s.Any(char.IsControl);
        if (Invalid(r.ReportId, 200) || Invalid(r.Provider, 200) || Invalid(r.Model, 200) ||
            new[] { r.InputTokens, r.OutputTokens, r.CachedInputTokens, r.ReasoningTokens }.Any(x => x < 0) ||
            r.InputTokens.HasValue && r.CachedInputTokens > r.InputTokens ||
            r.OutputTokens.HasValue && r.ReasoningTokens > r.OutputTokens)
            throw new GatewayConflictException("gateway-usage-invalid");
        if (r.InputTokens.HasValue && r.OutputTokens.HasValue)
            try { _ = checked(r.InputTokens.Value + r.OutputTokens.Value); }
            catch (OverflowException) { throw new GatewayConflictException("gateway-usage-invalid"); }
    }
}
