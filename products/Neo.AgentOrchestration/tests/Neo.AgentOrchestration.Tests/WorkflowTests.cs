using Neo.AgentOrchestration.Application.Workflows;
using Neo.AgentOrchestration.Contracts;
using Neo.AgentOrchestration.Domain.Agents;
using Neo.AgentOrchestration.Domain.Projects;
using Neo.AgentOrchestration.Domain.Work;
using Neo.AgentOrchestration.Domain.Workflows;
using Xunit;

namespace Neo.AgentOrchestration.Tests;

public sealed class WorkflowTests
{
    [Fact]
    public void Satisfied_gates_select_next_agent_without_mutating_or_dispatching()
    {
        var f = new Fixture(new(true, true, true, "build-package"));
        f.Commit(); f.Test(EvidenceOutcome.Passed); f.Artifact(); f.Approve();
        var version = f.Item.Version;
        var preview = f.Preview();
        Assert.True(preview.GatesSatisfied);
        Assert.Empty(preview.UnmetConditions);
        Assert.Equal(f.ReviewerRole.Id, preview.TargetRoleId);
        Assert.Equal(f.Profile.Id, preview.AgentProfileId);
        Assert.Equal("Ready", preview.TargetStatus);
        Assert.Equal(version, preview.WorkItemVersion);
        Assert.Equal(version, f.Item.Version);
        Assert.Equal(WorkItemStatus.Review, f.Item.Status);
        Assert.Equal(f.Owner.AgentId, f.Item.OwnerAgentId);
    }

    [Fact]
    public void Each_missing_gate_has_an_explicit_reason()
    {
        var f = new Fixture(new(true, true, true, "build-package"));
        var preview = f.Preview();
        Assert.False(preview.GatesSatisfied);
        Assert.Null(preview.AgentProfileId);
        Assert.Equal(new[] { "commit-required", "passing-tests-required", "artifact-required", "approval-required" }, preview.UnmetConditions);
    }

    [Fact]
    public void Latest_test_result_wins_even_when_timestamps_are_equal()
    {
        var f = new Fixture(new(true, true));
        f.Commit(); f.Test(EvidenceOutcome.Passed); f.Test(EvidenceOutcome.Failed);
        Assert.Contains("passing-tests-required", f.Preview().UnmetConditions);
        f.Test(EvidenceOutcome.Skipped);
        Assert.False(f.Preview().GatesSatisfied);
        f.Test(EvidenceOutcome.Passed);
        Assert.True(f.Preview().GatesSatisfied);
        f.Test(EvidenceOutcome.Failed, "another-test");
        Assert.False(f.Preview().GatesSatisfied);
        Assert.Equal(Enumerable.Range(1, 6), f.Item.Evidence.Select(x => x.Sequence));
    }

    [Fact]
    public void Old_commit_and_unbound_evidence_do_not_validate_a_new_commit()
    {
        var f = new Fixture(new(true, true, RequiredArtifact: "build-package"));
        f.Commit(); f.Test(EvidenceOutcome.Passed); f.Artifact();
        Assert.True(f.Preview().GatesSatisfied);
        f.Sha = new string('b', 40); f.Commit();
        Assert.False(f.Preview().GatesSatisfied);
        f.Item.AddEvidence(f.Scope, f.Owner, EvidenceKind.Test, "unbound", EvidenceOutcome.Passed, null, f.Now);
        f.Item.AddEvidence(f.Scope, f.Owner, EvidenceKind.Artifact, "build-package", EvidenceOutcome.NotApplicable, null, f.Now);
        Assert.False(f.Preview().GatesSatisfied);
        f.Test(EvidenceOutcome.Passed); f.Artifact();
        Assert.True(f.Preview().GatesSatisfied);
    }

    [Fact]
    public void Approval_expires_after_item_or_workflow_changes()
    {
        var f = new Fixture(new(RequireApproval: true));
        f.Approve();
        Assert.True(f.Preview().GatesSatisfied);
        f.Item.AddLog(f.Scope, f.Owner, "New information", f.Now);
        Assert.False(f.Preview().GatesSatisfied);
        f.Approve();
        Assert.True(f.Preview().GatesSatisfied);
        f.Workflow.Update(f.Scope, "Updated workflow", true);
        Assert.False(f.Preview().GatesSatisfied);
        f.Approve();
        Assert.True(f.Preview().GatesSatisfied);
    }

