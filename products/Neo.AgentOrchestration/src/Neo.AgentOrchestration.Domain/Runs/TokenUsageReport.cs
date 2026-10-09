using Neo.Domain.Entities.Base;
using Neo.AgentOrchestration.Domain.Projects;

namespace Neo.AgentOrchestration.Domain.Runs;

// A provider report is intentionally separate from execution progress. A
// missing value means the provider did not expose that counter; it must not be
// rendered or aggregated as zero.
public sealed class TokenUsageReport : BaseEntity<Guid>
{
    public Guid OrganizationId { get; private set; }
    public Guid WorkspaceId { get; private set; }
    public Guid WorkItemId { get; private set; }
    public Guid AgentRunId { get; private set; }
    public string Provider { get; private set; } = "";
    public string? Model { get; private set; }
    public long? InputTokens { get; private set; }
    public long? OutputTokens { get; private set; }
    public long? CachedInputTokens { get; private set; }
    public long? ReasoningTokens { get; private set; }
    public string Source { get; private set; } = "reported";
    public string IdempotencyKey { get; private set; } = "";
    public DateTimeOffset RecordedAtUtc { get; private set; }

    public TokenUsageReport() { }

    public static TokenUsageReport Create(WorkspaceScope scope, Guid id, Guid runId, Guid workItemId,
        string provider, string? model, long? inputTokens, long? outputTokens, long? cachedInputTokens,
        long? reasoningTokens, string source, string idempotencyKey, DateTimeOffset recordedAtUtc)
    {
        scope.Require(scope.OrganizationId, scope.WorkspaceId);
        if (id == Guid.Empty || runId == Guid.Empty || workItemId == Guid.Empty)
            throw new ArgumentException("Usage identifiers are required.");
        ValidateCount(inputTokens); ValidateCount(outputTokens); ValidateCount(cachedInputTokens); ValidateCount(reasoningTokens);
        if (cachedInputTokens.HasValue && inputTokens.HasValue && cachedInputTokens > inputTokens ||
            reasoningTokens.HasValue && outputTokens.HasValue && reasoningTokens > outputTokens)
            throw new ArgumentException("Cached input and reasoning must not exceed their parent counters.");
        return new TokenUsageReport { Id = id, OrganizationId = scope.OrganizationId, WorkspaceId = scope.WorkspaceId,
            AgentRunId = runId, WorkItemId = workItemId, Provider = Required(provider, 80), Model = Optional(model, 200),
            InputTokens = inputTokens, OutputTokens = outputTokens, CachedInputTokens = cachedInputTokens,
            ReasoningTokens = reasoningTokens, Source = Required(source, 40), IdempotencyKey = Required(idempotencyKey, 120),
            RecordedAtUtc = recordedAtUtc };
    }

    private static void ValidateCount(long? value) { if (value is < 0) throw new ArgumentOutOfRangeException(nameof(value)); }
    private static string Required(string value, int max) => string.IsNullOrWhiteSpace(value) || value.Trim().Length > max
        ? throw new ArgumentException("A required usage value is invalid.") : value.Trim();
    private static string? Optional(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null :
        value.Trim().Length > max ? throw new ArgumentException("An optional usage value is too long.") : value.Trim();
}
