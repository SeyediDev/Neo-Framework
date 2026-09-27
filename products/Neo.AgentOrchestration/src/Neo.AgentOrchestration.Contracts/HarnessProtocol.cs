namespace Neo.AgentOrchestration.Contracts;

// This is Neo's provider-neutral gateway protocol, not a Codex/Agents API contract.
public sealed record HarnessRequest(string Protocol, Guid RunId, Guid OrganizationId, Guid WorkspaceId,
    Guid RoleId, Guid AgentProfileId, string? Model, string? Instructions, string? SkillPath,
    string? Branch, string CallbackUrl, WorkItemDetails Work);
public sealed record HarnessEvidence(string Kind, string Reference, string Outcome, string? Details = null, string? CommitSha = null);
public sealed record HarnessResult(string Outcome, string Summary, IReadOnlyList<HarnessEvidence>? Evidence = null);
public sealed record HarnessReceipt(Guid ReceiptId, bool Duplicate);
