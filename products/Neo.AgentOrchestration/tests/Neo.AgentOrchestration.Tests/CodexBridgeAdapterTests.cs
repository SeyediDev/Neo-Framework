using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Neo.AgentOrchestration.Application.ExternalAgents;
using Neo.AgentOrchestration.Application.ExternalExecution;
using Neo.AgentOrchestration.Infrastructure.ExternalAgents;
using Xunit;

namespace Neo.AgentOrchestration.Tests;

public sealed partial class NativeAgentAdapterTests
{
    private static readonly JsonSerializerOptions BridgeJson = new(JsonSerializerDefaults.Web);
    private static CodexBridgePolicy Policy => new("codex-pilot", "https://example.org/approved/repository.git",
        new('a', 40), "sha256:" + new string('b', 64), "0.162.1", new('f', 64));
    private static GatewayBinding Gateway(ExternalAgentScope scope) => new("pilot", "gateway-fingerprint", "codex-pilot",
        scope, Guid.NewGuid(), new("https://callback.example/result"));
    private static CodexBridgeReceipt Receipt(ExternalAgentHandle h, string state = "Completed") => new(
        "fanasa-codex/v1", h.Scope, h.NativeId!, h.BindingFingerprint, Policy, state,
        state is "Completed" or "Failed" or "Cancelled", state == "Completed" ? 0 : state is "Failed" or "Cancelled" ? 1 : null,
        state == "Completed", new('c', 64), new("turn-1", "served-provider", "served-model", 100, 20, 70, null),
        new(true, 3, 2, new('d', 64), new('e', 64), new('f', 64)));

    [Fact]
    public async Task Codex_uses_durable_preparation_pinned_policy_and_separates_model_from_acceptance()
    {
        ExternalAgentHandle? handle = null; var state = "Running";
        await using var server = await NativeServer.Start(async ctx =>
        {
            if (ctx.Request.Method == "POST")
            {
                Assert.True(ctx.Request.ContentLength > 0);
                Assert.Equal(0, ctx.Request.Headers.TransferEncoding.Count);
                Assert.Equal(0, ctx.Request.Headers.ContentEncoding.Count);
            }
            ctx.Response.StatusCode = ctx.Request.Method == "POST" ? 202 : 200;
            var r = Receipt(handle!, ctx.Request.Path.StartsWithSegments("/v1/codex/ready") ? "Ready" : state);
            await ctx.Response.WriteAsJsonAsync(r);
        });
        using var services = Services(server, "codex"); var scope = Scope();
        var a = Assert.IsType<CodexBridgeAdapter>(services.GetRequiredService<NativeAgentAdapterFactory>().Create("pilot", scope));
        var input = new ExternalAgentInput(scope, "bounded task", "policy");
        handle = await a.PrepareAsync(input, TestContext.Current.CancellationToken);
        Assert.Empty(server.Requests);
        await a.VerifyReadyAsync(Gateway(scope), TestContext.Current.CancellationToken);
        Assert.Equal(handle, await a.SubmitAsync(input, handle, TestContext.Current.CancellationToken));
        var submission = server.Requests.Single(x => x.Path == "/v1/codex/runs");
        Assert.StartsWith("Bearer ", submission.Authorization);
        Assert.Equal(scope.RunId.ToString("N"), submission.IdempotencyKey);
        using var sent = JsonDocument.Parse(submission.Body);
        Assert.Equal("model-id", sent.RootElement.GetProperty("model").GetString());
        Assert.Equal(Policy.ImageDigest, sent.RootElement.GetProperty("policy").GetProperty("imageDigest").GetString());
        var running = await a.ObserveAsync(handle, TestContext.Current.CancellationToken);
        Assert.Equal(ExternalAgentState.Running, running.State); Assert.Empty(running.Usage);
        Assert.False((await a.CollectAsync(Gateway(scope), running.State, TestContext.Current.CancellationToken)).ExecutorExited);
        await a.RequestStopAsync(handle, TestContext.Current.CancellationToken);
        Assert.Equal(ExternalAgentState.Running, (await a.ObserveAsync(handle, TestContext.Current.CancellationToken)).State);
        state = "Completed";
        var observation = await a.ObserveAsync(handle, TestContext.Current.CancellationToken);
        Assert.Equal(ExternalAgentState.Completed, observation.State);
        var usage = Assert.Single(observation.Usage); Assert.Equal(100, usage.InputTokens); Assert.Equal(20, usage.OutputTokens);
        Assert.Equal(70, usage.CachedInputTokens); Assert.Null(usage.ReasoningTokens);
        Assert.Equal(usage, Assert.Single((await a.ObserveAsync(handle, TestContext.Current.CancellationToken)).Usage));
        var evidence = await a.CollectAsync(Gateway(scope), observation.State, TestContext.Current.CancellationToken);
        Assert.True(evidence.ExecutorExited); Assert.Equal("Succeeded", evidence.Result!.Outcome);
        Assert.Equal(3, evidence.Result.Evidence!.Count);
        Assert.All(evidence.Result.Evidence, e => Assert.StartsWith("fanasa-evidence:" + scope.RunId.ToString("N") + ":", e.Reference));
        Assert.DoesNotContain("bounded task", JsonSerializer.Serialize(evidence));
        Assert.DoesNotContain("served-provider", JsonSerializer.Serialize(evidence));
    }

