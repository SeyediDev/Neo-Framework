using System.Net;
using Microsoft.EntityFrameworkCore;
using Neo.AgentOrchestration.Application.Work;
using Neo.AgentOrchestration.Application.Workspace;
using Neo.AgentOrchestration.Contracts;
using Neo.AgentOrchestration.Web;
using Xunit;

namespace Neo.AgentOrchestration.Tests;

public sealed class RoleHistoryTests
{
    [Fact]
    public async Task Role_grouping_prefers_current_owner_and_keeps_unknown_history_without_forged_assignment()
    {
        using var f = new WorkFixture();
        var item = (await f.Create("historical")).Item;
        var first = new RoleProfileView(Guid.NewGuid(), "DEVELOPER", "Developer", null, true);
        var second = new RoleProfileView(Guid.NewGuid(), "REVIEWER", "Reviewer", null, true);
        var history = new WorkItemOwnerHistoryView(Guid.NewGuid(), 1, 4, "developer", "old-agent", "old-chat", DateTimeOffset.UtcNow);
        var historical = item with { LastOwnerHistory = history };
        var current = item with { Id = Guid.NewGuid(), OwnerRoleId = second.Id, LastOwnerHistory = history };
        var unknown = item with { Id = Guid.NewGuid(), LastOwnerHistory = history with { OwnerRole = "retired-role" } };
        var groups = RoleWorkGrouping.Create([historical, current, unknown, item], [first, second], null);
        Assert.Equal(historical.Id, Assert.Single(groups.Single(x => x.RoleId == first.Id).Items).Id);
        Assert.Equal(current.Id, Assert.Single(groups.Single(x => x.RoleId == second.Id).Items).Id);
        Assert.Equal(unknown.Id, Assert.Single(groups.Single(x => x.Name.Contains("retired-role")).Items).Id);
        Assert.Equal(item.Id, Assert.Single(groups.Single(x => x.Name == "بدون رول").Items).Id);
        Assert.Null(historical.OwnerRoleId);
        Assert.Equal(historical.Id, Assert.Single(Assert.Single(RoleWorkGrouping.Create([historical, current], [first, second], first.Id)).Items).Id);
        Assert.Equal(current.Id, Assert.Single(Assert.Single(RoleWorkGrouping.Create([historical, current], [first, second], second.Id)).Items).Id);
    }

    [Fact]
    public async Task Sql_history_projects_to_api_and_filtered_role_page_without_changing_current_role_filter()
    {
        var f = await SqlPersistenceTests.Fixture.Create();
        var item = await f.CreateItem("history-only");
        var recordId = Guid.NewGuid();
        var at = DateTimeOffset.UtcNow;
        await using (var db = f.Factory.CreateDbContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO nao.WorkItemOwnerHistory
                (Id,ProjectId,WorkItemId,SourceWorkItemId,OriginalStatus,OwnerRole,OwnerAgent,OwnerChat,CreatedAtUtc)
                VALUES ({recordId},{f.Project.Id},{item.Id},{1L},{4},{f.Role.Key},{"historical-agent"},{"historical-chat"},{at})
                """, TestContext.Current.CancellationToken);
        }
        var board = await new WorkspaceHandlers(f.Store, f.Clock).Handle(new GetWorkBoard(f.Scope), TestContext.Current.CancellationToken);
        var projected = Assert.Single(board.Items);
        Assert.Equal(recordId, projected.LastOwnerHistory?.Id);
        Assert.Null(projected.OwnerRoleId);
        var availability = await new WorkspaceHandlers(f.Store, f.Clock).Handle(new GetWorkBoard(f.Scope, RoleId: f.Role.Id), TestContext.Current.CancellationToken);
        Assert.Empty(availability.Items);
        await using var web = await WebFixture.Create(f);
        var html = WebUtility.HtmlDecode(await web.Html(web.Root + $"/roles?RoleId={f.Role.Id}&ProjectId={f.Project.Id}"));
        Assert.Contains($"data-item-id=\"{item.Id}\"", html);
        Assert.Contains("رول تاریخی", html);
        Assert.Contains("historical-agent", html);
        Assert.DoesNotContain($"data-item-id=\"{item.Id}\"", await web.Html(web.Root + $"/roles?RoleId={f.Role.Id}&Domain=missing"));
        var details = await f.Handlers.Handle(new GetWorkItem(f.Scope, item.Id), TestContext.Current.CancellationToken);
        Assert.Null(details.Item.OwnerRoleId);
        Assert.Equal(item.Version, details.Item.Version);
        Assert.Single(details.OwnerHistory);
    }
}
