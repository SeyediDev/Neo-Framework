using Neo.AgentOrchestration.Application.Workspace;
using Neo.AgentOrchestration.Infrastructure.Persistence;

namespace Neo.AgentOrchestration.Api;

internal static class OrchestrationApiStorage
{
    private static string? ConfiguredConnection(IConfiguration configuration)
    {
        var named = configuration.GetConnectionString("Orchestration");
        return string.IsNullOrWhiteSpace(named) ? configuration["NEO_ORCHESTRATION_SQL"] : named;
    }
    public static string Connection(IConfiguration configuration)
    {
        var sql = ConfiguredConnection(configuration);
        if (string.IsNullOrWhiteSpace(sql)) throw new OrchestrationUnavailableException();
        OrchestrationProvisioner.ValidateDestination(sql, configuration["Orchestration:DatabaseName"] ?? "NeoAgentOrchestration");
        return sql;
    }
    public static void ValidateConfiguration(IConfiguration configuration)
    {
        if (!string.IsNullOrWhiteSpace(ConfiguredConnection(configuration))) _ = Connection(configuration);
    }
}
