using Neo.AgentOrchestration.Domain.Agents;
using Neo.AgentOrchestration.Domain.Projects;
using Neo.AgentOrchestration.Domain.Work;
using Neo.AgentOrchestration.Domain.Workflows;
using Neo.Domain.Entities.Base;

namespace Neo.AgentOrchestration.Domain.Runs;

public enum AgentRunStatus : byte { Queued = 1, AwaitingResult = 2, Succeeded = 3, Failed = 4, NeedsInput = 5 }
public enum RunDecision : byte { Pending = 0, Waiting = 1, HandedOff = 2, Completed = 3, NotApplicable = 4 }
public enum SimulationOutcome : byte { Succeeded = 1, Failed = 2, NeedsInput = 3 }
public sealed record RunEvidence(EvidenceKind Kind, string Reference, EvidenceOutcome Outcome, string? Details, string? CommitSha);

// An immutable execution configuration with a separately advancing result and
// handoff decision. Queue/execution retry leases remain owned by Neo Outbox.
public sealed class AgentRun : BaseEntity<Guid>
{
    public Guid OrganizationId { get; private set; }
    public Guid WorkspaceId { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid WorkItemId { get; private set; }
    public Guid RoleId { get; private set; }
    public Guid AgentProfileId { get; private set; }
    public Guid WorkflowId { get; private set; }
    public Guid InitialWorkflowVersion { get; private set; }
    public Guid WorkflowVersion { get; private set; }
    public Guid WorkItemVersion { get; private set; }
    public Guid? PreviousRunId { get; private set; }
    public Guid? NextRunId { get; private set; }
    public int Hop { get; private set; }
    public string RequestedByAgentId { get; private set; } = "";
    public string RequestedByChatId { get; private set; } = "";
    public string Provider { get; private set; } = "";
    public string? Model { get; private set; }
    public string? Instructions { get; private set; }
    public string? SkillPath { get; private set; }
    public string? Branch { get; private set; }
    public SimulationOutcome SimulationOutcome { get; private set; }
    public AgentRunStatus Status { get; private set; }
    public RunDecision Decision { get; private set; }
    public string? DecisionReason { get; private set; }
    public string? ResultSummary { get; private set; }
    public bool AllowExternalExecution { get; private set; }
    // Frozen transport input; deliberately absent from public run views.
    public string? HarnessPayload { get; private set; }
    public string? HarnessBinding { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset? DispatchedAtUtc { get; private set; }
    public DateTimeOffset? CompletedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }
    public WorkActor Actor => new("neo-run:" + Id.ToString("N"), "run:" + Id.ToString("N"));
    public AgentRun() { }

    public static AgentRun Create(WorkspaceScope scope, Guid id, WorkItem item, WorkflowDefinition workflow,
        RoleProfile role, AgentProfile profile, WorkActor requestor, string? branch, SimulationOutcome outcome,
        DateTimeOffset now, AgentRun? previous = null, bool allowExternalExecution = false)
    {
        item.RequireScope(scope); workflow.RequireScope(scope); role.RequireScope(scope); profile.RequireScope(scope);
        if (id == Guid.Empty || !Enum.IsDefined(outcome)) throw new ArgumentException("Invalid run identifier or outcome.");
        if (item.Status != WorkItemStatus.Ready || item.IsArchived || !role.IsEnabled || !profile.IsEnabled ||
            profile.RoleProfileId != role.Id || !workflow.IsEnabled || workflow.ProjectId != item.ProjectId)
            throw new InvalidOperationException("Run configuration does not match an eligible item.");
        if (previous is not null && (previous.WorkItemId != item.Id || previous.WorkflowId != workflow.Id || previous.Hop >= 20))
            throw new InvalidOperationException("Invalid run chain or handoff limit reached.");
        return new AgentRun { Id = id, OrganizationId = scope.OrganizationId, WorkspaceId = scope.WorkspaceId,
            ProjectId = item.ProjectId, WorkItemId = item.Id, RoleId = role.Id, AgentProfileId = profile.Id,
            WorkflowId = workflow.Id, InitialWorkflowVersion = workflow.Version, WorkflowVersion = workflow.Version,
            PreviousRunId = previous?.Id, Hop = (previous?.Hop ?? 0) + 1,
            RequestedByAgentId = requestor.AgentId, RequestedByChatId = requestor.ChatId,
            Provider = profile.Provider, Model = profile.Model, Instructions = profile.Instructions, SkillPath = profile.SkillPath,
            Branch = WorkRules.Optional(branch, 250), SimulationOutcome = outcome, Status = AgentRunStatus.Queued,
            AllowExternalExecution = previous?.AllowExternalExecution ?? allowExternalExecution,
            CreatedAtUtc = now, UpdatedAtUtc = now };
    }

    public void BindClaim(WorkItem item)
    {
        RequireOwner(item);
        if (Status != AgentRunStatus.Queued || item.Status != WorkItemStatus.InProgress)
            throw new InvalidOperationException("Run must bind its active claim.");
        WorkItemVersion = item.Version;
    }
    public void MarkDispatched(WorkItem item, DateTimeOffset now)
    {
        RequireCurrent(item);
        if (Status != AgentRunStatus.Queued || item.Status != WorkItemStatus.InProgress)
            throw new InvalidOperationException("Run is not queued.");
        Status = AgentRunStatus.AwaitingResult; DispatchedAtUtc = now; UpdatedAtUtc = now;
    }
    public void PrepareHarness(string binding, string payload)
    {
        if (!AllowExternalExecution || Provider == "fake" || Status != AgentRunStatus.Queued || HarnessPayload is not null ||
            binding.Length != 64 || !binding.All(Uri.IsHexDigit) || string.IsNullOrWhiteSpace(payload) ||
            System.Text.Encoding.UTF8.GetByteCount(payload) > 262144)
            throw new InvalidOperationException("Invalid external dispatch snapshot.");
        HarnessBinding = binding; HarnessPayload = payload;
    }
    public void RecordResult(WorkItem item, SimulationOutcome result, string summary, DateTimeOffset now,
        IReadOnlyList<RunEvidence>? evidence = null)
    {
        RequireCurrent(item);
        if (Status != AgentRunStatus.AwaitingResult || item.Status != WorkItemStatus.InProgress || !Enum.IsDefined(result))
            throw new InvalidOperationException("Run is not awaiting a result.");
        var text = WorkRules.Required(summary, 8000);
        foreach (var entry in evidence ?? [])
            item.AddEvidence(new(OrganizationId, WorkspaceId), Actor, entry.Kind, entry.Reference,
                entry.Outcome, entry.Details, now, entry.CommitSha);
        item.AddLog(new(OrganizationId, WorkspaceId), Actor, text, now);
        item.ChangeStatus(new(OrganizationId, WorkspaceId), Actor,
            result == SimulationOutcome.Succeeded ? WorkItemStatus.Review : WorkItemStatus.Blocked, now,
            Provider == "fake" ? "Simulation result received." : "Authenticated harness result received.");
        Status = result switch { SimulationOutcome.Succeeded => AgentRunStatus.Succeeded,
            SimulationOutcome.Failed => AgentRunStatus.Failed, _ => AgentRunStatus.NeedsInput };
        Decision = result == SimulationOutcome.Succeeded ? RunDecision.Pending : RunDecision.NotApplicable;
        ResultSummary = text; WorkItemVersion = item.Version; CompletedAtUtc = now; UpdatedAtUtc = now;
    }
    public void DeclineDispatch(WorkItem item, string reason, DateTimeOffset now)
    {
        RequireCurrent(item);
        if (Status != AgentRunStatus.Queued) throw new InvalidOperationException("Run is not queued.");
        Status = AgentRunStatus.AwaitingResult;
        RecordResult(item, SimulationOutcome.NeedsInput, reason, now);
    }
    public void Wait(string reason, DateTimeOffset now)
    {
        if (Decision is RunDecision.HandedOff or RunDecision.Completed) throw new InvalidOperationException("Run already advanced.");
        Decision = RunDecision.Waiting; DecisionReason = WorkRules.Required(reason, 500); UpdatedAtUtc = now;
    }
    public void RequestEvaluation(WorkItem item, WorkflowDefinition workflow, WorkActor requestor, DateTimeOffset now)
    {
        RequireOwner(item);
        if (Status is AgentRunStatus.Queued or AgentRunStatus.AwaitingResult || Decision is RunDecision.HandedOff or RunDecision.Completed ||
            item.IsArchived || item.Status is not (WorkItemStatus.Review or WorkItemStatus.Blocked) || workflow.Id != WorkflowId)
            throw new InvalidOperationException("This run cannot be reevaluated.");
        workflow.RequireScope(new(OrganizationId, WorkspaceId));
        WorkItemVersion = item.Version; WorkflowVersion = workflow.Version; Decision = RunDecision.Pending;
        DecisionReason = "evaluation-requested-by:" + WorkRules.Required(requestor.AgentId, 200); UpdatedAtUtc = now;
    }
    public void Advance(Guid? nextRun, DateTimeOffset now)
    {
        if (Decision is RunDecision.HandedOff or RunDecision.Completed) throw new InvalidOperationException("Run already advanced.");
        NextRunId = nextRun; Decision = nextRun.HasValue ? RunDecision.HandedOff : RunDecision.Completed;
        DecisionReason = null; UpdatedAtUtc = now;
    }
    public void ReturnAssignment(WorkItem item, WorkActor requestor, DateTimeOffset now)
    {
        RequireOwner(item);
        if (requestor.AgentId != RequestedByAgentId) throw new UnauthorizedAccessException("Only the run requestor may recover its stopped assignment.");
        if (Status is AgentRunStatus.Queued or AgentRunStatus.AwaitingResult || Decision == RunDecision.HandedOff)
            throw new InvalidOperationException("An active or handed-off run cannot be released.");
        item.ReturnStoppedAssignment(new(OrganizationId, WorkspaceId), Actor, requestor, now);
        WorkItemVersion = item.Version; Decision = RunDecision.NotApplicable;
        DecisionReason = "assignment-returned-to-requestor"; UpdatedAtUtc = now;
    }
    public void RequireCurrent(WorkItem item)
    {
        RequireOwner(item);
        if (item.Version != WorkItemVersion) throw new InvalidOperationException("Work changed after this execution was requested.");
    }
    private void RequireOwner(WorkItem item)
    {
        item.RequireScope(new(OrganizationId, WorkspaceId));
        if (item.Id != WorkItemId || item.OwnerRoleId != RoleId || item.OwnerAgentId != Actor.AgentId || item.OwnerChatId != Actor.ChatId)
            throw new InvalidOperationException("Run no longer owns the work item.");
    }
}
