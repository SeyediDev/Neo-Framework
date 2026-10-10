using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Neo.AgentOrchestration.Application.ExternalExecution;
using Neo.AgentOrchestration.Contracts;
using Neo.AgentOrchestration.Domain.Projects;

namespace Neo.AgentOrchestration.Infrastructure.ExternalExecution;

// Read-only control-plane projection from a private operator-owned collector.
// No HTTP discovery, shell, Docker socket, model call or start/stop in API/Web.
// Source path and scope are operator configuration, never request/task prose.
public sealed class FileAgentRuntimeStatus(IConfiguration configuration, TimeProvider clock) : IAgentRuntimeStatus
{
    private static readonly string[] Engines = ["hermes", "opencode", "codex"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 8 };

    public async Task<AgentRuntimeCatalog> ReadAsync(WorkspaceScope scope, Guid projectId, CancellationToken ct)
    {
        if (projectId == Guid.Empty) throw new ArgumentException("Project is required.");
        var bindings = configuration.GetSection("AgentRuntimeStatus:Bindings").GetChildren().Where(c =>
            Guid.TryParse(c["OrganizationId"], out var organization) && organization == scope.OrganizationId &&
            Guid.TryParse(c["WorkspaceId"], out var workspace) && workspace == scope.WorkspaceId &&
            Guid.TryParse(c["ProjectId"], out var project) && project == projectId).ToArray();
        if (bindings.Length != 1) return Empty("not-configured");
        var path = bindings[0]["StatusFile"];
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return Empty("report-unavailable");
        try
        {
            // Refuse links through any component, including a linked parent.
            for (var current = new FileInfo(path) as FileSystemInfo; current is not null; current = current switch
            { FileInfo f => f.Directory, DirectoryInfo d => d.Parent, _ => null })
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0) return Empty("report-unavailable");
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                4096, FileOptions.Asynchronous);
            if (file.Length is < 2 or > 32768) return Empty("report-invalid");
            using var buffer = new MemoryStream();
            var block = new byte[4096];
            for (int n; (n = await file.ReadAsync(block, ct)) != 0;)
            { if (buffer.Length + n > 32768) return Empty("report-invalid"); buffer.Write(block, 0, n); }
            using var doc = JsonDocument.Parse(buffer.ToArray(), new() { MaxDepth = 8 });
            Unique(doc.RootElement);
            var report = JsonSerializer.Deserialize<Report>(buffer.ToArray(), Json);
            if (report is null || report.Schema != "fanasa-agent-runtime/v1" || report.Runtimes is null ||
                report.Runtimes.Length != 3 || report.Runtimes.Any(x => x is null) ||
                !report.Runtimes.Select(x => x.Engine).Order().SequenceEqual(Engines.Order())) return Empty("report-invalid");
            var now = clock.GetUtcNow();
            if (report.ObservedAtUtc > now.AddSeconds(15) || report.ObservedAtUtc < now.AddMinutes(-3))
                return Empty("report-stale");
            foreach (var r in report.Runtimes)
                if ((r.Version is not null && !Regex.IsMatch(r.Version, @"\A[0-9a-zA-Z][0-9a-zA-Z.+_-]{0,79}\z")) ||
                    r.Installed && r.Version is null || r.TransportHealthy && !r.Installed ||
                    r.Reason is not ("native-healthy" or "native-unreachable" or "not-installed" or "cli-installed"))
                    return Empty("report-invalid");
            return new(projectId, report.Runtimes.Select(r => new AgentRuntimeView(r.Engine, r.Version,
                r.Installed, r.TransportHealthy, false, r.Installed && r.TransportHealthy ? "execution-gates-pending" : r.Reason,
                report.ObservedAtUtc)).ToArray());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        { return Empty("report-unavailable"); }
        AgentRuntimeCatalog Empty(string reason) => new(projectId,
            Engines.Select(e => new AgentRuntimeView(e, null, false, false, false, reason, null)).ToArray());
    }
    private static void Unique(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in element.EnumerateObject())
            { if (!names.Add(p.Name)) throw new JsonException(); Unique(p.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var c in element.EnumerateArray()) Unique(c);
    }
    private sealed record Report(string Schema, DateTimeOffset ObservedAtUtc, Runtime[] Runtimes);
    private sealed record Runtime(string Engine, string? Version, bool Installed, bool TransportHealthy, string Reason);
}
