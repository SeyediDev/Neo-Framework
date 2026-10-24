using Fanasa.HrReadPdp.Tests;
using Fanasa.HrReadHost.Tests;

var selectedSuite = args.Length == 2 && args[0] == "--suite" ? args[1] : null;
try
{
    var count = await Suite("wire/schema", () => Task.FromResult(WireTests.Run() + SchemaTests.Run()));
    count += await Suite("named JWT/native HTTP", HttpTests.RunAsync);
    count += await Suite("Fabric source", FabricSourceTests.RunAsync);
    count += await Suite("PDP service", ServiceTests.RunAsync);
    count += await Suite("atomic publication", () => Task.FromResult(PublicationTests.Run()));
    count += await Suite("central host records", CentralHostRecordsTests.RunAsync);
    count += await Suite("dual introspection", IntrospectionTests.RunAsync);
    count += await Suite("legacy coexistence", () => Task.FromResult(CompatibilityTests.Run()));
    count += await Suite("central consumer authority", CentralIntrospectionAuthorityTests.RunAsync);
    count += await Suite("actual host HTTP", HostEndpointTests.RunAsync);
    count += await Suite("runtime DI registration", HostServicesTests.RunAsync);
    Console.WriteLine($"PASS: {count} HR3 actual-host source integration assertions ({(selectedSuite is null ? "complete" : "targeted")}). No live authority or deployment acceptance.");
}
catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }

async Task<int> Suite(string name, Func<Task<int>> run)
{
    if (selectedSuite is not null && !name.Contains(selectedSuite, StringComparison.OrdinalIgnoreCase)) return 0;
    Console.WriteLine($"RUN: {name}");
    var count = await run();
    Console.WriteLine($"PASS: {name}: {count}");
    return count;
}
