using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Neo.AgentOrchestration.Application.ExternalAgents;
using Neo.AgentOrchestration.Infrastructure.ExternalAgents;
using Xunit;

namespace Neo.AgentOrchestration.Tests;

public sealed partial class NativeAgentAdapterTests
{
    [Theory]
    [InlineData("hermes-read", false)]
    [InlineData("hermes-submit", true)]
    [InlineData("opencode-prepare", true)]
    [InlineData("opencode-permission", false)]
    [InlineData("opencode-status", false)]
    public async Task Native_request_deadline_includes_stalled_body_after_headers(string operation, bool uncertainWrite)
    {
        var headersSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await NativeServer.Start(async ctx =>
        {
            if (operation is "opencode-permission" or "opencode-status")
            {
                if (ctx.Request.Path == "/session")
                { await ctx.Response.WriteAsJsonAsync(new { id = "ses_stalled" }); return; }
                if (ctx.Request.Path == "/session/ses_stalled/message")
                { await ctx.Response.WriteAsJsonAsync(Array.Empty<object>()); return; }
            }
            ctx.Response.StatusCode = operation == "hermes-submit" ? 202 : 200;
            ctx.Response.ContentType = "application/json";
            await ctx.Response.WriteAsync("{\"private-upstream-detail\":");
            await ctx.Response.Body.FlushAsync();
            headersSent.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, ctx.RequestAborted); }
            catch (OperationCanceledException) when (ctx.RequestAborted.IsCancellationRequested) { }
        });
        var engine = operation.StartsWith("hermes", StringComparison.Ordinal) ? "hermes" : "opencode";
        using var setupServices = Services(server, engine, permissionSnapshot: operation == "opencode-permission");
        using var services = Services(server, engine,
            permissionSnapshot: operation == "opencode-permission", httpTimeout: TimeSpan.FromSeconds(1));
        var scope = Scope(); var adapter = services.GetRequiredService<NativeAgentAdapterFactory>().Create("pilot", scope);
        var input = new ExternalAgentInput(scope, "bounded");
        using var safety = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        safety.CancelAfter(TimeSpan.FromSeconds(10));
        Task request;
        if (operation == "opencode-prepare") request = adapter.PrepareAsync(input, safety.Token);
        else
        {
            // Setup isn't the stalled operation. Don't let first-use HTTP/JSON
            // warmup consume the deliberately short body-test client budget.
            var handle = await setupServices.GetRequiredService<NativeAgentAdapterFactory>().Create("pilot", scope)
                .PrepareAsync(input, TestContext.Current.CancellationToken);
            request = operation == "hermes-submit" ? adapter.SubmitAsync(input, handle, safety.Token)
                : adapter.ObserveAsync(operation == "hermes-read" ? handle with { NativeId = "run_stalled" } : handle, safety.Token);
        }
        var error = await Assert.ThrowsAsync<ExternalAgentException>(() => request);
        Assert.True(headersSent.Task.IsCompletedSuccessfully, "The test must reach a stalled response body, not a header timeout.");
        Assert.False(safety.IsCancellationRequested, "The adapter must enforce its deadline before the caller's safety cancellation.");
        Assert.Equal("native-agent-transport-or-protocol", error.Code);
        Assert.Equal(uncertainWrite, error.RequiresReconciliation);
        Assert.Null(error.InnerException); Assert.DoesNotContain("private-upstream-detail", error.ToString());
        Assert.Equal(operation is "opencode-permission" ? 2 : operation is "opencode-status" ? 3 : 1, server.Requests.Count);
    }
}
