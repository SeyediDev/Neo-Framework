using System.Net;
using Neo.AgentOrchestration.Contracts;
using Neo.AgentOrchestration.Web.Pages;
using Xunit;
namespace Neo.AgentOrchestration.Tests;
public sealed class KanbanMoveTests
{
    [Theory]
    [InlineData("Backlog", "Ready,Cancelled")]
    [InlineData("Ready", "InProgress,Blocked,Cancelled")]
    [InlineData("InProgress", "Review,Blocked,Cancelled")]
    [InlineData("Blocked", "Ready,Cancelled")]
    [InlineData("Review", "Ready,Done,Blocked,Cancelled")]
    [InlineData("Done", "")]
    [InlineData("Cancelled", "")]
    [InlineData("Unknown", "")]
    public void Presentation_only_offers_lifecycle_destinations(string state, string expected)
    {
        Assert.Equal(expected, string.Join(',', BoardModel.MoveDestinations(state)));
        Assert.Empty(BoardModel.MoveDestinations(state, true));
    }
    [Fact]
    public async Task Board_forms_use_claim_for_inprogress_and_enforce_versions_permissions_and_scope()
    {
        var f = await SqlPersistenceTests.Fixture.Create();
        await using var web = await WebFixture.Create(f);
        web.Client.DefaultRequestHeaders.Add("X-Neo-Navigation", "1");
        var created = await web.Submit(web.Root + "/new", null, new() {
            ["ProjectId"] = f.Project.Id.ToString(), ["Key"] = "drag-test", ["Title"] = "Kanban move", ["Domain"] = "web" });
        Assert.Equal(HttpStatusCode.Redirect, created.StatusCode);
        var id = Guid.Parse(created.Headers.Location!.ToString().Split('/').Last());
        var board = web.Root + $"?ProjectId={f.Project.Id}&Domain=web";
        var html = await web.Html(board);
        Assert.Contains("move-menu", html);
        Assert.Contains("گزینه‌های انتقال", System.Net.WebUtility.HtmlDecode(html));
        Assert.DoesNotContain("class=\"move-handle quiet\"", html);
        Assert.Contains("data-draft-key=\"move-" + id, html);
        async Task<WorkItemDetails> Read() => await web.ApiRead<WorkItemDetails>($"items/{id}");
        async Task<HttpResponseMessage> Move(string destination, Guid? version = null, Guid? role = null) =>
            await web.Submit(board, "Move", new() { ["MoveItemId"] = id.ToString(),
                ["MoveVersion"] = (version ?? (await Read()).Item.Version).ToString(),
                ["Destination"] = destination, ["ClaimRoleId"] = (role ?? Guid.Empty).ToString(), ["Branch"] = "kanban-test" });
        Assert.Equal(HttpStatusCode.Conflict, (await Move("Done")).StatusCode);
        var old = (await Read()).Item.Version;
        var ready = await Move("Ready");
        Assert.Equal(HttpStatusCode.Redirect, ready.StatusCode);
        Assert.Contains("Domain=web", ready.Headers.Location!.ToString());
        Assert.Contains("ProjectId=" + f.Project.Id, ready.Headers.Location.ToString());
        Assert.Equal(HttpStatusCode.Conflict, (await Move("Blocked", old)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Move("InProgress")).StatusCode);
        Assert.Equal("Ready", (await Read()).Item.Status);
        // A hidden/unselected claim role is irrelevant to an ordinary status move.
        Assert.Equal(HttpStatusCode.Redirect, (await web.Submit(board, "Move", new() {
            ["MoveItemId"] = id.ToString(), ["MoveVersion"] = (await Read()).Item.Version.ToString(),
            ["Destination"] = "Blocked", ["ClaimRoleId"] = "" })).StatusCode);
        Assert.Equal("Blocked", (await Read()).Item.Status);
        Assert.Equal(HttpStatusCode.Redirect, (await Move("Ready")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await Move("InProgress", role:f.Role.Id)).StatusCode);
        var claimed = await Read();
        Assert.Equal("InProgress", claimed.Item.Status);
        Assert.Equal(f.Role.Id, claimed.Item.OwnerRoleId);
        Assert.True(claimed.Item.IsTracking);
        Assert.Contains(claimed.Logs, x => x.Kind == "Claimed");
        f.Clock.Advance(7);
        Assert.Equal(HttpStatusCode.Redirect, (await Move("Review")).StatusCode);
        var reviewed = await Read();
        Assert.False(reviewed.Item.IsTracking);
        Assert.Equal(7, reviewed.Item.ElapsedSeconds);
        // A real unfinished child continues to prevent completion through the board.
        await web.Submit(web.Root + "/new", null, new() { ["ProjectId"] = f.Project.Id.ToString(),
            ["Key"] = "drag-child", ["Title"] = "Child gate", ["Domain"] = "web", ["ParentWorkItemId"] = id.ToString() });
        Assert.Equal(HttpStatusCode.Conflict, (await Move("Done")).StatusCode);
        await using var denied = await WebFixture.Create(f, ["read"]);
        denied.Client.DefaultRequestHeaders.Add("X-Neo-Navigation", "1");
        Assert.Equal(HttpStatusCode.Forbidden, (await denied.Submit(board, "Move", new() {
            ["MoveItemId"] = id.ToString(), ["MoveVersion"] = (await Read()).Item.Version.ToString(), ["Destination"] = "Blocked" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await web.Client.PostAsync(board + "&handler=Move",
            new FormUrlEncodedContent(new Dictionary<string,string>{{"Destination","Blocked"}}), TestContext.Current.CancellationToken)).StatusCode);
        Assert.Equal("Review", (await Read()).Item.Status);
    }
}
