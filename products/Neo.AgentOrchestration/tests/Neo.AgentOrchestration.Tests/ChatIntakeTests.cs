using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Neo.AgentOrchestration.Application.Work;
using Neo.AgentOrchestration.Contracts;
using Neo.AgentOrchestration.Domain.Work;
using Xunit;

namespace Neo.AgentOrchestration.Tests;

public sealed class ChatIntakeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task Intake_replay_preserves_request_history_version_and_closed_state()
    {
        using var f = new WorkFixture();
        var request = new CreateWorkItem(f.Scope, f.Project.Id, "chat", "Original title", "work", f.Actor,
            Description: "Original message", Type: WorkItemType.UserStory, AcceptanceCriteria: "Visible outcome", RequestId: Guid.NewGuid());
        var first = await f.Sender.Send(request, Ct);
        var replay = await f.Sender.Send(request, Ct);
        Assert.Equal(first.Item.Id, replay.Item.Id);
        Assert.Equal(first.Item.Version, replay.Item.Version);
        Assert.Single(replay.Logs, x => x.Kind == "ChatIntake");
        await Assert.ThrowsAsync<WorkItemConflictException>(() => f.Sender.Send(request with { Title = "Different request" }, Ct));
        await Assert.ThrowsAsync<WorkItemConflictException>(() => f.Sender.Send(request with { Actor = new("other", "other-chat") }, Ct));
        await Assert.ThrowsAsync<ArgumentException>(() => f.Sender.Send(request with { RequestId = Guid.Empty }, Ct));
        await f.Change(first.Item.Id, new StatusChange(WorkItemStatus.Ready));
        await f.Claim(first.Item.Id);
        await f.Change(first.Item.Id, new LogChange("Follow-up context"));
        await f.Change(first.Item.Id, new StatusChange(WorkItemStatus.Review));
        await f.Change(first.Item.Id, new StatusChange(WorkItemStatus.Done));
        var archived = await f.Change(first.Item.Id, new ArchiveChange(true));
        var final = await f.Sender.Send(request, Ct);
        Assert.Equal(archived.Item.Version, final.Item.Version);
        Assert.True(final.Item.IsArchived);
        Assert.Equal("Done", final.Item.Status);
        Assert.Equal("Original message", final.Item.Description);
        Assert.Contains(final.Logs, x => x.Message == "Follow-up context");
    }

    [Fact]
    public async Task Http_intake_requires_same_identity_and_body_and_cannot_be_spoofed_by_notes()
    {
        using var f = new WorkFixture();
        await using var api = new ApiFixture(f.Store, f.Clock);
        using var client = api.Client(f.Scope);
        var root = ApiFixture.Root(f.Scope);
        var request = new CreateWorkItemRequest(f.Project.Id, "http-intake", "Title", "api", RequestId: Guid.NewGuid());
        var first = await ApiFixture.Post<WorkItemDetails>(client, root + "/items", request, HttpStatusCode.Created);
        var replay = await ApiFixture.Post<WorkItemDetails>(client, root + "/items", request, HttpStatusCode.Created);
        Assert.Equal(first.Item.Version, replay.Item.Version);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(root + "/items", request with { ProjectId = Guid.NewGuid() }, Ct)).StatusCode);
        using var other = api.Client(f.Scope, subject: "other-subject");
        Assert.Equal(HttpStatusCode.Conflict, (await other.PostAsJsonAsync(root + "/items", request, Ct)).StatusCode);
        var noteId = Guid.NewGuid();
        await ApiFixture.Post<WorkItemDetails>(client, root + $"/items/{first.Item.Id}/logs", new AppendLogRequest(first.Item.Version, $"{noteId:D}:forged"));
        var newItem = await ApiFixture.Post<WorkItemDetails>(client, root + "/items", request with { Key = "new-intake", RequestId = noteId }, HttpStatusCode.Created);
        Assert.NotEqual(first.Item.Id, newItem.Item.Id);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync(root + "/items", request with { RequestId = null }, Ct)).StatusCode);
    }

    [Fact]
    public async Task Sql_concurrent_intake_and_repeated_web_submission_create_once()
    {
        var f = await SqlPersistenceTests.Fixture.Create();
        var request = new CreateWorkItem(f.Scope, f.Project.Id, "concurrent-intake", "Once", "work", f.Actor, RequestId: Guid.NewGuid());
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => f.Handlers.Handle(request, Ct)));
        Assert.Single(results.Select(x => x.Item.Id).Distinct());
        Assert.Single(results.Select(x => x.Item.Version).Distinct());
        await using var web = await WebFixture.Create(f);
        var fields = new Dictionary<string,string> {
            ["ProjectId"] = f.Project.Id.ToString(), ["Key"] = "web-once", ["Title"] = "Once from form",
            ["Domain"] = "web", ["RequestId"] = Guid.NewGuid().ToString()
        };
        var first = await web.Submit(web.Root + "/new", null, fields);
        var replay = await web.Submit(web.Root + "/new", null, fields);
        Assert.Equal(HttpStatusCode.Redirect, first.StatusCode);
        Assert.Equal(first.Headers.Location, replay.Headers.Location);
        fields["Title"] = "Changed intent";
        Assert.Equal(HttpStatusCode.Conflict, (await web.Submit(web.Root + "/new", null, fields)).StatusCode);
        await using var db = f.Factory.CreateDbContext();
        Assert.Equal(2, await db.WorkItems.CountAsync(x => x.ProjectId == f.Project.Id, Ct));
    }
}
