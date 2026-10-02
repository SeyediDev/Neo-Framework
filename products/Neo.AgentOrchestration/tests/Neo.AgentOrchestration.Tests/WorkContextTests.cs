using System.Text.Json;
using Neo.AgentOrchestration.Contracts;
using Xunit;

namespace Neo.AgentOrchestration.Tests;
public sealed class WorkContextTests
{
    private static WorkItemDetails Sample()
    {
        var now = DateTimeOffset.UtcNow;
        var item = new WorkItemView(Guid.NewGuid(), Guid.NewGuid(), null, "CTX", "Context", "agent",
            new string('d', 8000), "InProgress", "Normal", Guid.NewGuid(), "owner", "chat", "branch",
            false, 30, 100, 30, true, Guid.NewGuid(), now, AcceptanceCriteria: new string('a', 8000));
        return new(item, [], [], Enumerable.Range(0, 100).Select(i => new WorkLogView(Guid.NewGuid(),
            "owner", "chat", "Note", new string('x', 8000), now.AddSeconds(i))).ToArray(), [], [], []);
    }
    [Fact]
    public void Brief_is_bounded_explicit_and_does_not_destroy_source()
    {
        var full = Sample(); var brief = WorkContextProjection.Create(full);
        Assert.Equal(3, brief.RecentLogs.Count); Assert.Equal(100, brief.TotalLogs);
        Assert.Equal(2000, brief.Item.Description!.Length);
        Assert.Contains("item.description", brief.Omitted); Assert.Contains("olderLogs", brief.Omitted);
        Assert.Equal(8000, full.Item.Description!.Length);
        Assert.True(JsonSerializer.Serialize(brief).Length < JsonSerializer.Serialize(full).Length / 10);
        var same = WorkContextProjection.Create(full, full.Item.Version);
        Assert.True(same.Unchanged); Assert.Empty(same.RecentLogs);
        Assert.Equal(full.Item.OwnerChatId, same.Item.OwnerChatId);
        Assert.Equal(full.Item.ElapsedSeconds, same.Item.ElapsedSeconds);
        Assert.False(WorkContextProjection.Create(full, Guid.NewGuid()).Unchanged);
    }
    [Fact]
    public void History_pages_reconstruct_original_logs_including_equal_timestamps()
    {
        var full = Sample(); full = full with { Logs = full.Logs.Select(x => x with { CreatedAtUtc = full.Item.UpdatedAtUtc }).ToArray() };
        var ids = new List<Guid>(); int? skip = 0;
        while (skip.HasValue) { var page = WorkContextProjection.History(full, skip.Value, 7); ids.AddRange(page.Logs.Select(x => x.Id)); skip = page.NextSkip; }
        Assert.Equal(100, ids.Distinct().Count());
        Assert.Equal(full.Logs.OrderBy(x => x.Id).Select(x => x.Id), ids);
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkContextProjection.History(full, -1, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => WorkContextProjection.History(full, 0, 21));
    }
    [Fact]
    public void Unchanged_parent_still_reports_current_child_and_live_time()
    {
        var full = Sample(); var child = full.Item with { Id = Guid.NewGuid(), Status = "Done", Version = Guid.NewGuid() };
        full = full with { Children = [child] };
        var brief = WorkContextProjection.Create(full, full.Item.Version);
        Assert.True(brief.Unchanged); Assert.Equal("Done", Assert.Single(brief.Children).Status);
        Assert.Equal(child.Version, brief.Children[0].Version);
    }
}
