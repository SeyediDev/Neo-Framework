using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Neo.AgentOrchestration.Application.ExternalAgents;
using Neo.AgentOrchestration.Infrastructure.ExternalAgents;
using Xunit;

namespace Neo.AgentOrchestration.Tests;

public sealed partial class NativeAgentAdapterTests
{
    [Fact]
    public async Task OpenCode_cursor_reads_all_scoped_pages_encodes_cursor_ignores_link_and_reports_once()
    {
        var scope = Scope(); const string cursor = "opaque+/=?&before=not-a-route";
        var queries = new ConcurrentQueue<string>();
        await using var server = await NativeServer.Start(async ctx =>
        {
            if (await CursorCommon(ctx)) return;
            queries.Enqueue(ctx.Request.QueryString.Value!);
            if (ctx.Request.Query["before"].Count == 0)
            {
                ctx.Response.Headers["X-Next-Cursor"] = cursor;
                ctx.Response.Headers.Link = "<https://unapproved.invalid/private>; rel=\"next\"";
                await ctx.Response.WriteAsJsonAsync(Enumerable.Range(3, 100).Select(i => CursorRow(scope, i)));
            }
            else
            {
                Assert.Equal(cursor, ctx.Request.Query["before"].ToString());
                await ctx.Response.WriteAsJsonAsync(Enumerable.Range(0, 3).Select(i => CursorRow(scope, i)));
            }
        });
        using var services = Services(server, "opencode", cursorPagination: true);
        var adapter = services.GetRequiredService<NativeAgentAdapterFactory>().Create("pilot", scope);
        var handle = await adapter.PrepareAsync(new(scope, "bounded"), TestContext.Current.CancellationToken);
        var observation = await adapter.ObserveAsync(handle, TestContext.Current.CancellationToken);
        Assert.Equal(ExternalAgentState.Completed, observation.State); Assert.Equal(103, observation.Usage.Count);
        Assert.Equal(103, observation.Usage.Select(x => x.ReportId).Distinct().Count());
        Assert.All(observation.Usage, r => { Assert.Equal(150, r.InputTokens); Assert.Equal(25, r.OutputTokens); });
        Assert.Equal(observation.Usage, (await adapter.ObserveAsync(handle, TestContext.Current.CancellationToken)).Usage);
        Assert.All(queries, q => Assert.Contains("limit=100", q));
        Assert.Contains(queries, q => q.Contains("before=" + Uri.EscapeDataString(cursor), StringComparison.Ordinal));
        Assert.All(server.Requests, r => Assert.StartsWith("/session", r.Path));
        var requests = server.Requests.ToArray();
        Assert.Equal("/session/status", requests[3].Path); // Status AFTER all pages.
    }

    [Theory]
    [InlineData(false, ExternalAgentState.Unknown, 0)]
    [InlineData(true, ExternalAgentState.Completed, 100)]
    public async Task OpenCode_exactly_full_final_page_needs_reviewed_cursor_opt_in(bool enabled, ExternalAgentState state, int reports)
    {
        var scope = Scope();
        await using var server = await NativeServer.Start(async ctx =>
        {
            if (!await CursorCommon(ctx))
                await ctx.Response.WriteAsJsonAsync(Enumerable.Range(0, 100).Select(i => CursorRow(scope, i)));
        });
        using var services = Services(server, "opencode", cursorPagination: enabled);
        var adapter = services.GetRequiredService<NativeAgentAdapterFactory>().Create("pilot", scope);
        var handle = await adapter.PrepareAsync(new(scope, "bounded"), TestContext.Current.CancellationToken);
        var observed = await adapter.ObserveAsync(handle, TestContext.Current.CancellationToken);
        Assert.Equal(state, observed.State); Assert.Equal(reports, observed.Usage.Count);
    }

