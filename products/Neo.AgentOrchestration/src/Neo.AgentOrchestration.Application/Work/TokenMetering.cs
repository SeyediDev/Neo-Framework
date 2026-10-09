using Neo.AgentOrchestration.Contracts;
using Neo.AgentOrchestration.Domain.Runs;
using Neo.AgentOrchestration.Domain.Work;

namespace Neo.AgentOrchestration.Application.Work;

public static class TokenMetering
{
    public static IReadOnlyList<WorkTokenUsageView> Reports(WorkItem item, IEnumerable<TokenUsageReport> runs)
        => item.TokenUsage.Select(x => new WorkTokenUsageView(x.Id, null, x.Provider, x.Model, "reported-manual", x.Reference,
            x.InputTokens, x.OutputTokens, x.CachedInputTokens, x.ReasoningTokens, x.RecordedAtUtc))
            .Concat(runs.Where(x => x.WorkItemId == item.Id).Select(x => new WorkTokenUsageView(x.Id, x.AgentRunId,
                x.Provider, x.Model, x.Source, x.IdempotencyKey, x.InputTokens, x.OutputTokens,
                x.CachedInputTokens, x.ReasoningTokens, x.RecordedAtUtc)))
            .OrderBy(x => x.RecordedAtUtc).ThenBy(x => x.Id).ToArray();

    public static TokenMeterView Summarize(IEnumerable<WorkTokenUsageView> reports)
    {
        var rows = reports.Where(x => x.Source is "reported" or "imported" or "reported-manual").ToArray();
        long? Sum(Func<WorkTokenUsageView, long?> select)
        {
            var known = rows.Select(select).Where(x => x.HasValue).ToArray();
            return known.Length == 0 ? null : known.Sum(x => x!.Value);
        }
        var input = Sum(x => x.InputTokens); var output = Sum(x => x.OutputTokens);
        var complete = rows.Count(x => x.InputTokens.HasValue && x.OutputTokens.HasValue);
        long? knownTotal = input.HasValue || output.HasValue ? checked((input ?? 0) + (output ?? 0)) : null;
        return new(rows.Length, complete, input, output, Sum(x => x.CachedInputTokens), Sum(x => x.ReasoningTokens),
            knownTotal, rows.Length > 0 && complete == rows.Length ? knownTotal : null);
    }
}
