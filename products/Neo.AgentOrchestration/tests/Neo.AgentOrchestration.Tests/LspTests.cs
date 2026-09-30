using System.IO.Pipelines;
using System.Text;
using System.Text.Json;
using Neo.AgentOrchestration.Infrastructure.LanguageTools;
using Xunit;

namespace Neo.AgentOrchestration.Tests;

public sealed class LspTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task Frames_roundtrip_utf8_byte_lengths_and_reject_invalid_sizes_and_duplicate_headers()
    {
        using var stream = new MemoryStream();
        await LspFrames.Write(stream, new { jsonrpc = "2.0", method = "test", @params = "وزیر" }, Ct);
        stream.Position = 0;
        Assert.Equal("وزیر", (await LspFrames.Read(stream, Ct)).GetProperty("params").GetString());
        foreach (var header in new[] { "Content-Length: -1", "Content-Length: 4194305", "Content-Length: 2\r\nContent-Length: 2", "Other: 2", "Content-Type: application/json; charset=ascii\r\nContent-Length: 2" })
        {
            using var invalid = new MemoryStream(Encoding.ASCII.GetBytes(header + "\r\n\r\n{}"));
            await Assert.ThrowsAsync<InvalidDataException>(() => LspFrames.Read(invalid, Ct));
        }
    }
    [Fact]
    public void Workspace_guard_rejects_sibling_traversal_absolute_unc_and_nonfile_uris()
    {
        using var f = new Files();
        Assert.Equal(f.File, WorkspacePath.Resolve(f.Root, "Code.cs"));
        Assert.Equal(f.File, WorkspacePath.ResolveUri(f.Root, new Uri(f.File).AbsoluteUri));
        Assert.Throws<UnauthorizedAccessException>(() => WorkspacePath.Resolve(f.Root, "../outside.cs"));
        Assert.Throws<ArgumentException>(() => WorkspacePath.Resolve(f.Root, f.File));
        foreach (var uri in new[] { "https://example.com/Code.cs", "file://host/share/Code.cs", new Uri(f.File).AbsoluteUri + "?x=1" })
            Assert.Throws<UnauthorizedAccessException>(() => WorkspacePath.ResolveUri(f.Root, uri));
    }
    [Fact]
    public async Task Session_negotiates_capabilities_reads_diagnostics_and_locations_and_refuses_server_edits()
    {
        using var f = new Files(); using var input = new MemoryStream(); using var output = new MemoryStream();
        var location = new { uri = new Uri(f.File).AbsoluteUri, range = new { start = new { line = 0, character = 0 }, end = new { line = 0, character = 1 } } };
        await Reply(input, 1, new { capabilities = new { diagnosticProvider = new { }, definitionProvider = true, referencesProvider = true } });
        await LspFrames.Write(input, new { jsonrpc = "2.0", id = "edit-1", method = "workspace/applyEdit", @params = new { } }, Ct);
        await Reply(input, 2, new { kind = "full", items = new[] { new { severity = 1, message = "error" } } });
        await Reply(input, 3, new[] { location }); await Reply(input, 4, new[] { location }); await Reply(input, 5, (object?)null);
        input.Position = 0;
        await using (var session = new LspSession(input, output, f.Root))
        {
            await session.Initialize(Ct);
            Assert.Equal("utf-16", session.PositionEncoding);
            var document = await session.Open("Code.cs", "csharp", Ct);
            Assert.Equal(64, document.ContentHash.Length);
            Assert.Equal(1, (await session.Diagnostics(Ct)).GetArrayLength());
            Assert.Single((await session.Definition(0, 0, Ct)).EnumerateArray());
            Assert.Single((await session.References(0, 0, Ct)).EnumerateArray());
        }
        Assert.Equal("class Code { }", await System.IO.File.ReadAllTextAsync(f.File, Ct));
        output.Position = 0; var messages = new List<JsonElement>();
        while (output.Position < output.Length) messages.Add(await LspFrames.Read(output, Ct));
        Assert.False(messages.Single(x => x.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String).GetProperty("result").GetProperty("applied").GetBoolean());
        Assert.Equal("exit", messages.Last().GetProperty("method").GetString());
        Assert.DoesNotContain(messages, x => x.TryGetProperty("method", out var m) && m.GetString() == "workspace/executeCommand");
    }
    [Fact]
    public async Task Push_diagnostics_ignore_foreign_unversioned_and_stale_snapshots()
    {
        using var f = new Files(); using var input = new MemoryStream(); using var output = new MemoryStream();
        await Reply(input, 1, new { capabilities = new { } });
        await LspFrames.Write(input, new { jsonrpc = "2.0", method = "textDocument/publishDiagnostics", @params = new { uri = new Uri(f.File).AbsoluteUri, diagnostics = new[] { new { message = "unversioned" } } } }, Ct);
        foreach (var (uri, version) in new[] { (new Uri(f.File).AbsoluteUri, 0), ("file:///foreign.cs", 1), (new Uri(f.File).AbsoluteUri, 1) })
            await LspFrames.Write(input, new { jsonrpc = "2.0", method = "textDocument/publishDiagnostics", @params = new { uri, version, diagnostics = new[] { new { message = version == 1 ? "current" : "stale" } } } }, Ct);
        await Reply(input, 2, (object?)null); input.Position = 0;
        await using var session = new LspSession(input, output, f.Root);
        await session.Initialize(Ct); await session.Open("Code.cs", "csharp", Ct);
        Assert.Equal("current", (await session.Diagnostics(Ct))[0].GetProperty("message").GetString());
        await Assert.ThrowsAsync<NotSupportedException>(() => session.Definition(0, 0, Ct));
    }
    [Fact]
    public async Task Foreign_definition_is_rejected_without_following_its_uri()
    {
        using var f = new Files(); using var input = new MemoryStream(); using var output = new MemoryStream();
        await Reply(input, 1, new { capabilities = new { definitionProvider = true } });
        await Reply(input, 2, new { uri = "https://untrusted.example/code", range = new { } });
        await Reply(input, 3, (object?)null); input.Position = 0;
        await using var session = new LspSession(input, output, f.Root);
        await session.Initialize(Ct); await session.Open("Code.cs", "csharp", Ct);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => session.Definition(0, 0, Ct));
    }
    [Fact]
    public async Task Cancelled_request_sends_cancel_and_session_cannot_be_reused()
    {
        using var f = new Files(); var pipe = new Pipe(); using var output = new MemoryStream();
        await Reply(pipe.Writer.AsStream(), 1, new { capabilities = new { diagnosticProvider = new { } } });
        await using var session = new LspSession(pipe.Reader.AsStream(), output, f.Root);
        await session.Initialize(Ct); await session.Open("Code.cs", "csharp", Ct);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.Diagnostics(timeout.Token));
        Assert.Contains("$/cancelRequest", Encoding.UTF8.GetString(output.ToArray()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.Diagnostics(Ct));
        await pipe.Writer.CompleteAsync(); await pipe.Reader.CompleteAsync();
    }
    [Fact]
    public async Task Unsupported_encoding_fails_negotiation_instead_of_misreading_positions()
    {
        using var f = new Files(); using var input = new MemoryStream(); using var output = new MemoryStream();
        await Reply(input, 1, new { capabilities = new { positionEncoding = "utf-8" } }); input.Position = 0;
        await using var session = new LspSession(input, output, f.Root);
        await Assert.ThrowsAsync<InvalidDataException>(() => session.Initialize(Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.Initialize(Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.Open("Code.cs", "csharp", Ct));
    }
    private static Task Reply(Stream stream, int id, object? result) => LspFrames.Write(stream, new { jsonrpc = "2.0", id, result }, Ct);
    private sealed class Files : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "neo-lsp-" + Guid.NewGuid().ToString("N"));
        public string File => Path.Combine(Root, "Code.cs");
        public Files() { Directory.CreateDirectory(Root); System.IO.File.WriteAllText(File, "class Code { }"); }
        public void Dispose() { System.IO.File.Delete(File); Directory.Delete(Root); }
    }
}