    [Theory]
    [InlineData("cycle", "native-agent-cursor-cycle")]
    [InlineData("duplicate", "native-agent-duplicate-message")]
    [InlineData("foreign", "native-agent-response-scope")]
    [InlineData("empty", "native-agent-cursor-invalid")]
    [InlineData("oversized", "native-agent-cursor-invalid")]
    [InlineData("too-many", "native-agent-cursor-invalid")]
    public async Task OpenCode_invalid_traversal_never_returns_partial_usage_or_terminal_success(string fault, string code)
    {
        var scope = Scope(); var pages = 0;
        await using var server = await NativeServer.Start(async ctx =>
        {
            if (await CursorCommon(ctx)) return;
            pages++;
            ctx.Response.Headers["X-Next-Cursor"] = fault == "oversized" ? new string('x', 4097) : "cursor_1";
            if (fault == "empty") { await ctx.Response.WriteAsJsonAsync(Array.Empty<object>()); return; }
            if (fault == "too-many")
            { await ctx.Response.WriteAsJsonAsync(Enumerable.Range(0, 101).Select(i => CursorRow(scope, i))); return; }
            await ctx.Response.WriteAsJsonAsync(new[] { CursorRow(scope, fault == "duplicate" ? 1 : pages,
                fault == "foreign" ? "ses_foreign" : "ses_cursor") });
        });
        using var services = Services(server, "opencode", cursorPagination: true);
        var adapter = services.GetRequiredService<NativeAgentAdapterFactory>().Create("pilot", scope);
        var handle = await adapter.PrepareAsync(new(scope, "bounded"), TestContext.Current.CancellationToken);
        var ex = await Assert.ThrowsAsync<ExternalAgentException>(() => adapter.ObserveAsync(handle, TestContext.Current.CancellationToken));
        Assert.Equal(code, ex.Code); Assert.Null(ex.InnerException);
        Assert.DoesNotContain("ses_foreign", ex.ToString()); Assert.InRange(pages, 1, 2);
    }

    [Theory]
    [InlineData(false, 10)]
    [InlineData(true, 5)]
    public async Task OpenCode_page_or_aggregate_byte_budget_returns_unknown_without_partial_reports(bool large, int expectedPages)
    {
        var scope = Scope(); var pages = 0;
        await using var server = await NativeServer.Start(async ctx =>
        {
            if (await CursorCommon(ctx)) return;
            pages++; ctx.Response.Headers["X-Next-Cursor"] = "cursor_" + pages;
            await ctx.Response.WriteAsJsonAsync(new[] { CursorRow(scope, pages, filler: large ? new string('x', 900 * 1024) : null) });
        });
        using var services = Services(server, "opencode", cursorPagination: true);
        var adapter = services.GetRequiredService<NativeAgentAdapterFactory>().Create("pilot", scope);
        var handle = await adapter.PrepareAsync(new(scope, "bounded"), TestContext.Current.CancellationToken);
        var observed = await adapter.ObserveAsync(handle, TestContext.Current.CancellationToken);
        Assert.Equal(ExternalAgentState.Unknown, observed.State); Assert.Empty(observed.Usage);
        Assert.Equal(expectedPages, pages);
    }

