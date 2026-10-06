namespace Neo.AgentOrchestration.Contracts;

public sealed record ProjectRepositoryBindingView(Guid Id, Guid ProjectId, string Provider, string RepositoryUrl,
    string RepositoryKey, string DefaultBranch, string? DevelopmentBranch, string? CiCdReference,
    string? SecretReference, bool IsEnabled, DateTimeOffset UpdatedAtUtc);
public sealed record UpsertProjectRepositoryBindingRequest(string Provider, string RepositoryUrl,
    string RepositoryKey, string DefaultBranch = "main", string? DevelopmentBranch = null,
    string? CiCdReference = null, string? SecretReference = null, bool IsEnabled = true);
