using System.Text.Json;
using System.Text.RegularExpressions;
using Neo.AgentOrchestration.Contracts;

namespace Neo.AgentOrchestration.Infrastructure.ExternalExecution;

public sealed partial class FileAgentRuntimeStatus
{
    // Independent optional observation: invalid advisory health never changes the
    // existing three-engine v1 report. This is NOT an executable provider binding.
    private async Task<AgentRuntimeView> ReadAdvisoryAsync(string path, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(path)) return Unavailable("report-unavailable");
        try
        {
            for (var current = new FileInfo(path) as FileSystemInfo; current is not null; current = current switch
            { FileInfo f => f.Directory, DirectoryInfo d => d.Parent, _ => null })
                if ((current.Attributes & FileAttributes.ReparsePoint) != 0) return Unavailable("report-unavailable");
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete,
                4096, FileOptions.Asynchronous);
            if (file.Length is < 2 or > 8192) return Unavailable("report-invalid");
            using var buffer = new MemoryStream();
            var block = new byte[2048];
            for (int n; (n = await file.ReadAsync(block, ct)) != 0;)
            { if (buffer.Length + n > 8192) return Unavailable("report-invalid"); buffer.Write(block, 0, n); }
            using var doc = JsonDocument.Parse(buffer.ToArray(), new() { MaxDepth = 8 });
            Unique(doc.RootElement);
            var report = JsonSerializer.Deserialize<AdvisoryReport>(buffer.ToArray(), Json);
            if (report is null || report.Schema != "fanasa-agno-advisory/v1" || report.Mode != "advisory-only" ||
                report.ObservedAtUtc == default ||
                report.Version is not null && !Regex.IsMatch(report.Version, @"\A[0-9a-zA-Z][0-9a-zA-Z.+_-]{0,79}\z") ||
                report.Installed && report.Version is null || report.TransportHealthy && !report.Installed ||
                report.Reason is not ("advisory-healthy" or "advisory-unreachable" or "not-installed") ||
                report.TransportHealthy != (report.Reason == "advisory-healthy") ||
                report.Installed == (report.Reason == "not-installed")) return Unavailable("report-invalid");
            var now = clock.GetUtcNow();
            if (report.ObservedAtUtc > now.AddSeconds(15) || report.ObservedAtUtc < now.AddMinutes(-3))
                return Unavailable("report-stale");
            return new("agno", report.Version, report.Installed, report.TransportHealthy, false,
                report.Reason, report.ObservedAtUtc);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        { return Unavailable("report-unavailable"); }
        static AgentRuntimeView Unavailable(string reason) => new("agno", null, false, false, false, reason, null);
    }

    private sealed record AdvisoryReport(string Schema, DateTimeOffset ObservedAtUtc, string Mode,
        string? Version, bool Installed, bool TransportHealthy, string Reason);
}