    [Fact]
    public async Task OpenCode_cursor_disabled_fingerprint_stays_compatible_and_opt_in_fences_old_handles()
    {
        await using var server = await NativeServer.Start(async ctx => { await CursorCommon(ctx); });
        var scope = Scope(); using var legacy = Services(server, "opencode");
        using var paged = Services(server, "opencode", cursorPagination: true);
        var original = legacy.GetRequiredService<NativeAgentAdapterFactory>().Create("pilot", scope);
        var updated = paged.GetRequiredService<NativeAgentAdapterFactory>().Create("pilot", scope);
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            Key = "pilot", Engine = ExternalAgentEngine.OpenCode, OrganizationId = scope.OrganizationId,
            WorkspaceId = scope.WorkspaceId, ProjectId = scope.ProjectId, Endpoint = new Uri(server.Address),
            SecretRef = "env:TEST_NATIVE_SECRET", ModelProvider = "model-provider", ModelId = "model-id",
            Username = "opencode", OpenCodeDisjointTokenAccounting = true, OpenCodeOmittedStatusIsIdle = true
        }))));
        Assert.Equal(expected, original.BindingFingerprint); Assert.NotEqual(expected, updated.BindingFingerprint);
        var handle = await original.PrepareAsync(new(scope, "bounded"), TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<ExternalAgentException>(() => updated.ObserveAsync(handle, TestContext.Current.CancellationToken));
        Assert.Equal("native-agent-binding-changed", error.Code); Assert.Single(server.Requests);
    }

    [Fact]
    public async Task OpenCode_unreviewed_short_page_with_cursor_is_not_complete()
    {
        var scope = Scope(); var pages = 0;
        await using var server = await NativeServer.Start(async ctx =>
        {
            if (await CursorCommon(ctx)) return;
            pages++; ctx.Response.Headers["X-Next-Cursor"] = "cursor_1";
            await ctx.Response.WriteAsJsonAsync(new[] { CursorRow(scope, 1) });
        });
        using var services = Services(server, "opencode");
        var adapter = services.GetRequiredService<NativeAgentAdapterFactory>().Create("pilot", scope);
        var handle = await adapter.PrepareAsync(new(scope, "bounded"), TestContext.Current.CancellationToken);
        var observation = await adapter.ObserveAsync(handle, TestContext.Current.CancellationToken);
        Assert.Equal(ExternalAgentState.Unknown, observation.State); Assert.Empty(observation.Usage); Assert.Equal(1, pages);
    }

    [Fact]
    public async Task OpenCode_failure_after_first_page_never_returns_partial_usage_or_retries()
    {
        var scope = Scope(); var pages = 0;
        await using var server = await NativeServer.Start(async ctx =>
        {
            if (await CursorCommon(ctx)) return;
            pages++;
            if (pages == 2) { ctx.Response.StatusCode = 500; await ctx.Response.WriteAsync("private-upstream-detail"); return; }
            ctx.Response.Headers["X-Next-Cursor"] = "cursor_1";
            await ctx.Response.WriteAsJsonAsync(new[] { CursorRow(scope, 1) });
        });
        using var services = Services(server, "opencode", cursorPagination: true);
        var adapter = services.GetRequiredService<NativeAgentAdapterFactory>().Create("pilot", scope);
        var handle = await adapter.PrepareAsync(new(scope, "bounded"), TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<ExternalAgentException>(() => adapter.ObserveAsync(handle, TestContext.Current.CancellationToken));
        Assert.Equal("native-agent-http-500", error.Code); Assert.Null(error.InnerException); Assert.Equal(2, pages);
        Assert.DoesNotContain("private-upstream-detail", error.ToString());
    }

    private static async Task<bool> CursorCommon(HttpContext ctx)
    {
        if (ctx.Request.Path == "/session") { await ctx.Response.WriteAsJsonAsync(new { id = "ses_cursor" }); return true; }
        if (ctx.Request.Path == "/session/status")
        { await ctx.Response.WriteAsync("{\"ses_cursor\":{\"type\":\"idle\"}}"); return true; }
        return false;
    }
    private static object CursorRow(ExternalAgentScope scope, int id, string session = "ses_cursor", string? filler = null) => new
    {
        info = new { id = "msg_page_" + id, parentID = "msg_" + scope.RunId.ToString("N"), sessionID = session,
            role = "assistant", finish = "stop", time = new { completed = 1 }, providerID = "served", modelID = "served-model",
            tokens = new { input = 100, output = 20, reasoning = 5, cache = new { read = 40, write = 10 } } },
        parts = filler is null ? Array.Empty<object>() : new object[] { new { type = "text", text = filler } }
    };
}
