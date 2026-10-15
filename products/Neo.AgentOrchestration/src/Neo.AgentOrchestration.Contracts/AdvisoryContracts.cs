namespace Neo.AgentOrchestration.Contracts;

public sealed record CreateAdvisoryRequest(Guid RequestId, Guid ExpectedWorkItemVersion,
    string Brief, bool Consent, int BudgetSeconds = 180);
public sealed record AdvisoryRequestCatalog(bool SubmissionEnabled, IReadOnlyList<AdvisoryRequestView> Requests);
public sealed record AdvisoryRequestView(Guid Id, Guid WorkItemId, Guid WorkItemVersion, Guid Version,
    string Brief, string State, bool CancelRequested, string? Reason, string? Draft, string? Model,
    long? InputTokens, long? OutputTokens, DateTimeOffset CreatedAtUtc, DateTimeOffset DeadlineUtc,
    DateTimeOffset? StartedAtUtc, DateTimeOffset? FinishedAtUtc);
