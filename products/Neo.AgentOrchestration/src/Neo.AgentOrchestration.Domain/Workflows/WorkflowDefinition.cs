using Neo.AgentOrchestration.Domain.Agents;
using Neo.AgentOrchestration.Domain.Projects;
using Neo.AgentOrchestration.Domain.Work;
using Neo.Domain.Entities.Base;

namespace Neo.AgentOrchestration.Domain.Workflows;

public sealed class WorkflowDefinition : BaseEntity<Guid>
{
    private readonly List<WorkflowTransition> _transitions = [];
    public Guid OrganizationId { get; private set; }
    public Guid WorkspaceId { get; private set; }
    public Guid ProjectId { get; private set; }
    public string Key { get; private set; } = "";
    public string Name { get; private set; } = "";
    public bool IsEnabled { get; private set; }
    public Guid Version { get; private set; }
    public IReadOnlyList<WorkflowTransition> Transitions => _transitions.AsReadOnly();
    public WorkflowDefinition() { }

    public static WorkflowDefinition Create(WorkspaceScope scope, Project project, string key, string name)
    {
        ArgumentNullException.ThrowIfNull(project);
        project.RequireScope(scope);
        if (!project.IsEnabled) throw new InvalidOperationException("Project is disabled.");
        return new WorkflowDefinition { Id = Guid.NewGuid(), OrganizationId = project.OrganizationId,
            WorkspaceId = project.WorkspaceId, ProjectId = project.Id, Key = ProjectRules.Key(key),
            Name = ProjectRules.Name(name), Version = Guid.NewGuid(), IsEnabled = true };
    }

    public WorkflowTransition ConfigureTransition(WorkspaceScope scope, string key, RoleProfile fromRole,
        WorkItemStatus fromStatus, RoleProfile? toRole, WorkItemStatus toStatus, WorkflowGates gates,
        bool enabled = true)
    {
        RequireScope(scope);
        var validKey = ProjectRules.Key(key);
        var existing = _transitions.SingleOrDefault(x => x.Key == validKey);
        var proposed = WorkflowTransition.Create(scope, Id, validKey, fromRole, fromStatus, toRole, toStatus, gates, enabled);
        if (_transitions.Any(x => x.Id != existing?.Id && x.IsEnabled && proposed.IsEnabled &&
            x.FromRoleId == proposed.FromRoleId && x.FromStatus == proposed.FromStatus))
            throw new InvalidOperationException("Only one enabled transition may match a source role and status.");
        if (existing is null) _transitions.Add(proposed);
        else existing.ReplaceWith(proposed);
        Version = Guid.NewGuid();
        return existing ?? proposed;
    }

    public void Update(WorkspaceScope scope, string name, bool enabled)
    {
        RequireScope(scope);
        Name = ProjectRules.Name(name);
        IsEnabled = enabled;
        Version = Guid.NewGuid();
    }

    public void RequireScope(WorkspaceScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        scope.Require(OrganizationId, WorkspaceId);
    }
}

public sealed record WorkflowGates(bool RequireCommit = false, bool RequirePassingTests = false,
    bool RequireApproval = false, string? RequiredArtifact = null);

public sealed class WorkflowTransition : BaseEntity<Guid>
{
    public Guid WorkflowDefinitionId { get; private set; }
    public string Key { get; private set; } = "";
    public Guid FromRoleId { get; private set; }
    public WorkItemStatus FromStatus { get; private set; }
    public Guid? ToRoleId { get; private set; }
    public WorkItemStatus ToStatus { get; private set; }
    public bool RequireCommit { get; private set; }
    public bool RequirePassingTests { get; private set; }
    public bool RequireApproval { get; private set; }
    public string? RequiredArtifact { get; private set; }
    public bool IsEnabled { get; private set; }
    public WorkflowTransition() { }

    internal static WorkflowTransition Create(WorkspaceScope scope, Guid workflowId, string key,
        RoleProfile fromRole, WorkItemStatus fromStatus, RoleProfile? toRole, WorkItemStatus toStatus,
        WorkflowGates gates, bool enabled)
    {
        ArgumentNullException.ThrowIfNull(fromRole);
        ArgumentNullException.ThrowIfNull(gates);
        fromRole.RequireScope(scope);
        toRole?.RequireScope(scope);
        if (!fromRole.IsEnabled || toRole is { IsEnabled: false })
            throw new InvalidOperationException("Transition roles must be enabled.");
        if (fromStatus is not (WorkItemStatus.Review or WorkItemStatus.Blocked) ||
            toStatus is not (WorkItemStatus.Ready or WorkItemStatus.Done) ||
            (toStatus == WorkItemStatus.Done && fromStatus != WorkItemStatus.Review))
            throw new ArgumentException("Handoffs start at Review/Blocked and lead to Ready; only Review can complete.");
        if ((toStatus == WorkItemStatus.Ready) != (toRole is not null))
            throw new ArgumentException("A Ready handoff requires a target role; completion must not assign one.");
        return new WorkflowTransition { Id = Guid.NewGuid(), WorkflowDefinitionId = ProjectRules.Id(workflowId),
            Key = key, FromRoleId = ProjectRules.Id(fromRole.Id), FromStatus = fromStatus,
            ToRoleId = toRole?.Id, ToStatus = toStatus, RequireCommit = gates.RequireCommit,
            RequirePassingTests = gates.RequirePassingTests, RequireApproval = gates.RequireApproval,
            RequiredArtifact = WorkRules.Optional(gates.RequiredArtifact, 2000), IsEnabled = enabled };
    }

    internal void ReplaceWith(WorkflowTransition value)
    {
        FromRoleId = value.FromRoleId; FromStatus = value.FromStatus;
        ToRoleId = value.ToRoleId; ToStatus = value.ToStatus;
        RequireCommit = value.RequireCommit; RequirePassingTests = value.RequirePassingTests;
        RequireApproval = value.RequireApproval; RequiredArtifact = value.RequiredArtifact;
        IsEnabled = value.IsEnabled;
    }
}
