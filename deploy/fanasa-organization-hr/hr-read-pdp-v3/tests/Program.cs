using Fanasa.HrReadPdp.Tests;

try
{
    var count = WireTests.Run();
    count += SchemaTests.Run();
    count += await HttpTests.RunAsync();
    count += await FabricSourceTests.RunAsync();
    count += await ServiceTests.RunAsync();
    Console.WriteLine($"PASS: {count} local HR3 PDP HTTP assertions. No live TLS, introspection, provisioning or APISIX acceptance.");
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    Environment.ExitCode = 1;
}
