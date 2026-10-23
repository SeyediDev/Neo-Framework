namespace Neo.AgentOrchestration.Contracts;

// This is Neo's provider-neutral gateway protocol, not a Codex/Agents API contract.
public sealed record HarnessRequest(string Protocol, Guid RunId, Guid OrganizationId, Guid WorkspaceId,
    Guid RoleId, Guid AgentProfileId, string? Model, string? Instructions, string? SkillPath,
    string? Branch, string CallbackUrl, WorkItemDetails Work);
// Versioned opt-in contract. Unlike neo-harness/v1 it carries only the bounded
// context projection; v1 remains the default for backward compatibility.
public sealed record HarnessCompactRequest(string Protocol, Guid RunId, Guid OrganizationId, Guid WorkspaceId,
    Guid RoleId, Guid AgentProfileId, string? Model, string? Instructions, string? SkillPath,
    string? Branch, string CallbackUrl, WorkContextView Context);
public sealed record HarnessEvidence(string Kind, string Reference, string Outcome, string? Details = null, string? CommitSha = null);
public sealed record HarnessUsage(string ReportId, string Provider, string Model,
    long? InputTokens, long? OutputTokens, long? CachedInputTokens, long? ReasoningTokens);
public sealed record HarnessResult(string Outcome, string Summary, IReadOnlyList<HarnessEvidence>? Evidence = null,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyList<HarnessUsage>? Usage = null);
public sealed record HarnessReceipt(Guid ReceiptId, bool Duplicate);
