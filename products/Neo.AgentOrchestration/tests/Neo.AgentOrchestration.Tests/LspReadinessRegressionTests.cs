using System.IO.Pipelines;
using System.Text.Json;
using Neo.AgentOrchestration.Infrastructure.LanguageTools;
using Xunit;

namespace Neo.AgentOrchestration.Tests;

public sealed class LspReadinessRegressionTests
{
    [Fact]
    public async Task Configuration_defaults_are_bounded_and_dynamic_registration_stays_rejected()
    {
        using var input = new MemoryStream(); using var output = new MemoryStream();
        var root = Path.GetTempPath();
        await LspFrames.Write(input, new { jsonrpc = "2.0", id = 1, result = new { capabilities = new { } } }, TestContext.Current.CancellationToken);
        foreach (var request in new object[] {
            new { jsonrpc = "2.0", id = "valid", method = "workspace/configuration", @params = new { items = Enumerable.Repeat(new { section = "csharp" }, 256).ToArray() } },
            new { jsonrpc = "2.0", id = "foreign", method = "workspace/configuration", @params = new { items = new[] { new { scopeUri = "https://example.com" } } } },
            new { jsonrpc = "2.0", id = "large", method = "workspace/configuration", @params = new { items = Enumerable.Repeat(new { section = "x" }, 257).ToArray() } },
            new { jsonrpc = "2.0", id = "malformed", method = "workspace/configuration", @params = new { items = "bad" } },
            new { jsonrpc = "2.0", id = "dynamic", method = "client/registerCapability", @params = new { } },
            new { jsonrpc = "2.0", method = "workspace/projectInitializationComplete" },
            new { jsonrpc = "2.0", id = 2, result = (object?)null }
        }) await LspFrames.Write(input, request, TestContext.Current.CancellationToken);
        input.Position = 0;
        await using (var session = new LspSession(input, output, root))
        {
            await session.Initialize(TestContext.Current.CancellationToken);
            await session.WaitForRoslynProjectInitialization(TestContext.Current.CancellationToken);
            await session.Close(TestContext.Current.CancellationToken);
        }
        output.Position = 0; var messages = new List<JsonElement>();
        while (output.Position < output.Length) messages.Add(await LspFrames.Read(output, TestContext.Current.CancellationToken));
        JsonElement Find(string id) => messages.Single(x => x.TryGetProperty("id", out var v) && v.ValueKind == JsonValueKind.String && v.GetString() == id);
        Assert.Equal(256, Find("valid").GetProperty("result").GetArrayLength());
        Assert.Equal(JsonValueKind.Null, Find("valid").GetProperty("result")[0].ValueKind);
        foreach (var id in new[] { "foreign", "large", "malformed" }) Assert.Equal(-32602, Find(id).GetProperty("error").GetProperty("code").GetInt32());
        Assert.Equal(-32601, Find("dynamic").GetProperty("error").GetProperty("code").GetInt32());
    }

    [Fact]
    public async Task Missing_readiness_cancels_and_invalidates_session()
    {
        var pipe = new Pipe(); using var output = new MemoryStream();
        await LspFrames.Write(pipe.Writer.AsStream(), new { jsonrpc = "2.0", id = 1, result = new { capabilities = new { } } }, TestContext.Current.CancellationToken);
        await using var session = new LspSession(pipe.Reader.AsStream(), output, Path.GetTempPath());
        await session.Initialize(TestContext.Current.CancellationToken);
        using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.WaitForRoslynProjectInitialization(deadline.Token));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.WaitForRoslynProjectInitialization(TestContext.Current.CancellationToken));
        await pipe.Writer.CompleteAsync(); await pipe.Reader.CompleteAsync();
    }
}
