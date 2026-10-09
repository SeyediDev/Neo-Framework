namespace Neo.AgentOrchestration.Contracts;

public sealed record SetTokenEstimateRequest(Guid ExpectedVersion, long? Tokens);
public sealed record RecordWorkTokenUsageRequest(Guid ExpectedVersion, Guid RequestId, string Provider, string Reference,
    string? Model = null, long? InputTokens = null, long? OutputTokens = null,
    long? CachedInputTokens = null, long? ReasoningTokens = null);
public sealed record WorkTokenUsageView(Guid Id, Guid? RunId, string Provider, string? Model, string Source,
    string Reference, long? InputTokens, long? OutputTokens, long? CachedInputTokens, long? ReasoningTokens,
    DateTimeOffset RecordedAtUtc);
// Known counters are subtotals, never a substitute for missing counters. Cache/reasoning are included subsets.
public sealed record TokenMeterView(int ReportCount, int CompleteReportCount, long? InputTokens, long? OutputTokens,
    long? CachedInputTokens, long? ReasoningTokens, long? KnownTotalTokens, long? TotalTokens);
