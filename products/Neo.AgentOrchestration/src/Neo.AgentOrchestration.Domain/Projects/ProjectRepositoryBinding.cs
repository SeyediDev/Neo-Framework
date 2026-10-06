using Neo.Domain.Entities.Base;

namespace Neo.AgentOrchestration.Domain.Projects;

// Provider-neutral project connection. Provider adapters and secrets belong to the delivery product.
public sealed class ProjectRepositoryBinding : BaseEntity<Guid>
{
    public Guid OrganizationId { get; private set; }
    public Guid WorkspaceId { get; private set; }
    public Guid ProjectId { get; private set; }
    public string Provider { get; private set; } = "";
    public string RepositoryUrl { get; private set; } = "";
    public string RepositoryKey { get; private set; } = "";
    public string DefaultBranch { get; private set; } = "main";
    public string? DevelopmentBranch { get; private set; }
    public string? CiCdReference { get; private set; }
    public string? SecretReference { get; private set; }
    public bool IsEnabled { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public ProjectRepositoryBinding() { }
    public static ProjectRepositoryBinding Create(WorkspaceScope scope, Project project, string provider,
        string repositoryUrl, string repositoryKey, string defaultBranch, string? developmentBranch,
        string? ciCdReference, string? secretReference, DateTimeOffset now)
    {
        project.RequireScope(scope);
        return new() { Id = Guid.NewGuid(), OrganizationId = scope.OrganizationId, WorkspaceId = scope.WorkspaceId,
            ProjectId = project.Id, Provider = Required(provider, 80), RepositoryUrl = Required(repositoryUrl, 2000),
            RepositoryKey = Required(repositoryKey, 200), DefaultBranch = Required(defaultBranch, 250),
            DevelopmentBranch = Optional(developmentBranch, 250), CiCdReference = Optional(ciCdReference, 500),
            SecretReference = Optional(secretReference, 500), IsEnabled = true, UpdatedAtUtc = now.ToUniversalTime() };
    }
    public void Update(WorkspaceScope scope, string provider, string repositoryUrl, string repositoryKey,
        string defaultBranch, string? developmentBranch, string? ciCdReference, string? secretReference,
        bool enabled, DateTimeOffset now)
    {
        RequireScope(scope); Provider = Required(provider, 80); RepositoryUrl = Required(repositoryUrl, 2000);
        RepositoryKey = Required(repositoryKey, 200); DefaultBranch = Required(defaultBranch, 250);
        DevelopmentBranch = Optional(developmentBranch, 250); CiCdReference = Optional(ciCdReference, 500);
        SecretReference = Optional(secretReference, 500); IsEnabled = enabled; UpdatedAtUtc = now.ToUniversalTime();
    }
    public void RequireScope(WorkspaceScope scope) { ArgumentNullException.ThrowIfNull(scope); scope.Require(OrganizationId, WorkspaceId); }
    private static string Required(string value, int max) { ArgumentException.ThrowIfNullOrWhiteSpace(value); value = value.Trim(); if (value.Length > max) throw new ArgumentException($"Value cannot exceed {max} characters."); return value; }
    private static string? Optional(string? value, int max) => string.IsNullOrWhiteSpace(value) ? null : Required(value, max);
}