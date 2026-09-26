using Neo.AgentOrchestration.Application.Agents;
using Neo.AgentOrchestration.Contracts;
using Neo.AgentOrchestration.Domain.Agents;
using Neo.AgentOrchestration.Domain.Projects;
using Neo.AgentOrchestration.Domain.Work;
using Neo.AgentOrchestration.Domain.Workflows;

namespace Neo.AgentOrchestration.Application.Workflows;

// Pure preview, not a dispatch, claim or authorization decision. The executor
// must reload/re-evaluate inside its transaction, compare BOTH versions and
// enforce dependencies/children, role availability and membership before saving
// a handoff with its outbox entry. No harness is contacted from this layer.
public static class WorkflowPlanner
{
    public static WorkflowPlan Preview(WorkspaceScope scope, WorkflowDefinition workflow, Guid transitionId,
        WorkItem item, IEnumerable<RoleProfile> roles, IEnumerable<AgentProfile> agents,
        IEnumerable<WorkflowApproval> approvals, DateTimeOffset now, Guid? preferredProfileId = null)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(roles);
        ArgumentNullException.ThrowIfNull(agents);
        ArgumentNullException.ThrowIfNull(approvals);
        workflow.RequireScope(scope);
        item.RequireScope(scope);
        if (workflow.ProjectId != item.ProjectId) throw new InvalidOperationException("Workflow belongs to another project.");
        var transition = workflow.Transitions.SingleOrDefault(x => x.Id == transitionId)
            ?? throw new KeyNotFoundException("Transition not found.");
        var reasons = new List<string>();
        if (!workflow.IsEnabled || !transition.IsEnabled) reasons.Add("workflow-disabled");
        if (item.IsArchived) reasons.Add("item-archived");
        if (item.Status != transition.FromStatus || item.OwnerRoleId != transition.FromRoleId)
            reasons.Add("source-mismatch");
        if (now < item.UpdatedAtUtc) reasons.Add("stale-clock");
        var scopedRoles = roles.Where(x => x.OrganizationId == scope.OrganizationId &&
            x.WorkspaceId == scope.WorkspaceId && x.IsEnabled).ToArray();
        if (!scopedRoles.Any(x => x.Id == transition.FromRoleId)) reasons.Add("source-role-unavailable");
        var targetRole = scopedRoles.SingleOrDefault(x => x.Id == transition.ToRoleId);
        if (transition.ToRoleId.HasValue && targetRole is null) reasons.Add("target-role-unavailable");

        var evidence = item.Evidence.Where(x => x.CreatedAtUtc <= now).ToArray();
        var commit = evidence.Where(x => x.Kind == EvidenceKind.Commit).MaxBy(x => x.Sequence);
        if (transition.RequireCommit && commit is null) reasons.Add("commit-required");
        // When a commit is present, tests/artifacts must explicitly attest to it.
        // Sequence is persisted per item; timestamp ties cannot resurrect a pass.
        var applicable = evidence.Where(x => string.Equals(x.CommitSha, commit?.Reference, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (transition.RequirePassingTests)
        {
            var tests = applicable.Where(x => x.Kind == EvidenceKind.Test)
                .GroupBy(x => x.Reference, StringComparer.Ordinal).Select(x => x.MaxBy(e => e.Sequence)!).ToArray();
            if (tests.Length == 0 || tests.Any(x => x.Outcome != EvidenceOutcome.Passed))
                reasons.Add("passing-tests-required");
        }
        if (transition.RequiredArtifact is not null && !applicable.Any(x => x.Kind == EvidenceKind.Artifact &&
            x.Reference == transition.RequiredArtifact)) reasons.Add("artifact-required");
        if (transition.RequireApproval)
        {
            var current = approvals.Where(x => x.WorkItemId == item.Id && x.WorkItemVersion == item.Version &&
                x.WorkflowDefinitionId == workflow.Id && x.WorkflowVersion == workflow.Version &&
                x.TransitionId == transition.Id && x.ReviewerAgentId != item.OwnerAgentId &&
                x.CreatedAtUtc >= item.UpdatedAtUtc && x.CreatedAtUtc <= now).ToArray();
            // A rejection by any reviewer blocks. Equal timestamp conflicts fail closed.
            var latest = current.GroupBy(x => x.ReviewerAgentId, StringComparer.Ordinal)
                .SelectMany(g => g.Where(x => x.CreatedAtUtc == g.Max(a => a.CreatedAtUtc))).ToArray();
            if (latest.Length == 0 || latest.Any(x => !x.Approved)) reasons.Add("approval-required");
        }
        Guid? profileId = null;
        if (reasons.Count == 0 && targetRole is not null)
            profileId = AgentSelector.Select(scope, targetRole, agents, preferredProfileId).Id;
        return new WorkflowPlan(workflow.Id, workflow.Version, transition.Id, item.Id, item.Version,
            transition.ToStatus.ToString(), transition.ToRoleId, profileId, reasons.Count == 0, reasons.ToArray());
    }
}
