using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Neo.AgentOrchestration.Infrastructure.LanguageTools;

// Explicit read-only LSP 3.17 subset. The caller owns transport/process isolation.
public sealed class LspSession(Stream input, Stream output, string workspace) : IAsyncDisposable
{
    private readonly string root = WorkspacePath.Root(workspace);
    private readonly SemaphoreSlim gate = new(1);
    private int sequence;
    private bool initialized, initializationAttempted, stopped;
    private JsonElement capabilities;
    private bool roslynReady;
    private string? documentUri;
    private JsonElement? published;
    public string PositionEncoding { get; private set; } = "utf-16";
    public bool Supports(string name) => capabilities.ValueKind == JsonValueKind.Object &&
        capabilities.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.Object;

    public async Task Initialize(CancellationToken ct)
    {
        if (initializationAttempted || stopped) throw new InvalidOperationException("Session already initialized or closed.");
        initializationAttempted = true;
        var uri = new Uri(root + Path.DirectorySeparatorChar).AbsoluteUri;
        var result = await Request("initialize", new
        {
            processId = Environment.ProcessId,
            rootUri = uri,
            workspaceFolders = new[] { new { uri, name = "authorized-workspace" } },
            clientInfo = new { name = "Neo.AgentOrchestration", version = "0.1" },
            capabilities = new
            {
                general = new { positionEncodings = new[] { "utf-16" } },
                workspace = new { applyEdit = false, workspaceFolders = true, configuration = true },
                textDocument = new
                {
                    synchronization = new { dynamicRegistration = false },
                    definition = new { dynamicRegistration = false, linkSupport = true },
                    references = new { dynamicRegistration = false },
                    publishDiagnostics = new { versionSupport = true },
                    diagnostic = new { dynamicRegistration = false, relatedDocumentSupport = false }
                }
            }
        }, ct);
        capabilities = result.GetProperty("capabilities").Clone();
        if (capabilities.TryGetProperty("positionEncoding", out var encoding)) PositionEncoding = encoding.GetString()!;
        if (PositionEncoding != "utf-16") throw new InvalidDataException("Unsupported position encoding.");
        initialized = true;
        await Notify("initialized", new { }, ct);
    }

