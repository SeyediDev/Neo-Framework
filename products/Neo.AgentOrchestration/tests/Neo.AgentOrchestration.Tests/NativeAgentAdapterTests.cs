using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Neo.AgentOrchestration.Application.ExternalAgents;
using Neo.AgentOrchestration.Infrastructure.ExternalAgents;
using Neo.AgentOrchestration.Infrastructure.Runs;
using Xunit;

namespace Neo.AgentOrchestration.Tests;

public sealed partial class NativeAgentAdapterTests
{
    [Fact]
    public async Task Hermes_uses_native_run_id_stable_payload_key_and_per_run_memory_without_double_count()
    {
        var state = "stopping";
        await using var server = await NativeServer.Start(async ctx =>
        {
            if (ctx.Request.Method == "POST" && ctx.Request.Path == "/v1/runs")
            { ctx.Response.StatusCode = 202; await ctx.Response.WriteAsJsonAsync(new { run_id = "run_123" }); return; }
            if (ctx.Request.Path == "/v1/runs/run_123/stop")
            { await ctx.Response.WriteAsJsonAsync(new { status = "stopping" }); return; }
            await ctx.Response.WriteAsJsonAsync(new
            {
                run_id = "run_123", status = state, runtime = new { provider = "served-provider", model = "served-model" },
                usage = new { input_tokens = 100, output_tokens = 20, cache_read_tokens = 40 }
            });
        });
        using var services = Services(server, "hermes"); var scope = Scope();
        var adapter = services.GetRequiredService<NativeAgentAdapterFactory>().Create("pilot", scope);
        var input = new ExternalAgentInput(scope, "authorized bounded task", "approved instruction");
        var prepared = await adapter.PrepareAsync(input, TestContext.Current.CancellationToken); Assert.Empty(server.Requests);
        var handle = await adapter.SubmitAsync(input, prepared, TestContext.Current.CancellationToken);
        var duplicate = await adapter.SubmitAsync(input, prepared, TestContext.Current.CancellationToken);
        Assert.Equal(handle, duplicate);
        var submits = server.Requests.ToArray(); Assert.Equal(submits[0].Body, submits[1].Body);
        Assert.Equal(scope.RunId.ToString("N"), submits[0].IdempotencyKey);
        Assert.Equal($"fanasa:{scope.OrganizationId:N}:{scope.WorkspaceId:N}:{scope.ProjectId:N}:{scope.RunId:N}", submits[0].MemoryScope);
        Assert.StartsWith("Bearer ", submits[0].Authorization);
        Assert.Equal(ExternalAgentState.StopRequested, (await adapter.ObserveAsync(handle, TestContext.Current.CancellationToken)).State);
        await adapter.RequestStopAsync(handle, TestContext.Current.CancellationToken);
        Assert.Equal(ExternalAgentState.StopRequested, (await adapter.ObserveAsync(handle, TestContext.Current.CancellationToken)).State);
        state = "completed";
        var observed = await adapter.ObserveAsync(handle, TestContext.Current.CancellationToken); var usage = Assert.Single(observed.Usage);
        Assert.Equal(ExternalAgentState.Completed, observed.State); Assert.Equal(100, usage.InputTokens);
        Assert.Equal(20, usage.OutputTokens); Assert.Equal(40, usage.CachedInputTokens); Assert.Null(usage.ReasoningTokens);
        Assert.Equal("served-provider", usage.Provider); Assert.Equal("served-model", usage.Model);
        Assert.Equal(usage, Assert.Single((await adapter.ObserveAsync(handle, TestContext.Current.CancellationToken)).Usage));
    }

