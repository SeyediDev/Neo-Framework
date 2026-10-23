using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
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
public sealed record ReceiveHarnessResult(WorkspaceScope Scope, Guid RunId, string Provider, HarnessResult Body,
    string? UsageBindingFingerprint = null) : IRequest<HarnessReceipt>;
public sealed class HarnessResultHandler(IWorkspaceWorkStore store, IDurableWorkStore durable, TimeProvider clock)
    : IRequestHandler<ReceiveHarnessResult, HarnessReceipt>
{
    public async Task<HarnessReceipt> Handle(ReceiveHarnessResult r, CancellationToken ct)
    {
        var outcome = Named<SimulationOutcome>(r.Body.Outcome);
        ValidateUsage(r.Body.Usage);
        if (r.Body.Evidence?.Count > 100) throw new ArgumentException("Too many evidence entries.");
        var evidence = (r.Body.Evidence ?? []).Select(x => new RunEvidence(Named<EvidenceKind>(x.Kind),
            x.Reference, Named<EvidenceOutcome>(x.Outcome), x.Details, x.CommitSha)).ToArray();
        var itemId = await store.ExecuteAsync(r.Scope, async (session, token) =>
        {
            var run = await RunHandlers.Run(RunHandlers.Session(session), r.RunId, token);
            Match(run, r); return run.WorkItemId;
        }, ct);
        var acceptance = await durable.ApplyInboxAsync(r.Scope,
            new(itemId, "result." + r.Provider, r.RunId.ToString("N"), JsonSerializer.Serialize(r.Body), r.RunId),
            async (session, token) =>
            {
                var s = RunHandlers.Session(session); var run = await RunHandlers.Run(s, r.RunId, token);
                Match(run, r);
                // Same workspace transaction as result/evidence/time/Inbox and
                // follow-up. Failure of any part rolls back all effects.
                foreach (var report in r.Body.Usage ?? [])
                {
                    var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{r.Scope.OrganizationId:N}/{r.Scope.WorkspaceId:N}/{run.Id:N}/{r.Provider}/{report.ReportId}"));
                    var key = "harness." + Convert.ToHexString(hash).ToLowerInvariant();
                    var usage = TokenUsageReport.Create(r.Scope, new Guid(hash.AsSpan(0, 16)), run.Id, run.WorkItemId,
                        run.Provider, report.Model, report.InputTokens, report.OutputTokens,
                        report.CachedInputTokens, report.ReasoningTokens, "reported-harness", key, clock.GetUtcNow());
                    // Native Provider remains provenance in the immutable Inbox
                    // body. The run's authenticated transport owns the row.
                    var existing = await s.FindTokenUsageAsync(key, token);
                    if (existing is not null)
                    {
                        if (existing.Id != usage.Id || existing.AgentRunId != run.Id || existing.WorkItemId != run.WorkItemId ||
                            existing.Provider != usage.Provider || existing.Model != usage.Model || existing.Source != usage.Source ||
                            existing.InputTokens != usage.InputTokens || existing.OutputTokens != usage.OutputTokens ||
                            existing.CachedInputTokens != usage.CachedInputTokens || existing.ReasoningTokens != usage.ReasoningTokens)
                            throw new WorkItemConflictException("Harness usage conflicts with an existing report.");
                    }
                    else s.Add(usage);
                }
                run.RecordResult(await RunHandlers.Item(s, itemId, token), outcome, r.Body.Summary, clock.GetUtcNow(), evidence);
            }, ct, outcome == SimulationOutcome.Succeeded ? WorkDeliveryKind.EvaluateWorkflow : null);
        return new(acceptance.ReceiptId, acceptance.Duplicate);
    }
    private static void ValidateUsage(IReadOnlyList<HarnessUsage>? reports)
    {
        if (reports is null) return;
        if (reports.Count > 100) throw new ArgumentException("Too many usage reports.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        static bool Invalid(string value) => string.IsNullOrWhiteSpace(value) || value.Length > 200 || value.Any(char.IsControl);
        foreach (var report in reports)
        {
            if (report is null || Invalid(report.ReportId) || Invalid(report.Provider) || Invalid(report.Model) ||
                !ids.Add(report.ReportId) || new[] { report.InputTokens, report.OutputTokens, report.CachedInputTokens, report.ReasoningTokens }.Any(x => x < 0) ||
                report.InputTokens.HasValue && report.CachedInputTokens > report.InputTokens ||
                report.OutputTokens.HasValue && report.ReasoningTokens > report.OutputTokens)
                throw new ArgumentException("Invalid harness usage report.");
            if (report.InputTokens.HasValue && report.OutputTokens.HasValue)
                try { _ = checked(report.InputTokens.Value + report.OutputTokens.Value); }
                catch (OverflowException) { throw new ArgumentException("Invalid harness usage total."); }
        }
    }
    private static void Match(AgentRun run, ReceiveHarnessResult request)
    {
        var provider = request.Provider;
        if (provider == "fake" || run.Provider != provider || run.HarnessPayload is null || !run.AllowExternalExecution)
            throw new UnauthorizedAccessException("Result does not belong to this connection.");
        if (request.Body.Usage is not null && (request.UsageBindingFingerprint is null || run.HarnessBinding != request.UsageBindingFingerprint))
            throw new UnauthorizedAccessException("Usage does not belong to the frozen connection binding.");
    }
    private static T Named<T>(string value) where T : struct, Enum
        => Enum.GetNames<T>().Any(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase)) && Enum.TryParse<T>(value, true, out var parsed)
            ? parsed : throw new ArgumentException("Unknown result or evidence name.");
}