    [Fact]
    public void Self_approval_and_rejected_or_future_approval_cannot_pass()
    {
        var f = new Fixture(new(RequireApproval: true));
        Assert.Throws<InvalidOperationException>(() => WorkflowApproval.Record(f.Scope, f.Workflow,
            f.Transition.Id, f.Item, new WorkActor(f.Owner.AgentId, "another-chat"), true, "Self approval", f.Now));
        f.Approve(); f.Approve(false); // equal timestamps fail closed
        Assert.False(f.Preview().GatesSatisfied);
        f.Now = f.Now.AddSeconds(1); f.Approve();
        Assert.True(f.Preview().GatesSatisfied);
        f.Approvals.Clear(); f.Now = f.Now.AddSeconds(10); f.Approve(); f.Now = f.Now.AddSeconds(-10);
        Assert.False(f.Preview().GatesSatisfied);
    }

    [Fact]
    public void Invalid_configuration_is_atomic_and_ambiguous_routes_are_rejected()
    {
        var f = new Fixture(new());
        var version = f.Workflow.Version;
        Assert.Throws<InvalidOperationException>(() => f.Workflow.ConfigureTransition(f.Scope, "duplicate",
            f.Developer, WorkItemStatus.Review, f.ReviewerRole, WorkItemStatus.Ready, new()));
        Assert.Throws<ArgumentException>(() => f.Workflow.ConfigureTransition(f.Scope, "handoff",
            f.Developer, WorkItemStatus.Done, f.ReviewerRole, WorkItemStatus.Ready, new()));
        Assert.Throws<ArgumentException>(() => f.Workflow.ConfigureTransition(f.Scope, "handoff",
            f.Developer, WorkItemStatus.Review, null, WorkItemStatus.Ready, new()));
        Assert.Equal(version, f.Workflow.Version);
        Assert.Single(f.Workflow.Transitions);
        Assert.Equal(f.ReviewerRole.Id, f.Transition.ToRoleId);
        var updated = f.Workflow.ConfigureTransition(f.Scope, "handoff", f.Developer,
            WorkItemStatus.Review, f.ReviewerRole, WorkItemStatus.Ready, new(RequireCommit: true));
        Assert.Equal(f.Transition.Id, updated.Id);
        Assert.NotEqual(version, f.Workflow.Version);
        Assert.False(f.Preview().GatesSatisfied);
    }

    [Fact]
    public void Completion_has_no_next_agent_and_requires_review()
    {
        var f = new Fixture(new());
        var terminal = f.Workflow.ConfigureTransition(f.Scope, "handoff", f.Developer,
            WorkItemStatus.Review, null, WorkItemStatus.Done, new());
        var plan = f.Preview();
        Assert.True(plan.GatesSatisfied);
        Assert.Equal("Done", plan.TargetStatus);
        Assert.Null(plan.TargetRoleId); Assert.Null(plan.AgentProfileId);
        Assert.Throws<ArgumentException>(() => f.Workflow.ConfigureTransition(f.Scope, "handoff", f.Developer,
            WorkItemStatus.Blocked, null, WorkItemStatus.Done, new()));
        Assert.Equal(terminal.Id, plan.TransitionId);
    }

    [Fact]
    public void Disabled_roles_workflow_and_archived_or_wrong_status_items_fail_closed()
    {
        var f = new Fixture(new());
        f.ReviewerRole.SetEnabled(f.Scope, false);
        Assert.Contains("target-role-unavailable", f.Preview().UnmetConditions);
        f.ReviewerRole.SetEnabled(f.Scope, true);
        f.Workflow.Update(f.Scope, "Disabled", false);
        Assert.Contains("workflow-disabled", f.Preview().UnmetConditions);
        f.Workflow.Update(f.Scope, "Enabled", true);
        f.Item.ChangeStatus(f.Scope, f.Owner, WorkItemStatus.Blocked, f.Now);
        Assert.Contains("source-mismatch", f.Preview().UnmetConditions);
        f.Item.Archive(f.Scope, f.Owner, f.Now);
        Assert.Contains("item-archived", f.Preview().UnmetConditions);
    }