    [Fact]
    public async Task OpenCode_prepares_before_inference_correlates_turn_and_normalizes_disjoint_usage_once()
    {
        var busy = true; var finish = false; var omitIdle = false; string? prompt = null;
        await using var server = await NativeServer.Start(async ctx =>
        {
            if (ctx.Request.Path == "/session") { ctx.Response.StatusCode = 201; await ctx.Response.WriteAsJsonAsync(new { id = "ses_123" }); return; }
            if (ctx.Request.Path == "/session/ses_123/prompt_async")
            { using var body = await JsonDocument.ParseAsync(ctx.Request.Body); prompt = body.RootElement.GetProperty("messageID").GetString(); ctx.Response.StatusCode = 204; return; }
            if (ctx.Request.Path == "/session/ses_123/abort") { await ctx.Response.WriteAsJsonAsync(true); return; }
            if (ctx.Request.Path == "/session/status")
            { await ctx.Response.WriteAsJsonAsync(omitIdle ? new Dictionary<string, object>() :
                new Dictionary<string, object> { ["ses_123"] = new { type = busy ? "busy" : "idle" } }); return; }
            await ctx.Response.WriteAsJsonAsync(finish ? new[] { new { info = new
            {
                id = "msg_answer", parentID = prompt, sessionID = "ses_123", role = "assistant", finish = "stop",
                time = new { completed = 1234 }, providerID = "served", modelID = "served-model",
                tokens = new { input = 100, output = 20, reasoning = 5, cache = new { read = 40, write = 10 } }
            } } } : []);
        });
        using var services = Services(server, "opencode"); var scope = Scope();
        var adapter = services.GetRequiredService<NativeAgentAdapterFactory>().Create("pilot", scope);
        var input = new ExternalAgentInput(scope, "bounded coding change", "project policy");
        var prepared = await adapter.PrepareAsync(input, TestContext.Current.CancellationToken);
        Assert.Equal("ses_123", prepared.NativeId); Assert.Single(server.Requests);
        var handle = await adapter.SubmitAsync(input, prepared, TestContext.Current.CancellationToken);
        Assert.Equal(handle.PromptId, prompt);
        using var sent = JsonDocument.Parse(server.Requests.ToArray()[1].Body);
        Assert.Equal("model-provider", sent.RootElement.GetProperty("model").GetProperty("providerID").GetString());
        Assert.StartsWith("Basic ", server.Requests.ToArray()[1].Authorization);
        await adapter.RequestStopAsync(handle, TestContext.Current.CancellationToken);
        Assert.Equal(ExternalAgentState.Running, (await adapter.ObserveAsync(handle, TestContext.Current.CancellationToken)).State);
        busy = false;
        Assert.Equal(ExternalAgentState.Unknown, (await adapter.ObserveAsync(handle, TestContext.Current.CancellationToken)).State);
        finish = true; omitIdle = true;
        var observation = await adapter.ObserveAsync(handle, TestContext.Current.CancellationToken); Assert.Equal(ExternalAgentState.Completed, observation.State);
        var usage = Assert.Single(observation.Usage); Assert.Equal(150, usage.InputTokens); Assert.Equal(25, usage.OutputTokens);
        Assert.Equal(40, usage.CachedInputTokens); Assert.Equal(5, usage.ReasoningTokens);
        Assert.Equal("opencode:msg_answer", usage.ReportId);
        Assert.Equal(usage, Assert.Single((await adapter.ObserveAsync(handle, TestContext.Current.CancellationToken)).Usage));
        using var unreviewed = Services(server, "opencode", disjoint: false);
        Assert.Empty((await unreviewed.GetRequiredService<NativeAgentAdapterFactory>().Create("pilot", scope)
            .ObserveAsync(handle with { BindingFingerprint = (await unreviewed.GetRequiredService<NativeAgentAdapterFactory>()
                .Create("pilot", scope).PrepareAsync(input, TestContext.Current.CancellationToken)).BindingFingerprint }, TestContext.Current.CancellationToken)).Usage);
    }