    public async Task<LspDocument> Open(string relativePath, string languageId, CancellationToken ct)
    {
        RequireInitialized();
        if (documentUri is not null) throw new InvalidOperationException("Only one immutable document per session.");
        if (string.IsNullOrWhiteSpace(languageId) || languageId.Length > 64) throw new ArgumentException("Invalid language.");
        var path = WorkspacePath.Resolve(root, relativePath);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > 2 * 1024 * 1024) throw new ArgumentException("Document exceeds 2 MiB.");
        using var reader = new StreamReader(file, new UTF8Encoding(false, true), true, leaveOpen: true);
        var text = await reader.ReadToEndAsync(ct);
        documentUri = new Uri(path).AbsoluteUri;
        await Notify("textDocument/didOpen", new { textDocument = new { uri = documentUri, languageId, version = 1, text } }, ct);
        return new(relativePath, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))), 1);
    }

    public async Task<JsonElement> Diagnostics(CancellationToken ct)
    {
        RequireDocument();
        if (Supports("diagnosticProvider"))
        {
            var result = await Request("textDocument/diagnostic", new { textDocument = new { uri = documentUri } }, ct);
            if (result.GetProperty("kind").GetString() != "full") throw new InvalidDataException("A full report is required.");
            return result.GetProperty("items").Clone();
        }
        await gate.WaitAsync(ct);
        try
        {
            while (published is null) await Handle(await LspFrames.Read(input, ct), ct);
            return published.Value;
        }
        catch (OperationCanceledException) { stopped = true; throw; }
        finally { gate.Release(); }
    }

    public Task<JsonElement> Definition(int line, int character, CancellationToken ct)
        => Locations("textDocument/definition", "definitionProvider", line, character, ct);
    public Task<JsonElement> References(int line, int character, CancellationToken ct)
        => Locations("textDocument/references", "referencesProvider", line, character, ct);
    private async Task<JsonElement> Locations(string method, string capability, int line, int character, CancellationToken ct)
    {
        RequireDocument();
        if (!Supports(capability)) throw new NotSupportedException("Language server capability unavailable.");
        if (line < 0 || character < 0) throw new ArgumentException("Position must be nonnegative UTF-16 coordinates.");
        object parameters = method.EndsWith("references", StringComparison.Ordinal)
            ? new { textDocument = new { uri = documentUri }, position = new { line, character }, context = new { includeDeclaration = true } }
            : new { textDocument = new { uri = documentUri }, position = new { line, character } };
        var result = await Request(method, parameters, ct);
        if (result.ValueKind == JsonValueKind.Null) return result;
        var locations = result.ValueKind == JsonValueKind.Array ? result.EnumerateArray().ToArray() : [result];
        foreach (var location in locations)
        {
            var uri = location.TryGetProperty("targetUri", out var target) ? target : location.GetProperty("uri");
            WorkspacePath.ResolveUri(root, uri.GetString()!); // Reject foreign results, never follow them.
        }
        return result;
    }

    private void RequireInitialized()
    { if (!initialized || stopped) throw new InvalidOperationException("Session is not ready."); }
    private void RequireDocument()
    { RequireInitialized(); if (documentUri is null) throw new InvalidOperationException("Open a document first."); }
    private Task Notify(string method, object? parameters, CancellationToken ct)
        => LspFrames.Write(output, parameters is null ? new { jsonrpc = "2.0", method } : (object)new { jsonrpc = "2.0", method, @params = parameters }, ct);
    private async Task<JsonElement> Request(string method, object? parameters, CancellationToken ct)
    {
        if (stopped) throw new InvalidOperationException("Session closed.");
        await gate.WaitAsync(ct);
        var id = ++sequence;
        try
        {
            await LspFrames.Write(output, parameters is null ? new { jsonrpc = "2.0", id, method } : (object)new { jsonrpc = "2.0", id, method, @params = parameters }, ct);
            while (true)
            {
                var message = await LspFrames.Read(input, ct);
                if (!message.TryGetProperty("method", out _) && message.TryGetProperty("id", out var responseId) &&
                    responseId.ValueKind == JsonValueKind.Number && responseId.GetInt32() == id)
                {
                    if (message.TryGetProperty("error", out _)) throw new InvalidDataException("Language server rejected request.");
                    return message.GetProperty("result").Clone();
                }
                await Handle(message, ct);
            }
        }
        catch (OperationCanceledException)
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            try { await Notify("$/cancelRequest", new { id }, cancel.Token); } catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
            stopped = true; // A partial frame cannot be safely reused.
            throw;
        }
        finally { gate.Release(); }
    }

    // Opt-in Roslyn profile extension, not a generic LSP readiness guarantee.
    public async Task WaitForRoslynProjectInitialization(CancellationToken ct)
    {
        RequireInitialized();
        await gate.WaitAsync(ct);
        try
        {
            while (!roslynReady) await Handle(await LspFrames.Read(input, ct), ct);
        }
        catch (OperationCanceledException) { stopped = true; throw; }
        finally { gate.Release(); }
    }

    private object?[] ConfigurationDefaults(JsonElement message)
    {
        if (!message.TryGetProperty("params", out var data) || data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array ||
            items.GetArrayLength() > 256) throw new ArgumentException();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw new ArgumentException();
            foreach (var property in item.EnumerateObject())
            {
                if (property.Name is not ("scopeUri" or "section") ||
                    property.Value.ValueKind != JsonValueKind.String ||
                    property.Value.GetString()!.Length > 1024) throw new ArgumentException();
                if (property.Name == "scopeUri")
                {
                    var value = property.Value.GetString()!;
                    var rootUri = new Uri(root + Path.DirectorySeparatorChar).AbsoluteUri;
                    if (value != rootUri) WorkspacePath.ResolveUri(root, value);
                }
            }
        }
        return new object?[items.GetArrayLength()];
    }
    private async Task Handle(JsonElement message, CancellationToken ct)
    {
        if (!message.TryGetProperty("method", out var method)) return;
        if (message.TryGetProperty("id", out var id))
        {
            if (method.GetString() == "workspace/configuration")
            {
                object response;
                try { response = new { jsonrpc = "2.0", id, result = ConfigurationDefaults(message) }; }
                catch (Exception ex) when (ex is ArgumentException or UnauthorizedAccessException)
                { response = new { jsonrpc = "2.0", id, error = new { code = -32602, message = "Invalid configuration scope or items." } }; }
                await LspFrames.Write(output, response, ct);
            }
            else if (method.GetString() == "workspace/applyEdit")
                await LspFrames.Write(output, new { jsonrpc = "2.0", id, result = new { applied = false, failureReason = "Read-only client." } }, ct);
            else
                await LspFrames.Write(output, new { jsonrpc = "2.0", id, error = new { code = -32601, message = "Unsupported client method." } }, ct);
            return;
        }
        if (method.GetString() == "workspace/projectInitializationComplete") { roslynReady = true; return; }
        if (method.GetString() != "textDocument/publishDiagnostics" || documentUri is null) return;
        var data = message.GetProperty("params");
        if (data.GetProperty("uri").GetString() != documentUri) return;
        // Unversioned push reports cannot establish freshness for this immutable snapshot.
        if (!data.TryGetProperty("version", out var version) || version.ValueKind != JsonValueKind.Number || version.GetInt32() != 1) return;
        published = data.GetProperty("diagnostics").Clone();
    }

    public async Task Close(CancellationToken ct)
    {
        if (stopped) return;
        try
        {
            if (initialized)
            {
                if (documentUri is not null) await Notify("textDocument/didClose", new { textDocument = new { uri = documentUri } }, ct);
                await Request("shutdown", null, ct);
            }
            await Notify("exit", null, ct);
        }
        finally { stopped = true; }
    }
    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await Close(timeout.Token); }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or InvalidDataException) { }
    }
}

