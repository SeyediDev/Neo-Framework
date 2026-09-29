using System.Net;
using System.Net.Http.Json;
using Neo.AgentOrchestration.Application.Work;
using Neo.AgentOrchestration.Contracts;
using Neo.AgentOrchestration.Domain.Work;
using Xunit;

namespace Neo.AgentOrchestration.Tests;

public sealed class WorkItemPlanningApiTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Http_create_edit_filter_and_invalid_or_stale_input_keep_existing_data()
    {
        using var f = new WorkFixture();
        await using var api = new ApiFixture(f.Store, f.Clock);
        using var client = api.Client(f.Scope);
        var root = ApiFixture.Root(f.Scope);
        var story = await ApiFixture.Post<WorkItemDetails>(client, root + "/items",
            new CreateWorkItemRequest(f.Project.Id, "story", "Story", "web", "Original request",
                Type: "UserStory", AcceptanceCriteria: "Visible outcome"), HttpStatusCode.Created);
        Assert.Equal("UserStory", story.Item.Type);
        var planning = root + $"/items/{story.Item.Id}/planning";
        using var response = await client.PutAsJsonAsync(planning,
            new SetWorkItemPlanningRequest(story.Item.Version, "Bug", "Corrected outcome"), Ct);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var changed = (await response.Content.ReadFromJsonAsync<WorkItemDetails>(Ct))!;
        Assert.Equal("Original request", changed.Item.Description);
        Assert.Equal("Corrected outcome", changed.Item.AcceptanceCriteria);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync(planning,
            new SetWorkItemPlanningRequest(story.Item.Version, "Epic", null), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(planning,
            new SetWorkItemPlanningRequest(changed.Item.Version, "3", null), Ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(root + "/items?type=unsupported", Ct)).StatusCode);
        var filtered = (await client.GetFromJsonAsync<WorkBoard>(root + $"/items?projectId={f.Project.Id}&domain=web&type=Bug&take=1", Ct))!;
        Assert.Equal(story.Item.Id, Assert.Single(filtered.Items).Id);
        Assert.Equal(1, filtered.Total);
        using var reader = api.Client(f.Scope, ["read"]);
        Assert.Equal(HttpStatusCode.Forbidden, (await reader.PutAsJsonAsync(planning,
            new SetWorkItemPlanningRequest(changed.Item.Version, "Task", null), Ct)).StatusCode);
    }

    [Fact]
    public async Task Sql_criteria_type_and_history_survive_new_context()
    {
        var f = await SqlPersistenceTests.Fixture.Create();
        var created = await f.Handlers.Handle(new CreateWorkItem(f.Scope, f.Project.Id, "typed", "Typed", "web", f.Actor,
            Type: WorkItemType.UserStory, AcceptanceCriteria: "اولین معیار پذیرش"), Ct);
        await f.Change(created.Item.Id, new PlanningChange(WorkItemType.Bug, "معیار اصلاح‌شده"));
        var read = await f.Handlers.Handle(new GetWorkItem(f.Scope, created.Item.Id), Ct);
        Assert.Equal("Bug", read.Item.Type);
        Assert.Equal("معیار اصلاح‌شده", read.Item.AcceptanceCriteria);
        Assert.Contains(read.Logs, x => x.Kind == "AcceptanceCriteriaChanged" && x.Message == "اولین معیار پذیرش");
    }
}