    [Theory]
    [InlineData(302)] [InlineData(409)] [InlineData(429)] [InlineData(500)]
    public async Task Uncertain_writes_are_not_replayed_and_raw_upstream_errors_are_not_exposed(int status)
    {
        await using var server = await NativeServer.Start(async ctx => { ctx.Response.StatusCode = status;
            ctx.Response.Headers.Location = "/forbidden"; await ctx.Response.WriteAsync("private-provider-error-and-secret"); });
        using var services = Services(server, "hermes"); var scope = Scope();
        var adapter = services.GetRequiredService<NativeAgentAdapterFactory>().Create("pilot", scope);
        var input = new ExternalAgentInput(scope, "bounded"); var prepared = await adapter.PrepareAsync(input, TestContext.Current.CancellationToken);
        var error = await Assert.ThrowsAsync<ExternalAgentException>(() => adapter.SubmitAsync(input, prepared, TestContext.Current.CancellationToken));
        Assert.True(error.RequiresReconciliation); Assert.Equal("native-agent-http-" + status, error.Code);
        Assert.Null(error.InnerException); Assert.DoesNotContain("private-provider", error.ToString()); Assert.Single(server.Requests);
    }

    [Theory]
    [InlineData("Production", "http://127.0.0.1:4096/")]
    [InlineData("Testing", "http://remote.example/")]
    [InlineData("Testing", "https://user:password@host.example/")]
    [InlineData("Testing", "https://host.example/?token=x")]
    public async Task Unsafe_destination_configuration_is_rejected_without_network(string environment, string endpoint)
    {
        await using var server = await NativeServer.Start(_ => Task.CompletedTask);
        using var services = Services(server, "hermes", environment: environment, endpoint: endpoint);
        Assert.Throws<ExternalAgentException>(() => services.GetRequiredService<NativeAgentAdapterFactory>().Create("pilot", Scope()));
        Assert.Empty(server.Requests);
    }