public sealed record LspDocument(string RelativePath, string ContentHash, int Version);

public static class WorkspacePath
{
    public static string Root(string value)
    {
        if (!Path.IsPathFullyQualified(value)) throw new ArgumentException("Absolute workspace required.");
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        if (!Directory.Exists(root)) throw new ArgumentException("Workspace missing.");
        RejectLinks(root);
        return root;
    }
    public static string Resolve(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':'))
            throw new ArgumentException("Relative document required.");
        var path = Path.GetFullPath(Path.Combine(root, relative));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, comparison)) throw new UnauthorizedAccessException("Outside workspace.");
        RejectLinks(path);
        if (!File.Exists(path)) throw new ArgumentException("Document missing.");
        return path;
    }
    public static string ResolveUri(string root, string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !uri.IsFile || uri.IsUnc ||
            uri.Host.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new UnauthorizedAccessException("Nonlocal document URI.");
        return Resolve(root, Path.GetRelativePath(root, uri.LocalPath));
    }
    private static void RejectLinks(string path)
    {
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new UnauthorizedAccessException("Symlinks and junctions are not allowed.");
    }
}

public static class LspFrames
{
    public const int MaximumBytes = 4 * 1024 * 1024;
    public static async Task Write(Stream stream, object value, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (bytes.Length > MaximumBytes) throw new InvalidDataException("Oversize LSP frame.");
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {bytes.Length}\r\n\r\n"), ct);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }
    public static async Task<JsonElement> Read(Stream stream, CancellationToken ct)
    {
        var header = new List<byte>(); var one = new byte[1];
        while (true)
        {
            await stream.ReadExactlyAsync(one, ct); header.Add(one[0]);
            if (header.Count > 8192) throw new InvalidDataException("Oversize LSP header.");
            if (header.Count >= 4 && header[^4] == 13 && header[^3] == 10 && header[^2] == 13 && header[^1] == 10) break;
        }
        int? length = null;
        foreach (var line in Encoding.ASCII.GetString(header.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries))
        {
            var split = line.IndexOf(':'); if (split < 1) throw new InvalidDataException("Invalid LSP header.");
            var key = line[..split]; var value = line[(split + 1)..].Trim();
            if (key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                if (length is not null || !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var count) ||
                    count < 1 || count > MaximumBytes) throw new InvalidDataException("Invalid LSP length.");
                length = count;
            }
            if (key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) &&
                value.Contains("charset=", StringComparison.OrdinalIgnoreCase) &&
                !value.EndsWith("charset=utf-8", StringComparison.OrdinalIgnoreCase) &&
                !value.EndsWith("charset=utf8", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Unsupported LSP encoding.");
        }
        var body = new byte[length ?? throw new InvalidDataException("Missing LSP length.")];
        await stream.ReadExactlyAsync(body, ct);
        using var json = JsonDocument.Parse(body);
        if (json.RootElement.GetProperty("jsonrpc").GetString() != "2.0") throw new InvalidDataException("Invalid JSON-RPC version.");
        return json.RootElement.Clone();
    }
}
