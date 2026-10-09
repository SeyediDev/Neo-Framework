using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Globalization;
using Neo.AgentOrchestration.Application.ExternalAgents;

namespace Neo.AgentOrchestration.Infrastructure.ExternalExecution;

// Operator expectations, never task/model text. This is a read-only inspection
// policy for an offline executor, NOT IGatewaySandbox or authority to release a
// journal lease. A trusted lifecycle/evidence collector remains a separate gate.
public sealed record DockerSandboxExpectation(ExternalAgentScope Scope, string ContainerId,
    string EngineId, string EngineVersion, string BindingFingerprint, string SandboxId,
    string ImageId, string Revision, string RepositoryUrl, long MaxMemoryBytes,
    long MaxNanoCpus, int MaxPids)
{
    [JsonIgnore]
    public string WorkspaceVolume => $"fanasa-executor-{Scope.OrganizationId:N}-{Scope.WorkspaceId:N}-{Scope.ProjectId:N}-{Scope.RunId:N}";
    public void Validate()
    {
        if (new[] { Scope.OrganizationId, Scope.WorkspaceId, Scope.ProjectId, Scope.RunId }.Any(x => x == Guid.Empty) ||
            !Match(ContainerId, "[0-9a-f]{64}") || !Match(ImageId, "sha256:[0-9a-f]{64}") ||
            !Match(BindingFingerprint, "[0-9A-F]{64}") || !Match(SandboxId, "[a-z0-9][a-z0-9_-]{0,99}") ||
            !Match(Revision, "[0-9a-fA-F]{40,64}") || string.IsNullOrWhiteSpace(EngineId) || EngineId.Length > 200 || EngineId.Any(char.IsControl) ||
            !Match(EngineVersion, "[0-9]+\\.[0-9]+\\.[0-9]+(?:[-+][A-Za-z0-9.-]+)?") ||
            !Uri.TryCreate(RepositoryUrl, UriKind.Absolute, out var repo) || repo.Scheme != "https" ||
            repo.UserInfo.Length != 0 || repo.Query.Length != 0 || repo.Fragment.Length != 0 ||
            MaxMemoryBytes is < 67108864 or > 2147483648 || MaxNanoCpus is < 100000000 or > 2000000000 || MaxPids is < 1 or > 256)
            throw new ArgumentException("Invalid sandbox expectations.");
    }
    private static bool Match(string value, string pattern) => Regex.IsMatch(value, "\\A" + pattern + "\\z", RegexOptions.CultureInvariant);
}

public sealed record DockerSandboxHealth(bool PolicyCompliant, bool ExecutorExited, int? ExitCode, IReadOnlyList<string> Reasons,
    DateTimeOffset ObservedAtUtc);

