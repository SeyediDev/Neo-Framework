using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Neo.AgentOrchestration.Application.ExternalAgents;
using Neo.AgentOrchestration.Infrastructure.ExternalAgents;
using Xunit;

namespace Neo.AgentOrchestration.Tests;

public sealed partial class NativeAgentAdapterTests
{
    [Theory]
    [InlineData(false, "per_pending")]
    [InlineData(true, null)]
    public async Task OpenCode_pending_snapshot_is_scoped_read_only_and_never_selects_an_ambiguous_grant(bool multiple, string? expected)
    {
        await using var server = await NativeServer.Start(async ctx =>
        {
            if (ctx.Request.Path == "/session") { await ctx.Response.WriteAsJsonAsync(new { id = "ses_pending" }); return; }
            Assert.Equal("/permission", ctx.Request.Path.Value); Assert.Equal("GET", ctx.Request.Method);
            var requests = new List<object> { new { id = "per_other", sessionID = "ses_other" },
                new { id = "per_pending", sessionID = "ses_pending", permission = "bash", patterns = new[] { "private-command" } } };
            if (multiple) requests.Add(new { id = "per_second", sessionID = "ses_pending" });
            await ctx.Response.WriteAsJsonAsync(requests);
        });
        using var services = Services(server, "opencode", permissionSnapshot: true);
        var scope = Scope(); var adapter = services.GetRequiredService<NativeAgentAdapterFactory>().Create("pilot", scope);
        var handle = await adapter.PrepareAsync(new(scope, "bounded"), TestContext.Current.CancellationToken);
        var observed = await adapter.ObserveAsync(handle, TestContext.Current.CancellationToken);
        Assert.Equal(ExternalAgentState.AwaitingApproval, observed.State); Assert.Empty(observed.Usage);
        Assert.Equal(expected, observed.NativeApprovalId); Assert.DoesNotContain("private-command", observed.ToString());
        Assert.Equal(2, server.Requests.Count);
        Assert.All(server.Requests, r => Assert.DoesNotContain("reply", r.Path));
    }

    [Fact]
    public async Task OpenCode_unrelated_permission_does_not_block_own_session_and_snapshot_opt_in_fences_handles()
    {
        var scope = Scope();
        await using var server = await NativeServer.Start(async ctx =>
        {
            if (await CursorCommon(ctx)) return;
            if (ctx.Request.Path == "/permission")
            { await ctx.Response.WriteAsJsonAsync(new[] { new { id = "per_other", sessionID = "ses_other" } }); return; }
            await ctx.Response.WriteAsJsonAsync(new[] { CursorRow(scope, 1) });
        });
        using var legacy = Services(server, "opencode"); using var services = Services(server, "opencode", permissionSnapshot: true);
        var original = legacy.GetRequiredService<NativeAgentAdapterFactory>().Create("pilot", scope);
        var adapter = services.GetRequiredService<NativeAgentAdapterFactory>().Create("pilot", scope);
        var originalHandle = await original.PrepareAsync(new(scope, "bounded"), TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<ExternalAgentException>(() => adapter.ObserveAsync(originalHandle, TestContext.Current.CancellationToken));
        Assert.Equal("native-agent-binding-changed", error.Code); Assert.Single(server.Requests);
        var handle = await adapter.PrepareAsync(new(scope, "bounded"), TestContext.Current.CancellationToken);
        Assert.Equal(ExternalAgentState.Completed, (await adapter.ObserveAsync(handle, TestContext.Current.CancellationToken)).State);
        Assert.NotEqual(original.BindingFingerprint, adapter.BindingFingerprint);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[{\"id\":\"per_unknown\"}]")]
    [InlineData("[{\"id\":\"private-invalid\",\"sessionID\":\"ses_cursor\"}]")]
    [InlineData("[{\"id\":\"per_one\",\"sessionID\":\"ses_cursor\"},{\"id\":\"per_one\",\"sessionID\":\"ses_cursor\"}]")]
    public async Task OpenCode_malformed_pending_snapshot_fails_closed_without_raw_details(string json)
    {
        await using var server = await NativeServer.Start(async ctx =>
        { if (!await CursorCommon(ctx)) await ctx.Response.WriteAsync(json); });
        using var services = Services(server, "opencode", permissionSnapshot: true); var scope = Scope();
        var adapter = services.GetRequiredService<NativeAgentAdapterFactory>().Create("pilot", scope);
        var handle = await adapter.PrepareAsync(new(scope, "bounded"), TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<ExternalAgentException>(() => adapter.ObserveAsync(handle, TestContext.Current.CancellationToken));
        Assert.Equal("native-agent-permission-snapshot-invalid", error.Code); Assert.Null(error.InnerException);
        Assert.DoesNotContain("private-invalid", error.ToString()); Assert.Equal(2, server.Requests.Count);
    }
}
