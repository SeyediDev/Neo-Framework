using MediatR;
using Neo.AgentOrchestration.Application.Work;
using Neo.AgentOrchestration.Domain.Projects;
using Neo.Application.Features.Outbox.Dto;

namespace Neo.AgentOrchestration.Application.Delivery;

public enum WorkDeliveryKind : byte { EvaluateWorkflow = 1, DispatchAgent = 2, ReceiveSimulationResult = 3 }

// Source is an authenticated producer/operation namespace, not arbitrary caller
// authority. Payload is fingerprinted, never stored by the delivery ledger.
public sealed record WorkDeliveryRequest(Guid WorkItemId, string Source, string MessageId, string Payload, Guid? AgentRunId = null);
public sealed record DeliveryAcceptance(Guid ReceiptId, Guid WorkItemId, Guid WorkItemVersion,
    long? OutboxId, bool Duplicate);
public sealed record WorkDeliverySignal(Guid OperationId) : IRequest, IOutboxMessage;
public sealed record WorkDeliveryExecution(Guid OperationId, WorkspaceScope Scope, Guid ProjectId,
    Guid WorkItemId, Guid RequestedWorkItemVersion, WorkDeliveryKind Kind, Guid? AgentRunId = null);

public interface IDurableWorkStore
{
    // Callbacks stage same-session domain changes only. No nested store calls,
    // HTTP, external queue publication or independent commits inside them.
    Task<DeliveryAcceptance> EnqueueAsync(WorkspaceScope scope, WorkDeliveryRequest request, WorkDeliveryKind kind,
        Func<IWorkItemSession, CancellationToken, Task> mutation, CancellationToken ct);
    Task<DeliveryAcceptance> ApplyInboxAsync(WorkspaceScope scope, WorkDeliveryRequest request,
        Func<IWorkItemSession, CancellationToken, Task> mutation, CancellationToken ct, WorkDeliveryKind? followUp = null);
}

public sealed class DeliveryConflictException(string message) : InvalidOperationException(message);
