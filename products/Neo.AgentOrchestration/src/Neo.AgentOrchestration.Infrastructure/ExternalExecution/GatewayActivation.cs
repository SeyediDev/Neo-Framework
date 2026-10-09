using System.Text.Json;
using Neo.AgentOrchestration.Application.ExternalAgents;
using Neo.AgentOrchestration.Domain.ExternalExecution;
using Neo.Domain.Entities.Common;

namespace Neo.AgentOrchestration.Infrastructure.ExternalExecution;

// Delivery metadata only, not another scheduler, run owner or work catalog.
public sealed class GatewayActivation
{
    public Guid Id { get; set; }
    public Guid RunId { get; set; }
    public Guid OrganizationId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public int Sequence { get; set; }
    public long OutboxId { get; set; }
    public OutboxMessage Outbox { get; set; } = null!;
    public ExternalAgentScope Scope => new(OrganizationId, WorkspaceId, ProjectId, RunId);

    internal static void Stage(GatewayDbContext db, GatewayRun run, int sequence, DateTimeOffset due)
    {
        var id = Guid.NewGuid();
        db.Activations.Add(new() { Id = id, RunId = run.RunId, OrganizationId = run.OrganizationId,
            WorkspaceId = run.WorkspaceId, ProjectId = run.ProjectId, Sequence = sequence,
            Outbox = new() { MessageName = nameof(GatewayAdvanceSignal), MessageType = typeof(GatewayAdvanceSignal).FullName!,
                MessageContent = JsonSerializer.Serialize(new GatewayAdvanceSignal(id)),
                TenantKey = run.WorkspaceId.ToString("N"), IdempotencyKey = id.ToString("N"),
                NextAttemptAtUtc = due.UtcDateTime } });
    }
}

public sealed record GatewayAdvanceSignal(Guid ActivationId);
