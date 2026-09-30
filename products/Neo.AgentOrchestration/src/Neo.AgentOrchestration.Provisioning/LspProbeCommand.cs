using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Neo.AgentOrchestration.Infrastructure.LanguageTools;

namespace Neo.AgentOrchestration.Provisioning;

// Only explicit local CLI invocation can launch a server. No API accepts executable paths.
public static class LspProbeCommand
{
    private sealed record Configuration(string Workspace, string Document, string LanguageId,
        string Executable, string[] Arguments, int Line, int Character, int TimeoutSeconds = 60);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true, AllowDuplicateProperties = false
    };
    public static async Task<int> Run(string[] args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        if (args is not ["lsp", _, "--allow-server-execution"])
        { await error.WriteLineAsync("neo-agent lsp <private-config.json> --allow-server-execution"); return 2; }
        Process? process = null;
        Task? stderr = null;
        try
        {
            await using var file = File.OpenRead(args[1]);
            if (file.Length > 65536) throw new ArgumentException();
            var config = await JsonSerializer.DeserializeAsync<Configuration>(file, Json, ct) ?? throw new ArgumentException();
            var root = WorkspacePath.Root(config.Workspace);
            WorkspacePath.Resolve(root, config.Document);
            if (!Path.IsPathFullyQualified(config.Executable) || !File.Exists(config.Executable) ||
                config.TimeoutSeconds is < 1 or > 300 || config.Line < 0 || config.Character < 0 ||
                config.Arguments.Length > 32 || config.Arguments.Any(a => a.Length > 4096))
                throw new ArgumentException();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));
            var start = new ProcessStartInfo(config.Executable)
            {
                WorkingDirectory = root, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            // Do not pass database, harness, cloud or API credentials to the child.
            var allowed = new[] { "PATH", "SystemRoot", "WINDIR", "TEMP", "TMP", "USERPROFILE", "HOME",
                "DOTNET_ROOT", "DOTNET_ROOT_X64", "ProgramFiles", "ProgramFiles(x86)", "LOCALAPPDATA", "APPDATA" };
            start.Environment.Clear();
            foreach (var name in allowed)
                if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
            start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            foreach (var argument in config.Arguments) start.ArgumentList.Add(argument);
            process = Process.Start(start) ?? throw new InvalidOperationException();
            stderr = Drain(process.StandardError); // Discard untrusted server logs without buffering/secrets.
            await using (var session = new LspSession(process.StandardOutput.BaseStream, process.StandardInput.BaseStream, root))
            {
                await session.Initialize(deadline.Token);
                var document = await session.Open(config.Document, config.LanguageId, deadline.Token);
                var diagnostics = await session.Diagnostics(deadline.Token);
                var definition = session.Supports("definitionProvider")
                    ? await session.Definition(config.Line, config.Character, deadline.Token) : (JsonElement?)null;
                var references = session.Supports("referencesProvider")
                    ? await session.References(config.Line, config.Character, deadline.Token) : (JsonElement?)null;
                // Detect ordinary on-disk changes during analysis; never label stale reports current.
                var current = await File.ReadAllTextAsync(WorkspacePath.Resolve(root, config.Document), deadline.Token);
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(current)));
                if (hash != document.ContentHash) throw new InvalidOperationException("Document changed during analysis.");
                var counts = diagnostics.EnumerateArray().GroupBy(x => x.TryGetProperty("severity", out var s) ? s.GetInt32() : 0)
                    .ToDictionary(x => x.Key.ToString(System.Globalization.CultureInfo.InvariantCulture), x => x.Count());
                await session.Close(deadline.Token);
                await process.WaitForExitAsync(deadline.Token);
                if (process.ExitCode != 0) throw new InvalidOperationException("Language server exited unsuccessfully.");
                await output.WriteLineAsync(JsonSerializer.Serialize(new
                {
                    protocol = "LSP 3.17 capability subset", session.PositionEncoding, document,
                    diagnostics = new { count = diagnostics.GetArrayLength(), bySeverity = counts },
                    definition = Locations(root, definition), references = Locations(root, references),
                    observedAtUtc = DateTimeOffset.UtcNow,
                    scope = "Single immutable document; diagnostics are not build/test acceptance.",
                    sourceTextExported = false
                }, Json));
            }
            return 0;
        }
        catch (OperationCanceledException)
        { await error.WriteLineAsync(ct.IsCancellationRequested ? "LSP cancelled." : "LSP deadline exceeded; no completed report."); return ct.IsCancellationRequested ? 130 : 1; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { await error.WriteLineAsync($"LSP failed ({ex.GetType().Name}); no completed report. Check private server configuration."); return 1; }
        finally
        {
            if (process is not null)
            {
                using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await process.WaitForExitAsync(stop.Token); }
                catch (OperationCanceledException) { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                if (stderr is not null) { try { await stderr.WaitAsync(TimeSpan.FromSeconds(2)); } catch (TimeoutException) { } }
                process.Dispose();
            }
        }
    }
    private static object Locations(string root, JsonElement? result)
    {
        if (result is null) return new { supported = false, locations = Array.Empty<object>() };
        var value = result.Value;
        var values = value.ValueKind == JsonValueKind.Null ? [] :
            value.ValueKind == JsonValueKind.Array ? value.EnumerateArray().ToArray() : [value];
        return new { supported = true, locations = values.Select(x => new
        {
            document = Path.GetRelativePath(root, WorkspacePath.ResolveUri(root,
                (x.TryGetProperty("targetUri", out var target) ? target : x.GetProperty("uri")).GetString()!)),
            range = (x.TryGetProperty("targetSelectionRange", out var range) ? range : x.GetProperty("range")).Clone()
        }).ToArray() };
    }
    private static async Task Drain(StreamReader reader)
    {
        var buffer = new char[4096];
        try { while (await reader.ReadAsync(buffer) != 0) { } } catch (IOException) { }
    }
}