    [Theory]
    [InlineData("scope")] [InlineData("binding")] [InlineData("native")]
    [InlineData("revision")] [InlineData("image")] [InlineData("protocol")]
    [InlineData("container")] [InlineData("exit")] [InlineData("runtime")]
    public async Task Codex_rejects_receipts_for_another_run_or_runtime_without_fabricated_evidence(string changed)
    {
        ExternalAgentHandle? handle = null;
        await using var server = await NativeServer.Start(async ctx =>
        {
            var r = Receipt(handle!);
            r = changed switch
            {
                "scope" => r with { Scope = r.Scope with { RunId = Guid.NewGuid() } },
                "binding" => r with { BindingFingerprint = "wrong" },
                "native" => r with { NativeId = "codex_other" },
                "revision" => r with { Policy = r.Policy with { Revision = new('0', 40) } },
                "image" => r with { Policy = r.Policy with { ImageDigest = "sha256:" + new string('0', 64) } },
                "protocol" => r with { Protocol = "unknown" },
                "container" => r with { ContainerId = "../host" },
                "exit" => r with { ExecutorExited = false },
                _ => r with { Policy = r.Policy with { RuntimeVersion = "1.0.0" } }
            };
            await ctx.Response.WriteAsJsonAsync(r);
        });
        using var services = Services(server, "codex"); var scope = Scope();
        var a = services.GetRequiredService<NativeAgentAdapterFactory>().Create("pilot", scope);
        handle = await a.PrepareAsync(new(scope, "bounded"), TestContext.Current.CancellationToken);
        var e = await Assert.ThrowsAsync<ExternalAgentException>(() => a.ObserveAsync(handle, TestContext.Current.CancellationToken));
        Assert.Equal("codex-receipt-invalid", e.Code); Assert.Null(e.InnerException);
    }

