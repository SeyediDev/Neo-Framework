using Neo.AgentOrchestration.Application.Work;
using Neo.AgentOrchestration.Domain.Projects;
using Neo.AgentOrchestration.Domain.Work;
using Xunit;

namespace Neo.AgentOrchestration.Tests;

public sealed class WorkItemTests
{
    [Fact]
    public async Task Repeat_claim_keeps_one_timer_and_another_chat_cannot_take_over()
    {
        using var f = new WorkFixture();
        var item = await f.Create("task");
        await f.Change(item.Item.Id, new StatusChange(WorkItemStatus.Ready));
        await f.Claim(item.Item.Id);
        f.Clock.Advance(60);
        var resumed = await f.Claim(item.Item.Id);
        Assert.Single(resumed.TimeEntries);
        Assert.Equal(60, resumed.Item.ElapsedSeconds);
        Assert.Equal("Task context remains available.", resumed.Item.Description);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            f.Claim(item.Item.Id, new WorkActor(f.Actor.AgentId, "another-chat")));
        Assert.Equal(f.Actor.ChatId, f.Store.Items.Single().OwnerChatId);
        Assert.Single(f.Store.Items.Single().TimeEntries);
    }

    [Fact]
    public async Task Pause_resume_review_and_archive_preserve_measured_intervals_and_history()
    {
        using var f = new WorkFixture();
        var item = await f.Create("task", estimate: 100);
        var id = item.Item.Id;
        await f.Change(id, new StatusChange(WorkItemStatus.Ready));
        await f.Claim(id);
        f.Clock.Advance(10);
        await f.Change(id, new TrackingChange(false));
        f.Clock.Advance(100);
        var paused = await f.Claim(id);
        Assert.False(paused.Item.IsTracking);
        Assert.Equal(10, paused.Item.ElapsedSeconds);
        await f.Change(id, new TrackingChange(true));
        f.Clock.Advance(40);
        await f.Change(id, new LogChange("Implementation evidence recorded."));
        await f.Change(id, new EvidenceChange(EvidenceKind.Commit, new string('a', 40), EvidenceOutcome.NotApplicable));
        await f.Change(id, new EvidenceChange(EvidenceKind.Test, "flow-check", EvidenceOutcome.Passed, "Actual test result"));
        var review = await f.Change(id, new StatusChange(WorkItemStatus.Review));
        Assert.False(review.Item.IsTracking);
        Assert.Equal(50, review.Item.ElapsedSeconds);
        Assert.Equal(50m, review.Item.BudgetUsedPercent);
        await f.Change(id, new StatusChange(WorkItemStatus.Done));
        await f.Change(id, new ArchiveChange(true));
        f.Clock.Advance(500);
        var restored = await f.Change(id, new ArchiveChange(false));
        Assert.False(restored.Item.IsArchived);
        Assert.Equal("Done", restored.Item.Status);
        Assert.Equal(50, restored.Item.ElapsedSeconds);
        Assert.All(restored.TimeEntries, x => Assert.NotNull(x.EndedAtUtc));
        Assert.Contains(restored.Logs, x => x.Kind == "Note" && x.Message == "Implementation evidence recorded.");
        Assert.Equal(2, restored.Evidence.Count);
    }

    [Fact]
    public async Task A_role_cannot_work_on_two_active_items()
    {
        using var f = new WorkFixture();
        var first = await f.Create("first");
        var second = await f.Create("second");
        await f.Change(first.Item.Id, new StatusChange(WorkItemStatus.Ready));
        await f.Change(second.Item.Id, new StatusChange(WorkItemStatus.Ready));
        await f.Claim(first.Item.Id);
        await Assert.ThrowsAsync<WorkItemConflictException>(() => f.Claim(second.Item.Id));
        await f.Change(first.Item.Id, new StatusChange(WorkItemStatus.Review));
        Assert.Equal("InProgress", (await f.Claim(second.Item.Id)).Item.Status);
    }

    [Fact]
    public async Task A_role_with_capacity_two_can_work_on_two_items_but_not_three()
    {
        using var f = new WorkFixture(roleCapacity: 2);
        var first = await f.Create("first");
        var second = await f.Create("second");
        var third = await f.Create("third");
        foreach (var item in new[] { first, second, third })
            await f.Change(item.Item.Id, new StatusChange(WorkItemStatus.Ready));

        await f.Claim(first.Item.Id);
        await f.Claim(second.Item.Id);
        await Assert.ThrowsAsync<WorkItemConflictException>(() => f.Claim(third.Item.Id));

        await f.Change(first.Item.Id, new StatusChange(WorkItemStatus.Review));
        Assert.Equal("InProgress", (await f.Claim(third.Item.Id)).Item.Status);
    }

    [Fact]
    public async Task Parent_cannot_complete_with_an_open_child_and_child_keeps_full_history()
    {
        using var f = new WorkFixture();
        var parent = await f.Create("parent");
        var child = await f.Create("child", parent.Item.Id);
        await f.Change(parent.Item.Id, new StatusChange(WorkItemStatus.Ready));
        await f.Claim(parent.Item.Id);
        await f.Change(parent.Item.Id, new StatusChange(WorkItemStatus.Review));
        await Assert.ThrowsAsync<WorkItemConflictException>(() => f.Change(parent.Item.Id, new StatusChange(WorkItemStatus.Done)));
        await f.Complete(child.Item.Id);
        var done = await f.Change(parent.Item.Id, new StatusChange(WorkItemStatus.Done));
        Assert.Single(done.Children);
        Assert.Equal("Done", done.Children[0].Status);
        var childDetails = await f.Sender.Send(new GetWorkItem(f.Scope, child.Item.Id), f.Ct);
        Assert.Contains(childDetails.Logs, x => x.Kind == "Claimed");
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Create("late-child", parent.Item.Id));
    }

    [Fact]
    public async Task Dependencies_block_claims_and_cycles_are_rejected()
    {
        using var f = new WorkFixture();
        var first = await f.Create("first");
        var second = await f.Create("second");
        var third = await f.Create("third");
        await f.Change(first.Item.Id, new DependencyChange(second.Item.Id));
        await f.Change(second.Item.Id, new DependencyChange(third.Item.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Change(third.Item.Id, new DependencyChange(first.Item.Id)));
        Assert.Empty(f.Store.Items.Single(x => x.Id == third.Item.Id).Dependencies);
        await f.Change(first.Item.Id, new StatusChange(WorkItemStatus.Ready));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Claim(first.Item.Id));
        await f.Complete(third.Item.Id);
        await f.Complete(second.Item.Id);
        await f.Complete(first.Item.Id);
        Assert.All(f.Store.Items, x => Assert.Equal(WorkItemStatus.Done, x.Status));
    }

    [Fact]
    public async Task Stale_updates_and_invalid_evidence_do_not_mutate_the_item()
    {
        using var f = new WorkFixture();
        var created = await f.Create("task");
        var id = created.Item.Id;
        await f.Change(id, new LogChange("First note"));
        var item = f.Store.Items.Single();
        var version = item.Version;
        var logCount = item.Logs.Count;
        await Assert.ThrowsAsync<WorkItemConflictException>(() =>
            f.Change(id, new EstimateChange(100), version: created.Item.Version));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Change(id,
            new EvidenceChange(EvidenceKind.Commit, "invalid-sha", EvidenceOutcome.NotApplicable)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => f.Change(id, new EstimateChange(-1)));
        Assert.Null(item.EstimatedSeconds);
        Assert.Empty(item.Evidence);
        Assert.Equal(version, item.Version);
        Assert.Equal(logCount, item.Logs.Count);
    }

    [Fact]
    public async Task Duplicate_keys_and_cross_project_relationships_are_rejected()
    {
        using var f = new WorkFixture();
        var first = await f.Create("first");
        await Assert.ThrowsAsync<WorkItemConflictException>(() => f.Create(" FIRST "));
        var other = Project.Create(f.Workspace, "other", "Other");
        f.Store.Projects.Add(other);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Sender.Send(new CreateWorkItem(
            f.Scope, other.Id, "child", "Child", "other", f.Actor, ParentWorkItemId: first.Item.Id), f.Ct));
        var otherItem = await f.Sender.Send(new CreateWorkItem(f.Scope, other.Id, "other-item", "Other", "other", f.Actor), f.Ct);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => f.Change(first.Item.Id, new DependencyChange(otherItem.Item.Id)));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => f.Sender.Send(new GetWorkItem(
            new WorkspaceScope(Guid.NewGuid(), Guid.NewGuid()), first.Item.Id), f.Ct));
        Assert.Equal(2, f.Store.Items.Count);
    }

    [Fact]
    public async Task Archive_is_limited_and_budget_usage_is_not_completion_percentage()
    {
        using var f = new WorkFixture();
        var created = await f.Create("task", estimate: 10);
        var id = created.Item.Id;
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Change(id, new ArchiveChange(true)));
        await f.Change(id, new StatusChange(WorkItemStatus.Ready));
        await f.Claim(id);
        f.Clock.Advance(15);
        var details = await f.Sender.Send(new GetWorkItem(f.Scope, id), f.Ct);
        Assert.Equal(150m, details.Item.BudgetUsedPercent);
        Assert.Equal("InProgress", details.Item.Status);
        await f.Change(id, new StatusChange(WorkItemStatus.Blocked, "Waiting for dependency"));
        var archived = await f.Change(id, new ArchiveChange(true));
        Assert.True(archived.Item.IsArchived);
        Assert.False(archived.Item.IsTracking);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Change(id, new LogChange("Cannot mutate archived item")));
    }

    [Fact]
    public async Task Backwards_time_is_rejected_without_changing_intervals()
    {
        using var f = new WorkFixture();
        var item = await f.Create("task");
        await f.Change(item.Item.Id, new StatusChange(WorkItemStatus.Ready));
        await f.Claim(item.Item.Id);
        f.Clock.Advance(-1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Change(item.Item.Id, new TrackingChange(false)));
        Assert.Null(f.Store.Items.Single().TimeEntries.Single().EndedAtUtc);
    }
}
