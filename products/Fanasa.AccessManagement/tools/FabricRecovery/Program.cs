using System.Text.Json;
using Fanasa.AccessManagement.Web.Persistence;

try
{
    var result = args switch
    {
        ["backup", var fabric, var registry, var destination, "--offline"] => FabricRecovery.Backup(fabric, registry, destination, true),
        ["verify", var bundle] => FabricRecovery.Verify(bundle),
        ["restore", var bundle, var destination, "--offline"] => FabricRecovery.Restore(bundle, destination, true),
        _ => throw new ArgumentException("Usage: backup <fabric.db> <platform-registry.db> <new-bundle-dir> --offline | verify <bundle-dir> | restore <bundle-dir> <new-data-dir> --offline")
    };
    Console.WriteLine(JsonSerializer.Serialize(new { result.Format, result.CreatedAt, Files = result.Files.Select(x => new { x.Name, x.Length }), Status = "verified" }));
}
catch (Exception ex) when (ex is ArgumentException or IOException or System.Data.Common.DbException or JsonException)
{ Console.Error.WriteLine(ex.Message); Environment.ExitCode = 1; }