    [Theory]
    [InlineData("no-validation")] [InlineData("no-tests")] [InlineData("no-files")]
    [InlineData("bad-patch")] [InlineData("bad-test-output")] [InlineData("bad-snapshot")]
    public async Task Codex_completed_model_without_independent_evidence_is_not_accepted(string mode)
    {
        ExternalAgentHandle? handle = null;
        await using var server = await NativeServer.Start(async ctx =>
        {
            var r = Receipt(handle!); var v = r.Validation!;
            r = r with { Validation = mode switch
            {
                "no-validation" => null, "no-tests" => v with { TestCount = 0 }, "no-files" => v with { ChangedFileCount = 0 },
                "bad-patch" => v with { ChangeManifestSha256 = "untrusted" }, "bad-test-output" => v with { TestOutputSha256 = "" },
                _ => v with { SnapshotSha256 = "" }
            } };
            await ctx.Response.WriteAsJsonAsync(r);
        });
        using var services = Services(server, "codex"); var scope = Scope();
        var a = Assert.IsType<CodexBridgeAdapter>(services.GetRequiredService<NativeAgentAdapterFactory>().Create("pilot", scope));
        handle = await a.PrepareAsync(new(scope, "bounded"), TestContext.Current.CancellationToken);
        Assert.Equal(ExternalAgentState.Completed, (await a.ObserveAsync(handle, TestContext.Current.CancellationToken)).State);
        var e = await Assert.ThrowsAsync<ExternalAgentException>(() => a.CollectAsync(Gateway(scope), ExternalAgentState.Completed, TestContext.Current.CancellationToken));
        Assert.Equal("codex-evidence-unverified", e.Code);
    }

    [Theory]
    [InlineData("Failed", "Failed")] [InlineData("Cancelled", "NeedsInput")]
    [InlineData("tests-failed", "Failed")]
    public async Task Codex_failures_or_stop_never_become_success(string state, string outcome)
    {
        ExternalAgentHandle? handle = null;
        await using var server = await NativeServer.Start(async ctx =>
        {
            var r = Receipt(handle!, state == "tests-failed" ? "Completed" : state);
            if (state == "tests-failed") r = r with { Validation = r.Validation! with { Passed = false } };
            await ctx.Response.WriteAsJsonAsync(r);
        });
        using var services = Services(server, "codex"); var scope = Scope();
        var a = Assert.IsType<CodexBridgeAdapter>(services.GetRequiredService<NativeAgentAdapterFactory>().Create("pilot", scope));
        handle = await a.PrepareAsync(new(scope, "bounded"), TestContext.Current.CancellationToken);
        Assert.Equal(outcome, (await a.CollectAsync(Gateway(scope), ExternalAgentState.Completed, TestContext.Current.CancellationToken)).Result!.Outcome);
    }

    [Fact]
    public async Task Codex_uncertain_submit_is_not_retried_and_cannot_reveal_upstream_text()
    {
        await using var server = await NativeServer.Start(async ctx => { ctx.Response.StatusCode = 503; await ctx.Response.WriteAsync("private-native-message"); });
        using var services = Services(server, "codex"); var scope = Scope();
        var a = services.GetRequiredService<NativeAgentAdapterFactory>().Create("pilot", scope); var input = new ExternalAgentInput(scope, "bounded");
        var h = await a.PrepareAsync(input, TestContext.Current.CancellationToken);
        var e = await Assert.ThrowsAsync<ExternalAgentException>(() => a.SubmitAsync(input, h, TestContext.Current.CancellationToken));
        Assert.True(e.RequiresReconciliation); Assert.Single(server.Requests);
        Assert.DoesNotContain("private-native", e.ToString()); Assert.Null(e.InnerException);
    }

    [Fact]
    public async Task Codex_opt_in_and_pins_are_required_before_any_network_call()
    {
        await using var server = await NativeServer.Start(_ => Task.CompletedTask);
        using var services = Services(server, "codex"); var config = services.GetRequiredService<IConfiguration>();
        var factory = services.GetRequiredService<NativeAgentAdapterFactory>(); var scope = Scope();
        var old = factory.Create("pilot", scope).BindingFingerprint;
        config["AgentGateway:Bindings:pilot:Revision"] = new('0', 40);
        Assert.NotEqual(old, factory.Create("pilot", scope).BindingFingerprint);
        config["AgentGateway:CodexBridgeEnabled"] = "false";
        Assert.Throws<ExternalAgentException>(() => factory.Create("pilot", scope));
        Assert.Empty(server.Requests);
    }
}
