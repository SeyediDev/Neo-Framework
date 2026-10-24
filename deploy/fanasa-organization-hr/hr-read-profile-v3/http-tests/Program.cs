using Fanasa.HrReadCandidate.Http.Tests;

try
{
    var count = EnvelopeTests.Run();
    count += await HttpReceiverTests.RunAsync();
    count += await KestrelEndpointTests.RunAsync();
    Console.WriteLine($"PASS: {count} local HTTP/envelope assertions. No live APISIX, introspection or deployment acceptance.");
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    Environment.ExitCode = 1;
}