public static class DockerSandboxPolicy
{
    public const int MaxSnapshotBytes = 1024 * 1024;
    public static DockerSandboxHealth Inspect(DockerSandboxExpectation expected, string infoJson, string inspectJson)
    {
        expected.Validate();
        try
        {
            if (Encoding.UTF8.GetByteCount(infoJson) > MaxSnapshotBytes || Encoding.UTF8.GetByteCount(inspectJson) > MaxSnapshotBytes)
                return Bad("snapshot-limit");
            using var infoDoc = JsonDocument.Parse(infoJson, new() { MaxDepth = 32 });
            using var inspectDoc = JsonDocument.Parse(inspectJson, new() { MaxDepth = 32 });
            Unique(infoDoc.RootElement); Unique(inspectDoc.RootElement);
            var info = infoDoc.RootElement; var list = inspectDoc.RootElement;
            if (info.ValueKind != JsonValueKind.Object || list.ValueKind != JsonValueKind.Array || list.GetArrayLength() != 1)
                return Bad("snapshot-shape");
            var c = list[0]; var config = Property(c, "Config"); var host = Property(c, "HostConfig"); var state = Property(c, "State");
            var reasons = new List<string>();
            void Require(bool condition, string reason) { if (!condition) reasons.Add(reason); }
            Require(Text(info, "ID") == expected.EngineId && Text(info, "ServerVersion") == expected.EngineVersion && Text(info, "OSType") == "linux", "engine-identity");
            var features = Strings(Property(info, "SecurityOptions"));
            Require(features is not null && features.Contains("name=rootless") && features.Contains("name=seccomp,profile=builtin"), "rootless-seccomp-required");
            Require(Text(info, "CgroupVersion") == "2" && Text(info, "CgroupDriver") == "systemd" &&
                new[] { "MemoryLimit", "SwapLimit", "PidsLimit", "CpuCfsQuota" }.All(k => Bool(info, k) == true), "resource-enforcement-unverified");
            Require(Text(c, "Id") == expected.ContainerId && Text(c, "Image") == expected.ImageId, "container-image-mismatch");
            var labels = Property(config, "Labels");
            foreach (var (key, value) in new Dictionary<string, string>
            {
                ["kind"] = "executor-v1", ["organization-id"] = expected.Scope.OrganizationId.ToString("D"),
                ["workspace-id"] = expected.Scope.WorkspaceId.ToString("D"), ["project-id"] = expected.Scope.ProjectId.ToString("D"),
                ["run-id"] = expected.Scope.RunId.ToString("D"), ["binding-fingerprint"] = expected.BindingFingerprint,
                ["sandbox-id"] = expected.SandboxId, ["revision"] = expected.Revision, ["repository"] = expected.RepositoryUrl
            }) Require(Text(labels, "fanasa." + key) == value, "scope-binding-mismatch");
            Require(Text(config, "User") is { } user && Regex.IsMatch(user, @"\A[1-9][0-9]{3,8}:[1-9][0-9]{3,8}\z", RegexOptions.CultureInvariant), "numeric-non-root-user-required");
            Require(Text(config, "WorkingDir") == "/workspace" && SafeEnvironment(Property(config, "Env")), "executor-environment-unreviewed");
            Require(Bool(host, "Privileged") == false && Bool(host, "ReadonlyRootfs") == true && Bool(host, "AutoRemove") == false &&
                Bool(host, "Init") == true && Bool(host, "OomKillDisable") == false, "executor-lifecycle-policy");
            Require(Strings(Property(host, "CapDrop")) is ["ALL"] && Empty(Property(host, "CapAdd")) &&
                Strings(Property(host, "SecurityOpt")) is ["no-new-privileges"] or ["no-new-privileges:true"], "capability-security-policy");
            Require(Text(host, "PidMode") == "" && Text(host, "UsernsMode") == "" && Text(host, "IpcMode") == "private" &&
                Text(host, "CgroupnsMode") == "private" && Text(host, "Runtime") == "runc", "namespace-runtime-policy");
            Require(Text(Property(host, "RestartPolicy"), "Name") == "no" && Number(Property(host, "RestartPolicy"), "MaximumRetryCount") == 0, "restart-must-be-disabled");
            var memory = Number(host, "Memory"); var cpu = Number(host, "NanoCpus"); var pids = Number(host, "PidsLimit");
            Require(memory is > 0 && memory <= expected.MaxMemoryBytes && Number(host, "MemorySwap") == memory &&
                cpu is > 0 && cpu <= expected.MaxNanoCpus && pids is > 0 && pids <= expected.MaxPids, "resource-bounds");
            Require(Bool(config, "NetworkDisabled") == true && Text(host, "NetworkMode") == "none" && Empty(Property(host, "PortBindings")) &&
                Empty(Property(host, "Links")) && Empty(Property(host, "ExtraHosts")), "offline-network-required");
            Require(new[] { "Binds", "VolumesFrom", "Devices", "DeviceRequests", "DeviceCgroupRules", "GroupAdd", "Sysctls" }.All(k => Empty(Property(host, k))), "host-access-forbidden");
            var mounts = Property(c, "Mounts");
            Require(mounts.ValueKind == JsonValueKind.Array && mounts.GetArrayLength() == 1 &&
                Text(mounts[0], "Type") == "volume" && Text(mounts[0], "Name") == expected.WorkspaceVolume &&
                Text(mounts[0], "Destination") == "/workspace" && Bool(mounts[0], "RW") == true &&
                Text(mounts[0], "Propagation") is "" or "rprivate", "scoped-workspace-volume-required");
            var tmpfs = Property(host, "Tmpfs");
            Require(tmpfs.ValueKind == JsonValueKind.Object && tmpfs.EnumerateObject().Count() == 1 &&
                Text(tmpfs, "/tmp") == "rw,noexec,nosuid,nodev,size=64m" && Number(host, "ShmSize") is > 0 and <= 67108864, "bounded-temp-required");
            var observedAt = DateTimeOffset.UtcNow;
            var validStart = Timestamp(Text(state, "StartedAt"), out var started) && started.Year >= 2020 && started <= observedAt;
            var running = validStart && Text(state, "Status") == "running" && Bool(state, "Running") == true && Number(state, "Pid") is > 0;
            var exited = Text(state, "Status") == "exited" && Bool(state, "Running") == false && Number(state, "Pid") == 0 &&
                Number(state, "ExitCode") is >= 0 and <= 255 && validStart &&
                Timestamp(Text(state, "FinishedAt"), out var finished) && finished >= started && finished.Year >= 2020 && finished <= observedAt;
            Require((running || exited) && Bool(state, "Paused") == false && Bool(state, "Restarting") == false &&
                Bool(state, "Dead") == false && Bool(state, "OOMKilled") == false && Text(state, "Error") == "", "executor-state-unsettled");
            if (reasons.Count != 0) return new(false, false, null, reasons.Distinct(StringComparer.Ordinal).ToArray(), observedAt);
            return new(true, exited, exited ? (int)Number(state, "ExitCode")!.Value : null,
                [exited ? "executor-exit-observed-not-task-acceptance" : "executor-running-not-exited"], observedAt);
        }
        catch (JsonException) { return Bad("snapshot-invalid"); }
    }
    private static bool SafeEnvironment(JsonElement env)
    {
        var entries = Strings(env); if (entries is null || entries.Length > 32) return false;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var split = entry.IndexOf('='); if (split < 1 || entry.Length > 4096 || entry.Any(char.IsControl)) return false;
            var name = entry[..split]; var value = entry[(split + 1)..];
            if (!names.Add(name) || name is not ("PATH" or "HOME" or "LANG" or "LC_ALL" or "TZ" or "DOTNET_CLI_TELEMETRY_OPTOUT" or "DOTNET_NOLOGO")) return false;
            if (name == "HOME" && value != "/workspace/.home") return false;
        }
        return names.Contains("HOME") && names.Contains("PATH");
    }
    private static DockerSandboxHealth Bad(string reason) => new(false, false, null, [reason], DateTimeOffset.UtcNow);
    private static bool Timestamp(string? value, out DateTimeOffset result)
    {
        result = default;
        return value is not null && Regex.IsMatch(value, @"\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,9})?(?:Z|[+-][0-9]{2}:[0-9]{2})\z", RegexOptions.CultureInvariant) &&
            DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out result);
    }
    private static JsonElement Property(JsonElement parent, string key) => parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(key, out var v) ? v : default;
    private static string? Text(JsonElement parent, string key) => Property(parent, key) is var v && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static bool? Bool(JsonElement parent, string key) => Property(parent, key).ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
    private static long? Number(JsonElement parent, string key) => Property(parent, key) is var v && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;
    private static string[]? Strings(JsonElement list) => list.ValueKind == JsonValueKind.Array && list.EnumerateArray().All(x => x.ValueKind == JsonValueKind.String)
        ? list.EnumerateArray().Select(x => x.GetString()!).ToArray() : null;
    private static bool Empty(JsonElement value) => value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ||
        value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0 || value.ValueKind == JsonValueKind.Object && !value.EnumerateObject().Any();
    private static void Unique(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            { if (!names.Add(property.Name)) throw new JsonException(); Unique(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var child in element.EnumerateArray()) Unique(child);
    }
}
