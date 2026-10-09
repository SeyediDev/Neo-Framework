using Neo.AgentOrchestration.Domain.Projects;
using Neo.Domain.Entities.Base;

namespace Neo.AgentOrchestration.Domain.Work;

public sealed class WorkTokenUsage : BaseEntity<Guid>
{
    public Guid WorkItemId { get; private set; }
    public string AgentId { get; private set; } = "";
    public string ChatId { get; private set; } = "";
    public string Provider { get; private set; } = "";
    public string? Model { get; private set; }
    public string Reference { get; private set; } = "";
    public long? InputTokens { get; private set; }
    public long? OutputTokens { get; private set; }
    public long? CachedInputTokens { get; private set; }
    public long? ReasoningTokens { get; private set; }
    public DateTimeOffset RecordedAtUtc { get; private set; }
    public WorkTokenUsage() { }

    internal static WorkTokenUsage Create(Guid itemId, Guid requestId, WorkActor actor, string provider,
        string? model, string reference, long? input, long? output, long? cached, long? reasoning, DateTimeOffset now)
    {
        if (requestId == Guid.Empty) throw new ArgumentException("A stable usage request ID is required.");
        TokenCounters.Validate(input, output, cached, reasoning);
        return new() { Id = requestId, WorkItemId = itemId, AgentId = actor.AgentId, ChatId = actor.ChatId,
            Provider = WorkRules.Required(provider, 80), Model = WorkRules.Optional(model, 200),
            Reference = WorkRules.Required(reference, 500), InputTokens = input, OutputTokens = output,
            CachedInputTokens = cached, ReasoningTokens = reasoning, RecordedAtUtc = now.ToUniversalTime() };
    }

    internal bool SamePayload(WorkTokenUsage other) => Id == other.Id && AgentId == other.AgentId && ChatId == other.ChatId &&
        Provider == other.Provider && Model == other.Model && Reference == other.Reference && InputTokens == other.InputTokens &&
        OutputTokens == other.OutputTokens && CachedInputTokens == other.CachedInputTokens && ReasoningTokens == other.ReasoningTokens;
}

public static class TokenCounters
{
    public static void Validate(long? input, long? output, long? cached, long? reasoning)
    {
        if (input is < 0 || output is < 0 || cached is < 0 || reasoning is < 0)
            throw new ArgumentOutOfRangeException(nameof(input), "Token counters cannot be negative.");
        if (cached.HasValue && input.HasValue && cached > input || reasoning.HasValue && output.HasValue && reasoning > output)
            throw new ArgumentException("Cached input and reasoning are subsets of input and output respectively.");
        if (input is null && output is null && cached is null && reasoning is null)
            throw new ArgumentException("At least one known counter is required.");
    }
}

public sealed partial class WorkItem
{
    private readonly List<WorkTokenUsage> _tokenUsage = [];
    public IReadOnlyList<WorkTokenUsage> TokenUsage => _tokenUsage.AsReadOnly();
    public long? EstimatedTokens { get; private set; }

    public void SetTokenEstimate(WorkspaceScope scope, WorkActor actor, long? tokens, DateTimeOffset now)
    {
        RequireEditable(scope, actor, now);
        if (OwnerAgentId is not null) RequireOwner(actor);
        if (tokens is <= 0) throw new ArgumentOutOfRangeException(nameof(tokens));
        if (EstimatedTokens == tokens) return;
        EstimatedTokens = tokens;
        Record(actor, "TokenEstimateChanged", tokens?.ToString() ?? "(cleared)", now);
    }

    // Identical delivery retries do not mutate the item or bypass source identity.
    // New writes still require the current item version in the application handler.
    public bool IsTokenUsageRetry(WorkspaceScope scope, WorkActor actor, Guid requestId, string provider, string? model,
        string reference, long? input, long? output, long? cached, long? reasoning, DateTimeOffset now)
    {
        RequireScope(scope);
        var candidate = WorkTokenUsage.Create(Id, requestId, actor, provider, model, reference, input, output, cached, reasoning, now);
        var existing = _tokenUsage.SingleOrDefault(x => x.Id == requestId);
        if (existing is null) return false;
        if (!existing.SamePayload(candidate)) throw new InvalidOperationException("Usage request ID was already used with different content or identity.");
        return true;
    }

    public void RecordTokenUsage(WorkspaceScope scope, WorkActor actor, Guid requestId, string provider, string? model,
        string reference, long? input, long? output, long? cached, long? reasoning, DateTimeOffset now)
    {
        RequireEditable(scope, actor, now);
        RequireOwner(actor);
        if (IsTokenUsageRetry(scope, actor, requestId, provider, model, reference, input, output, cached, reasoning, now)) return;
        _tokenUsage.Add(WorkTokenUsage.Create(Id, requestId, actor, provider, model, reference, input, output, cached, reasoning, now));
        Record(actor, "TokenUsageRecorded", $"Manual provider report {requestId:D}; not billing verification.", now);
    }
}
