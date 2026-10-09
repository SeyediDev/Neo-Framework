using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Neo.AgentOrchestration.Infrastructure.ExternalExecution;

namespace Neo.AgentOrchestration.Provisioning;

// Explicit operator-only read: no create/start/exec/stop/remove, SQL, model call
// or credentials copied into an executor. Not registered in API/MCP/Worker.
public static class SandboxProbeCommand
{
    private sealed record Configuration(string Executable, string Socket, string EmptyDockerConfigDirectory, DockerSandboxExpectation Expected);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true, AllowDuplicateProperties = false
    };
    public static async Task<int> Run(string[] args, TextWriter output, TextWriter error, CancellationToken ct)
    {
        if (args is not ["gateway-sandbox-health", _, "--allow-docker-inspection"])
        { await error.WriteLineAsync("neo-agent gateway-sandbox-health <private-manifest.json> --allow-docker-inspection"); return 2; }
        try
        {
            ct.ThrowIfCancellationRequested();
            await using var file = File.OpenRead(args[1]);
            if (file.Length > 65536) throw new ArgumentException();
            var config = await JsonSerializer.DeserializeAsync<Configuration>(file, Json, ct) ?? throw new ArgumentException();
            config.Expected.Validate();
            // Local rootless socket only: no remote/TCP/TLS downgrade or ambient
            // Docker context/SSH/registry credentials. Operator supplies a trusted
            // installed Docker executable and an existing empty configuration dir.
            if (!Regex.IsMatch(config.Socket, @"\Aunix:///run/user/[1-9][0-9]{3,8}/docker\.sock\z", RegexOptions.CultureInvariant) ||
                !Path.IsPathFullyQualified(config.Executable) || !File.Exists(config.Executable) ||
                !Path.IsPathFullyQualified(config.EmptyDockerConfigDirectory) || !Directory.Exists(config.EmptyDockerConfigDirectory) ||
                Directory.EnumerateFileSystemEntries(config.EmptyDockerConfigDirectory).Any()) throw new ArgumentException();
            RequireNoLinks(new FileInfo(config.Executable)); RequireNoLinks(new DirectoryInfo(config.EmptyDockerConfigDirectory));
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(15));
            var info = await ReadDocker(config, ["info", "--format", "{{json .}}"], deadline.Token);
            var container = await ReadDocker(config, ["container", "inspect", "--", config.Expected.ContainerId], deadline.Token);
            var result = DockerSandboxPolicy.Inspect(config.Expected, info, container);
            await output.WriteLineAsync(JsonSerializer.Serialize(result, Json));
            return result.PolicyCompliant ? 0 : 3;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        { await error.WriteLineAsync("Sandbox inspection cancelled; no container lifecycle changes made."); return 130; }
        catch (Exception ex) when (ex is JsonException || ex is ArgumentException && ex is not DecoderFallbackException)
        { await error.WriteLineAsync("Invalid sandbox probe configuration. No container lifecycle changes made."); return 2; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(new DockerSandboxHealth(false, false, null, ["docker-probe-unavailable"], DateTimeOffset.UtcNow), Json));
            return 1;
        }
    }
    private static void RequireNoLinks(FileSystemInfo entry)
    {
        for (FileSystemInfo? item = entry; item is not null; item = item is DirectoryInfo directory ? directory.Parent : ((FileInfo)item).Directory)
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0 || item.LinkTarget is not null) throw new ArgumentException();
    }
    private static async Task<string> ReadDocker(Configuration config, string[] arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo(config.Executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = config.EmptyDockerConfigDirectory
        };
        start.Environment.Clear();
        foreach (var name in new[] { "PATH", "SystemRoot", "WINDIR", "TEMP", "TMP" })
            if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        start.Environment["DOCKER_HOST"] = config.Socket;
        start.Environment["DOCKER_CONFIG"] = config.EmptyDockerConfigDirectory;
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException();
        process.StandardInput.Close();
        try
        {
            var stdout = ReadBounded(process.StandardOutput.BaseStream, DockerSandboxPolicy.MaxSnapshotBytes, ct);
            var stderr = ReadBounded(process.StandardError.BaseStream, 16384, ct);
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(ct));
            if (process.ExitCode != 0) throw new InvalidOperationException();
            return new UTF8Encoding(false, true).GetString(await stdout);
        }
        finally
        {
            // Only this short-lived read-only Docker CLIENT is terminated. Never
            // issue docker stop/kill against an executor or shared daemon.
            if (!process.HasExited)
            { try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } }
        }
    }
    private static async Task<byte[]> ReadBounded(Stream stream, int limit, CancellationToken ct)
    {
        using var buffer = new MemoryStream(); var chunk = new byte[8192];
        for (int read; (read = await stream.ReadAsync(chunk, ct)) != 0;)
        { if (buffer.Length + read > limit) throw new IOException(); buffer.Write(chunk, 0, read); }
        return buffer.ToArray();
    }
}
