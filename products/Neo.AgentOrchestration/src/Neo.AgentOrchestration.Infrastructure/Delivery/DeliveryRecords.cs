using Neo.AgentOrchestration.Application.Delivery;
using Neo.Domain.Entities.Base;
using Neo.Domain.Entities.Common;

namespace Neo.AgentOrchestration.Infrastructure.Delivery;

// Persistence records, not public input models. Scope/key validation is in the
// durable store; Neo owns the delivery state machine and lease fields.
public sealed class DeliveryRecord : BaseEntity<Guid>
{
    public Guid OrganizationId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid WorkItemId { get; set; }
    public Guid WorkItemVersion { get; set; }
    public string Source { get; set; } = "";
    public string MessageId { get; set; } = "";
    public string PayloadHash { get; set; } = "";
    public WorkDeliveryKind Kind { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public long OutboxId { get; set; }
    public OutboxMessage Outbox { get; set; } = null!;
}

public sealed class InboxReceipt : BaseEntity<Guid>
{
    public Guid OrganizationId { get; set; }
    public Guid WorkspaceId { get; set; }
    public Guid ProjectId { get; set; }
    public Guid WorkItemId { get; set; }
    public Guid WorkItemVersion { get; set; }
    public string Source { get; set; } = "";
    public string MessageId { get; set; } = "";
    public string PayloadHash { get; set; } = "";
    public DateTimeOffset AppliedAtUtc { get; set; }
    public Guid? FollowUpId { get; set; }
    public DeliveryRecord? FollowUp { get; set; }
}
