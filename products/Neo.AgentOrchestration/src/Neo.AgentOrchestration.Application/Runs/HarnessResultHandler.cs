using System.Text.Json;
using MediatR;
using Neo.AgentOrchestration.Application.Delivery;
using Neo.AgentOrchestration.Application.Work;
using Neo.AgentOrchestration.Contracts;
using Neo.AgentOrchestration.Domain.Projects;
using Neo.AgentOrchestration.Domain.Runs;
using Neo.AgentOrchestration.Domain.Work;

namespace Neo.AgentOrchestration.Application.Runs;

// Provider/scope are derived by the API's connection-specific authentication,
// never accepted as authority from the result body.
public sealed record ReceiveHarnessResult(WorkspaceScope Scope, Guid RunId, string Provider, HarnessResult Body) : IRequest<HarnessReceipt>;
public sealed class HarnessResultHandler(IWorkspaceWorkStore store, IDurableWorkStore durable, TimeProvider clock)
    : IRequestHandler<ReceiveHarnessResult, HarnessReceipt>
{
    public async Task<HarnessReceipt> Handle(ReceiveHarnessResult r, CancellationToken ct)
    {
        var outcome = Named<SimulationOutcome>(r.Body.Outcome);
        if (r.Body.Evidence?.Count > 100) throw new ArgumentException("Too many evidence entries.");
        var evidence = (r.Body.Evidence ?? []).Select(x => new RunEvidence(Named<EvidenceKind>(x.Kind),
            x.Reference, Named<EvidenceOutcome>(x.Outcome), x.Details, x.CommitSha)).ToArray();
        var itemId = await store.ExecuteAsync(r.Scope, async (session, token) =>
        {
            var run = await RunHandlers.Run(RunHandlers.Session(session), r.RunId, token);
            Match(run, r.Provider); return run.WorkItemId;
        }, ct);
        var acceptance = await durable.ApplyInboxAsync(r.Scope,
            new(itemId, "result." + r.Provider, r.RunId.ToString("N"), JsonSerializer.Serialize(r.Body), r.RunId),
            async (session, token) =>
            {
                var s = RunHandlers.Session(session); var run = await RunHandlers.Run(s, r.RunId, token);
                Match(run, r.Provider);
                run.RecordResult(await RunHandlers.Item(s, itemId, token), outcome, r.Body.Summary, clock.GetUtcNow(), evidence);
            }, ct, outcome == SimulationOutcome.Succeeded ? WorkDeliveryKind.EvaluateWorkflow : null);
        return new(acceptance.ReceiptId, acceptance.Duplicate);
    }
    private static void Match(AgentRun run, string provider)
    {
        if (provider == "fake" || run.Provider != provider || run.HarnessPayload is null || !run.AllowExternalExecution)
            throw new UnauthorizedAccessException("Result does not belong to this connection.");
    }
    private static T Named<T>(string value) where T : struct, Enum
        => Enum.GetNames<T>().Any(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase)) && Enum.TryParse<T>(value, true, out var parsed)
            ? parsed : throw new ArgumentException("Unknown result or evidence name.");
}
