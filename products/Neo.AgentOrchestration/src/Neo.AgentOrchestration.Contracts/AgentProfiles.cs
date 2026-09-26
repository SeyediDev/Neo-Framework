namespace Neo.AgentOrchestration.Contracts;

public sealed record RoleProfileView(Guid Id, string Key, string Name, string? ScopeDescription, bool IsEnabled);
public sealed record AgentProfileView(Guid Id, Guid RoleProfileId, string Key, string Name, string Provider,
    string? Model, string? Instructions, string? SkillPath, bool IsEnabled);
public sealed record CreateRoleProfileRequest(string Key, string Name, string? ScopeDescription = null);
public sealed record UpdateRoleProfileRequest(string Name, string? ScopeDescription);
public sealed record CreateAgentProfileRequest(Guid RoleProfileId, string Key, string Name, string Provider,
    string? Model = null, string? Instructions = null, string? SkillPath = null);
public sealed record UpdateAgentProfileRequest(string Name, string Provider, string? Model, string? Instructions, string? SkillPath);
public sealed record SetProfileEnabledRequest(bool IsEnabled);
