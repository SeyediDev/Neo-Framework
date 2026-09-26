using Neo.AgentOrchestration.Infrastructure.Persistence;

if (args.Length != 2 || args[0] != "migrate")
{
    Console.Error.WriteLine("Usage: provisioning migrate NeoAgentOrchestration[_installation]. Set NEO_ORCHESTRATION_SQL privately.");
    return 2;
}
var connection = Environment.GetEnvironmentVariable("NEO_ORCHESTRATION_SQL");
if (string.IsNullOrWhiteSpace(connection))
{
    Console.Error.WriteLine("Missing NEO_ORCHESTRATION_SQL. No changes made.");
    return 2;
}
using var shutdown = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; shutdown.Cancel(); };
try
{
    await OrchestrationProvisioner.MigrateAsync(connection, args[1], shutdown.Token);
    Console.WriteLine("Destination schema is current. No legacy data was imported, redirected or removed.");
    return 0;
}
catch (Exception ex) when (ex is not OutOfMemoryException)
{
    // Do not emit connection details, credentials or driver exception messages.
    Console.Error.WriteLine($"Migration failed ({ex.GetType().Name}). Check destination, permissions and connectivity. Legacy unchanged.");
    return 1;
}
