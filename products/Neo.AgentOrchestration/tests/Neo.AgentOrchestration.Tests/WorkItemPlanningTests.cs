using Neo.AgentOrchestration.Application.Work;
using Neo.AgentOrchestration.Application.Workspace;
using Neo.AgentOrchestration.Domain.Work;
using Xunit;

namespace Neo.AgentOrchestration.Tests;

public sealed class WorkItemPlanningTests
{
    [Fact]
    public async Task Legacy_default_and_typed_child_criteria_roundtrip_and_filter_before_paging()
    {
        using var f = new WorkFixture();
        var parent = await f.Create("parent");
        Assert.Equal("Task", parent.Item.Type);
        Assert.Null(parent.Item.AcceptanceCriteria);
        var story = await f.Sender.Send(new CreateWorkItem(f.Scope, f.Project.Id, "story", "Story", "web", f.Actor,
            Description: "Original request", ParentWorkItemId: parent.Item.Id, Type: WorkItemType.UserStory,
            AcceptanceCriteria: "  Visible outcome  "), f.Ct);
        Assert.Equal("UserStory", story.Item.Type);
        Assert.Equal("Visible outcome", story.Item.AcceptanceCriteria);
        var board = await f.Sender.Send(new GetWorkBoard(f.Scope, f.Project.Id, Take: 1, Type: WorkItemType.UserStory), f.Ct);
        Assert.Equal(story.Item.Id, Assert.Single(board.Items).Id);
        Assert.Equal(1, board.Total);
        var read = await f.Sender.Send(new GetWorkItem(f.Scope, parent.Item.Id), f.Ct);
        Assert.Equal("UserStory", Assert.Single(read.Children).Type);
    }

    [Fact]
    public async Task Planning_is_versioned_preserves_request_and_retains_criteria_history()
    {
        using var f = new WorkFixture();
        var item = await f.Create("task");
        var changed = await f.Change(item.Item.Id, new PlanningChange(WorkItemType.Bug, "Reproduction no longer fails"));
        Assert.Equal("Bug", changed.Item.Type);
        Assert.Equal(item.Item.Description, changed.Item.Description);
        Assert.NotEqual(item.Item.Version, changed.Item.Version);
        await Assert.ThrowsAsync<WorkItemConflictException>(() => f.Change(item.Item.Id,
            new PlanningChange(WorkItemType.Epic, null), version: item.Item.Version));
        var cleared = await f.Change(item.Item.Id, new PlanningChange(WorkItemType.Bug, null));
        Assert.Null(cleared.Item.AcceptanceCriteria);
        Assert.Contains(cleared.Logs, x => x.Kind == "AcceptanceCriteriaChanged" && x.Message == "Reproduction no longer fails");
        Assert.Contains(cleared.Logs, x => x.Kind == "AcceptanceCriteriaChanged" && x.Message == "(cleared)");
    }

    [Fact]
    public async Task Invalid_type_oversized_criteria_foreign_owner_and_closed_work_are_rejected()
    {
        using var f = new WorkFixture();
        var item = await f.Create("task");
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => f.Change(item.Item.Id, new PlanningChange((WorkItemType)99, null)));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => f.Change(item.Item.Id, new PlanningChange(WorkItemType.Task, new string('x', 8001))));
        await f.Change(item.Item.Id, new StatusChange(WorkItemStatus.Ready));
        await f.Claim(item.Item.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Change(item.Item.Id,
            new PlanningChange(WorkItemType.Epic, null), new WorkActor("other", "other-chat")));
        await f.Change(item.Item.Id, new StatusChange(WorkItemStatus.Review));
        await f.Change(item.Item.Id, new StatusChange(WorkItemStatus.Done));
        await Assert.ThrowsAsync<InvalidOperationException>(() => f.Change(item.Item.Id, new PlanningChange(WorkItemType.Task, "changed after acceptance")));
    }
}
