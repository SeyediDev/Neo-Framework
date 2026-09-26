namespace Neo.AgentOrchestration.Contracts;

public sealed record ProductInfo(string Name, string Stage, bool LegacyRetained, bool CleanupRequiresApproval);
