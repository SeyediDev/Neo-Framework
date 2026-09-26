using MediatR;
using Neo.AgentOrchestration.Contracts;
using Neo.AgentOrchestration.Domain.Projects;
using Neo.AgentOrchestration.Domain.Work;

namespace Neo.AgentOrchestration.Application.Work;

public sealed record CreateWorkItem(WorkspaceScope Scope, Guid ProjectId, string Key, string Title,
    string Domain, WorkActor Actor, string? Description = null, WorkItemPriority Priority = WorkItemPriority.Normal,
    Guid? ParentWorkItemId = null, long? EstimatedSeconds = null) : IRequest<WorkItemDetails>;
public sealed record ClaimWorkItem(WorkspaceScope Scope, Guid WorkItemId, Guid RoleId, WorkActor Actor,
    Guid ExpectedVersion, string? Branch = null) : IRequest<WorkItemDetails>;
public sealed record GetWorkItem(WorkspaceScope Scope, Guid WorkItemId) : IRequest<WorkItemDetails>;
public sealed record UpdateWorkItem(WorkspaceScope Scope, Guid WorkItemId, WorkActor Actor,
    Guid ExpectedVersion, WorkItemChange Change) : IRequest<WorkItemDetails>;

public abstract record WorkItemChange;
public sealed record StatusChange(WorkItemStatus Status, string? Note = null) : WorkItemChange;
public sealed record LogChange(string Message) : WorkItemChange;
public sealed record EstimateChange(long? Seconds) : WorkItemChange;
public sealed record TrackingChange(bool Start) : WorkItemChange;
public sealed record ArchiveChange(bool Archive) : WorkItemChange;
public sealed record EvidenceChange(EvidenceKind Kind, string Reference, EvidenceOutcome Outcome, string? Details = null) : WorkItemChange;
public sealed record DependencyChange(Guid DependsOnWorkItemId) : WorkItemChange;

public sealed class WorkItemConflictException(string message) : InvalidOperationException(message);