    [Fact]
    public void Foreign_projects_workspaces_and_ambiguous_agents_are_rejected()
    {
        var f = new Fixture(new());
        var other = Project.Create(f.Workspace, "other", "Other");
        var foreign = WorkflowDefinition.Create(f.Scope, other, "other", "Other");
        Assert.Throws<InvalidOperationException>(() => WorkflowPlanner.Preview(f.Scope, foreign,
            f.Transition.Id, f.Item, f.Roles, f.Agents, f.Approvals, f.Now));
        Assert.Throws<InvalidOperationException>(() => WorkflowPlanner.Preview(new(Guid.NewGuid(), Guid.NewGuid()),
            f.Workflow, f.Transition.Id, f.Item, f.Roles, f.Agents, f.Approvals, f.Now));
        f.Agents.Add(AgentProfile.Create(f.Scope, f.ReviewerRole, "another", "Another", "fake"));
        Assert.Throws<InvalidOperationException>(() => f.Preview());
        Assert.Equal(f.Profile.Id, WorkflowPlanner.Preview(f.Scope, f.Workflow, f.Transition.Id,
            f.Item, f.Roles, f.Agents, f.Approvals, f.Now, f.Profile.Id).AgentProfileId);
    }

    [Fact]
    public void Evidence_cannot_reference_a_commit_missing_from_the_item()
    {
        var f = new Fixture(new());
        var version = f.Item.Version;
        Assert.Throws<InvalidOperationException>(() => f.Test(EvidenceOutcome.Passed));
        Assert.Equal(version, f.Item.Version); Assert.Empty(f.Item.Evidence);
    }

    private sealed class Fixture
    {
        public Workspace Workspace { get; } = Workspace.Create(Organization.Create("org", "Org"), "work", "Work");
        public WorkspaceScope Scope => Workspace.Scope;
        public WorkActor Owner { get; } = new("developer-agent", "developer-chat");
        public WorkActor Reviewer { get; } = new("reviewer-agent", "reviewer-chat");
        public DateTimeOffset Now { get; set; } = new(2026, 9, 26, 0, 0, 0, TimeSpan.Zero);
        public string Sha { get; set; } = new('a', 40);
        public RoleProfile Developer { get; }
        public RoleProfile ReviewerRole { get; }
        public AgentProfile Profile { get; }
        public List<RoleProfile> Roles { get; }
        public List<AgentProfile> Agents { get; }
        public List<WorkflowApproval> Approvals { get; } = [];
        public WorkflowDefinition Workflow { get; }
        public WorkflowTransition Transition { get; }
        public WorkItem Item { get; }
        public Fixture(WorkflowGates gates)
        {
            var project = Project.Create(Workspace, "project", "Project");
            Developer = RoleProfile.Create(Workspace, "dev", "Developer");
            ReviewerRole = RoleProfile.Create(Workspace, "review", "Reviewer");
            Profile = AgentProfile.Create(Scope, ReviewerRole, "review-agent", "Reviewer", "fake");
            Roles = [Developer, ReviewerRole]; Agents = [Profile];
            Workflow = WorkflowDefinition.Create(Scope, project, "main", "Main flow");
            Transition = Workflow.ConfigureTransition(Scope, "handoff", Developer, WorkItemStatus.Review,
                ReviewerRole, WorkItemStatus.Ready, gates);
            Item = WorkItem.Create(Scope, project, "task", "Task", "domain", Owner, Now);
            Item.ChangeStatus(Scope, Owner, WorkItemStatus.Ready, Now);
            Item.Claim(Scope, Developer, Owner, "develop", Now);
            Item.ChangeStatus(Scope, Owner, WorkItemStatus.Review, Now);
        }
        public WorkflowPlan Preview() => WorkflowPlanner.Preview(Scope, Workflow, Transition.Id, Item, Roles, Agents, Approvals, Now);
        public void Commit() => Item.AddEvidence(Scope, Owner, EvidenceKind.Commit, Sha, EvidenceOutcome.NotApplicable, null, Now);
        public void Test(EvidenceOutcome outcome, string name = "tests")
            => Item.AddEvidence(Scope, Owner, EvidenceKind.Test, name, outcome, null, Now, Sha);
        public void Artifact() => Item.AddEvidence(Scope, Owner, EvidenceKind.Artifact, "build-package", EvidenceOutcome.NotApplicable, null, Now, Sha);
        public void Approve(bool approved = true) => Approvals.Add(WorkflowApproval.Record(Scope, Workflow, Transition.Id,
            Item, Reviewer, approved, "Review outcome", Now));
    }
}