    [Fact]
    public async Task Disabled_cross_project_changed_binding_and_excess_input_do_not_dispatch()
    {
        await using var server = await NativeServer.Start(_ => Task.CompletedTask);
        using var disabled = Services(server, "hermes", enabled: false);
        Assert.Throws<ExternalAgentException>(() => disabled.GetRequiredService<NativeAgentAdapterFactory>().Create("pilot", Scope()));
        using var services = Services(server, "hermes"); var factory = services.GetRequiredService<NativeAgentAdapterFactory>();
        Assert.Throws<ExternalAgentException>(() => factory.Create("pilot", Scope() with { ProjectId = Guid.NewGuid() }));
        var scope = Scope(); var adapter = factory.Create("pilot", scope); var input = new ExternalAgentInput(scope, "bounded");
        var handle = await adapter.PrepareAsync(input, TestContext.Current.CancellationToken);
        await Assert.ThrowsAsync<ExternalAgentException>(() => adapter.SubmitAsync(input, handle with { BindingFingerprint = "changed" }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ExternalAgentException>(() => adapter.PrepareAsync(input with { Prompt = new string('x', 65537) }, TestContext.Current.CancellationToken));
        await Assert.ThrowsAsync<ExternalAgentException>(() => adapter.ObserveAsync(handle with { NativeId = "../cross-tenant" }, TestContext.Current.CancellationToken));
        Assert.Empty(server.Requests);
    }

    [Fact]
    public void Permission_signals_are_scoped_and_never_grant_approval()
    {
        var handle = new ExternalAgentHandle(Scope(), ExternalAgentEngine.OpenCode, "f", "msg_1", "ses_123");
        var signal = NativeAgentPermissionSignals.Read(handle, "message", """{"type":"permission.updated","properties":{"sessionID":"ses_123","id":"perm_1","title":"untrusted"}}""");
        Assert.Equal(ExternalAgentState.AwaitingApproval, signal!.State); Assert.Equal("perm_1", signal.NativeApprovalId);
        Assert.Null(NativeAgentPermissionSignals.Read(handle, "message", """{"type":"permission.asked","properties":{"sessionID":"other","id":"perm_1"}}"""));
        var hermes = handle with { Engine = ExternalAgentEngine.Hermes, NativeId = "run_123" };
        Assert.Equal(ExternalAgentState.AwaitingApproval, NativeAgentPermissionSignals.Read(hermes, "approval.request", """{"run_id":"run_123"}""")!.State);
        Assert.Null(NativeAgentPermissionSignals.Read(hermes, "approval.request", """{"run_id":"other"}"""));
        Assert.Throws<ExternalAgentException>(() => NativeAgentPermissionSignals.Read(handle, "message", "not json"));
    }

    [Theory]
    [InlineData("queued", "{}", "{\"provider\":\"served\",\"model\":\"m\"}", "Queued", false)]
    [InlineData("future_status", "{}", "{}", "Unknown", false)]
    [InlineData("waiting_for_approval", "{}", "{}", "AwaitingApproval", false)]
    [InlineData("completed", "{\"input_tokens\":100}", "{}", "Completed", false)]
    [InlineData("completed", "{\"input_tokens\":100}", "{\"provider\":\"served\",\"model\":\"m\"}", "Completed", true)]
    [InlineData("failed", "{\"input_tokens\":100}", "{\"provider\":\"served\",\"model\":\"m\"}", "Failed", true)]
    [InlineData("cancelled", "{\"input_tokens\":100}", "{\"provider\":\"served\",\"model\":\"m\"}", "Cancelled", true)]
    public async Task Hermes_unknown_state_runtime_and_partial_counts_do_not_become_zero_or_billing(
        string status, string usageJson, string runtimeJson, string expectedState, bool hasUsage)
    {
        await using var server = await NativeServer.Start(async ctx =>
            await ctx.Response.WriteAsync($"{{\"run_id\":\"run_1\",\"status\":\"{status}\",\"usage\":{usageJson},\"runtime\":{runtimeJson}}}"));
        using var services = Services(server, "hermes"); var scope = Scope();
        var adapter = services.GetRequiredService<NativeAgentAdapterFactory>().Create("pilot", scope);
        var handle = (await adapter.PrepareAsync(new(scope, "bounded"), TestContext.Current.CancellationToken)) with { NativeId = "run_1" };
        var observation = await adapter.ObserveAsync(handle, TestContext.Current.CancellationToken);
        Assert.Equal(Enum.Parse<ExternalAgentState>(expectedState), observation.State);
        Assert.Equal(hasUsage ? 1 : 0, observation.Usage.Count);
        if (hasUsage) { Assert.Equal(100, observation.Usage[0].InputTokens); Assert.Null(observation.Usage[0].OutputTokens); }
    }

    [Theory]
    [InlineData("{\"input_tokens\":-1}")]
    [InlineData("{\"input_tokens\":\"private invalid value\"}")]
    [InlineData("{\"input_tokens\":10,\"cache_read_tokens\":11}")]
    [InlineData("{\"input_tokens\":9223372036854775807,\"output_tokens\":1}")]
    public async Task Invalid_usage_is_rejected_without_raw_data(string usage)
    {
        await using var server = await NativeServer.Start(async ctx => await ctx.Response.WriteAsync(
            "{\"run_id\":\"run_1\",\"status\":\"completed\",\"runtime\":{\"provider\":\"served\",\"model\":\"m\"},\"usage\":" + usage + "}"));
        using var services = Services(server, "hermes"); var scope = Scope();
        var adapter = services.GetRequiredService<NativeAgentAdapterFactory>().Create("pilot", scope);
        var handle = (await adapter.PrepareAsync(new(scope, "bounded"), TestContext.Current.CancellationToken)) with { NativeId = "run_1" };
        var error = await Assert.ThrowsAsync<ExternalAgentException>(() => adapter.ObserveAsync(handle, TestContext.Current.CancellationToken));
        Assert.Equal("native-agent-usage-invalid", error.Code); Assert.Null(error.InnerException);
        Assert.DoesNotContain("private invalid", error.ToString());
    }

    [Fact]
    public async Task OpenCode_saturated_transcript_does_not_claim_complete_or_report_partial_usage()
    {
        await using var server = await NativeServer.Start(async ctx =>
        {
            if (ctx.Request.Path == "/session") { await ctx.Response.WriteAsJsonAsync(new { id = "ses_1" }); return; }
            if (ctx.Request.Path == "/session/status") { await ctx.Response.WriteAsync("{\"ses_1\":{\"type\":\"idle\"}}"); return; }
            await ctx.Response.WriteAsJsonAsync(Enumerable.Range(0, 100).Select(i => new { info = new { id = "msg_" + i } }));
        });
        using var services = Services(server, "opencode"); var scope = Scope();
        var adapter = services.GetRequiredService<NativeAgentAdapterFactory>().Create("pilot", scope);
        var handle = await adapter.PrepareAsync(new(scope, "bounded"), TestContext.Current.CancellationToken);
        var observation = await adapter.ObserveAsync(handle, TestContext.Current.CancellationToken);
        Assert.Equal(ExternalAgentState.Unknown, observation.State); Assert.Empty(observation.Usage);
    }

    private static readonly Guid Org = Guid.NewGuid(), Workspace = Guid.NewGuid(), Project = Guid.NewGuid();
    private static ExternalAgentScope Scope() => new(Org, Workspace, Project, Guid.NewGuid());
    private static ServiceProvider Services(NativeServer server, string engine, bool enabled = true, bool disjoint = true,
        string environment = "Testing", string? endpoint = null, bool cursorPagination = false, bool permissionSnapshot = false)
    {
        var values = new Dictionary<string, string?> { ["NativeAgents:Enabled"] = enabled.ToString(), ["NativeAgents:AllowLoopbackHttp"] = "true" };
        const string root = "NativeAgents:Connections:pilot:";
        foreach (var (key, value) in new Dictionary<string, string?>
        {
            ["Enabled"] = "true", ["Engine"] = engine, ["OrganizationId"] = Org.ToString(), ["WorkspaceId"] = Workspace.ToString(),
            ["ProjectId"] = Project.ToString(), ["Endpoint"] = endpoint ?? server.Address, ["SecretRef"] = "env:TEST_NATIVE_SECRET",
            ["ModelProvider"] = "model-provider", ["ModelId"] = "model-id", ["OpenCodeDisjointTokenAccounting"] = disjoint.ToString(),
            ["OpenCodeOmittedStatusIsIdle"] = "true", ["OpenCodeCursorPagination"] = cursorPagination.ToString(),
            ["OpenCodePermissionSnapshot"] = permissionSnapshot.ToString()
        }) values[root + key] = value;
        var services = new ServiceCollection(); services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        services.AddSingleton<IHostEnvironment>(new TestEnvironment { EnvironmentName = environment });
        services.AddSingleton<IHarnessSecrets>(new TestSecrets()); services.AddNativeAgentAdapters(); return services.BuildServiceProvider();
    }
    private sealed class TestSecrets : IHarnessSecrets { public string Resolve(string reference) => new('T', 40); }
    private sealed class TestEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "NativeAdapterTests";
        public string ContentRootPath { get; set; } = Directory.GetCurrentDirectory();
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
    private sealed record Request(string Path, string Body, string Authorization, string IdempotencyKey, string MemoryScope);
    private sealed class NativeServer(WebApplication app, string address) : IAsyncDisposable
    {
        public string Address { get; } = address;
        public ConcurrentQueue<Request> Requests { get; } = new();
        public static async Task<NativeServer> Start(Func<HttpContext, Task> handler)
        {
            var builder = WebApplication.CreateBuilder(); builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders(); var app = builder.Build(); NativeServer? server = null;
            app.Run(async ctx =>
            {
                ctx.Request.EnableBuffering(); using var reader = new StreamReader(ctx.Request.Body, leaveOpen: true);
                var body = await reader.ReadToEndAsync(); ctx.Request.Body.Position = 0;
                server!.Requests.Enqueue(new(ctx.Request.Path, body, ctx.Request.Headers.Authorization.ToString(),
                    ctx.Request.Headers["Idempotency-Key"].ToString(), ctx.Request.Headers["X-Hermes-Session-Key"].ToString()));
                await handler(ctx);
            });
            await app.StartAsync(); var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            server = new NativeServer(app, address + "/"); return server;
        }
        public async ValueTask DisposeAsync() { await app.StopAsync(); await app.DisposeAsync(); }
    }
}
