using Neo.AgentOrchestration.Domain.Projects;
using Neo.AgentOrchestration.Domain.Work;
using Neo.Domain.Entities.Base;

namespace Neo.AgentOrchestration.Domain.Workflows;

// The application must authenticate and authorize the reviewer. Knowing an
// agent/chat ID is not permission to approve. Approvals do not change the item.
public sealed class WorkflowApproval : BaseEntity<Guid>
{
    public Guid ProjectId { get; private set; }
    public Guid WorkItemId { get; private set; }
    public Guid WorkItemVersion { get; private set; }
    public Guid WorkflowDefinitionId { get; private set; }
    public Guid WorkflowVersion { get; private set; }
    public Guid TransitionId { get; private set; }
    public string ReviewerAgentId { get; private set; } = "";
    public string ReviewerChatId { get; private set; } = "";
    public bool Approved { get; private set; }
    public string Reason { get; private set; } = "";
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public WorkflowApproval() { }

    public static WorkflowApproval Record(WorkspaceScope scope, WorkflowDefinition workflow,
        Guid transitionId, WorkItem item, WorkActor reviewer, bool approved, string reason, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(reviewer);
        workflow.RequireScope(scope);
        item.RequireScope(scope);
        var transition = workflow.Transitions.SingleOrDefault(x => x.Id == transitionId)
            ?? throw new KeyNotFoundException("Transition not found.");
        if (!workflow.IsEnabled || !transition.IsEnabled || !transition.RequireApproval || item.IsArchived ||
            workflow.ProjectId != item.ProjectId || transition.FromStatus != item.Status || transition.FromRoleId != item.OwnerRoleId)
            throw new InvalidOperationException("This transition cannot be approved for the current item.");
        if (item.OwnerAgentId == reviewer.AgentId)
            throw new InvalidOperationException("An owner cannot approve their own handoff, including from another chat.");
        if (now < item.UpdatedAtUtc) throw new InvalidOperationException("Approval predates the item change.");
        return new WorkflowApproval { Id = Guid.NewGuid(), ProjectId = item.ProjectId, WorkItemId = item.Id, WorkItemVersion = item.Version,
            WorkflowDefinitionId = workflow.Id, WorkflowVersion = workflow.Version, TransitionId = transition.Id,
            ReviewerAgentId = reviewer.AgentId, ReviewerChatId = reviewer.ChatId, Approved = approved,
            Reason = WorkRules.Required(reason, 8000), CreatedAtUtc = now.ToUniversalTime() };
    }
}
