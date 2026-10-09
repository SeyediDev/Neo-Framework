using System.Text.Json;
using System.Text.Json.Serialization;
using Neo.AgentOrchestration.Infrastructure.Persistence;
using Neo.AgentOrchestration.Infrastructure.ExternalExecution;

namespace Neo.AgentOrchestration.Provisioning;

public static class ProvisioningCommand
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        AllowDuplicateProperties = false
    };
    private const string Usage = """
        neo-agent init <manifest.json> <NeoAgentOrchestration[_installation]>
        neo-agent migrate <NeoAgentOrchestration[_installation]>
        neo-agent seed <NeoAgentOrchestration[_installation]> <manifest.json>
        neo-agent health <NeoAgentOrchestration[_installation]>
        neo-agent gateway-migrate <FanasaAgentGateway[_installation]>
        neo-agent gateway-health <FanasaAgentGateway[_installation]>
        Set NEO_ORCHESTRATION_SQL privately for migrate/seed/health. Edit the init template before seed.
        Set FANASA_AGENT_GATEWAY_SQL privately for gateway-migrate/gateway-health; never put connections in arguments.
        init writes only a new credential-free manifest. No command imports legacy data or dispatches agents.
        Exit: 0 success, 1 operational failure, 2 invalid input, 3 schema not ready, 4 seed conflict, 130 cancelled.
        """;

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        if (args is ["--help"] or ["help"])
        {
            await output.WriteLineAsync(Usage); return 0;
        }
        if (!(args is ["init", _, _] or ["migrate", _] or ["seed", _, _] or ["health", _] or
            ["gateway-migrate", _] or ["gateway-health", _]))
        {
            await error.WriteLineAsync(Usage); return 2;
        }
        try
        {
            ct.ThrowIfCancellationRequested();
            if (args[0] is "gateway-migrate" or "gateway-health")
            {
                var gatewayConnection = Environment.GetEnvironmentVariable("FANASA_AGENT_GATEWAY_SQL");
                if (string.IsNullOrWhiteSpace(gatewayConnection))
                {
                    await error.WriteLineAsync("Missing FANASA_AGENT_GATEWAY_SQL. No changes made."); return 2;
                }
                GatewayProvisioner.ValidateDestination(gatewayConnection, args[1]);
                if (args[0] == "gateway-migrate")
                {
                    await GatewayProvisioner.MigrateAsync(gatewayConnection, args[1], ct);
                    await output.WriteLineAsync("Gateway migration applied. No reservations imported or agents dispatched; verify with gateway-health.");
                    return 0;
                }
                var gatewayHealth = await GatewayProvisioner.HealthAsync(gatewayConnection, args[1], ct);
                await output.WriteLineAsync(JsonSerializer.Serialize(gatewayHealth, Json));
                return gatewayHealth.Ready ? 0 : gatewayHealth.Reason == "connection-unavailable" ? 1 : 3;
            }
            if (args[0] == "init")
            {
                // Validate the public name without reading any secret or opening SQL.
                OrchestrationProvisioner.ValidateDestination($"Server=localhost;Database={args[2]}", args[2]);
                var bytes = JsonSerializer.SerializeToUtf8Bytes(BootstrapManifest.Template(args[2]), Json);
                await using var file = new FileStream(args[1], FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await file.WriteAsync(bytes, ct);
                await output.WriteLineAsync("Template created. Review organization/workspace/project/roles before explicit migrate and seed. No database changes.");
                return 0;
            }
            var connection = Environment.GetEnvironmentVariable("NEO_ORCHESTRATION_SQL");
            if (string.IsNullOrWhiteSpace(connection))
            {
                await error.WriteLineAsync("Missing NEO_ORCHESTRATION_SQL. No changes made."); return 2;
            }
            OrchestrationProvisioner.ValidateDestination(connection, args[1]);
            switch (args[0])
            {
                case "migrate":
                    await OrchestrationProvisioner.MigrateAsync(connection, args[1], ct);
                    await output.WriteLineAsync("Destination schema is current. No legacy data was imported, redirected or removed.");
                    return 0;
                case "health":
                    var health = await OrchestrationBootstrap.HealthAsync(connection, args[1], ct);
                    await output.WriteLineAsync(JsonSerializer.Serialize(health, Json));
                    return health.Ready ? 0 : 3;
                case "seed":
                    await using (var file = new FileStream(args[2], FileMode.Open, FileAccess.Read, FileShare.Read))
                    {
                        if (file.Length > 65536) throw new ArgumentException("Manifest exceeds 64 KiB.");
                        var manifest = await JsonSerializer.DeserializeAsync<BootstrapManifest>(file, Json, ct)
                            ?? throw new JsonException();
                        var result = await OrchestrationBootstrap.SeedAsync(connection, args[1], manifest, ct);
                        await output.WriteLineAsync(JsonSerializer.Serialize(result, Json));
                        return 0;
                    }
                default: throw new InvalidOperationException();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await error.WriteLineAsync("Cancelled. Reconcile current state before retrying any command."); return 130;
        }
        catch (BootstrapConflictException)
        {
            await error.WriteLineAsync("Existing seed configuration differs or is disabled. No seed changes committed; review through the authorized Web/API."); return 4;
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException)
        {
            await error.WriteLineAsync("Invalid input, manifest or destination. No changes made."); return 2;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Exception messages/paths/driver details may include credentials; never emit them.
            await error.WriteLineAsync($"Command failed ({ex.GetType().Name}). Check configuration, permissions and connectivity. Reconcile before retry. Legacy unchanged.");
            return 1;
        }
    }
}
