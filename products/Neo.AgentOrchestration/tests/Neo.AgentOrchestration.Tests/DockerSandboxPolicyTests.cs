using System.Text.Json.Nodes;
using Neo.AgentOrchestration.Application.ExternalAgents;
using Neo.AgentOrchestration.Infrastructure.ExternalExecution;
using Xunit;

namespace Neo.AgentOrchestration.Tests;

public sealed class DockerSandboxPolicyTests
{
    [Theory]
    [InlineData(false, 0)] [InlineData(true, 0)] [InlineData(true, 1)]
    public void Scoped_offline_executor_inspection_distinguishes_running_exit_and_task_acceptance(bool exited, int code)
    {
        var f = Fixture(exited, code); var result = DockerSandboxPolicy.Inspect(f.Expected, f.Info.ToJsonString(), f.Inspect.ToJsonString());
        Assert.True(result.PolicyCompliant); Assert.Equal(exited, result.ExecutorExited); Assert.Equal(exited ? (int?)code : null, result.ExitCode);
        Assert.Contains(exited ? "executor-exit-observed-not-task-acceptance" : "executor-running-not-exited", result.Reasons);
    }

    [Theory]
    [InlineData("rootless")] [InlineData("seccomp")] [InlineData("cgroup")] [InlineData("limits-supported")]
    [InlineData("engine")] [InlineData("image")] [InlineData("container")] [InlineData("scope")]
    [InlineData("binding")] [InlineData("root")] [InlineData("named-user")] [InlineData("privileged")]
    [InlineData("read-write-root")] [InlineData("autoremove")] [InlineData("restart")] [InlineData("cap-add")]
    [InlineData("seccomp-disabled")] [InlineData("pid-host")] [InlineData("ipc-host")] [InlineData("cgroup-host")]
    [InlineData("unlimited-memory")] [InlineData("unlimited-swap")] [InlineData("unlimited-cpu")] [InlineData("unlimited-pids")]
    [InlineData("memory-over-budget")] [InlineData("cpu-over-budget")] [InlineData("pids-over-budget")]
    [InlineData("network")] [InlineData("port")] [InlineData("bind-socket")] [InlineData("extra-volume")]
    [InlineData("other-volume")] [InlineData("secret-env")] [InlineData("shared-home")]
    [InlineData("duplicate-env")] [InlineData("tmpfs")] [InlineData("paused")] [InlineData("restarting")]
    [InlineData("oom")] [InlineData("exit-pid")] [InlineData("unfinished")] [InlineData("future-exit")]
    [InlineData("missing-running")] [InlineData("missing-root-flag")]
    public void Unsafe_or_unknown_executor_inspection_cannot_be_accepted_or_release_capacity(string fault)
    {
        var f = Fixture(true, 0); var c = f.Inspect[0]!; var config = c["Config"]!; var host = c["HostConfig"]!; var state = c["State"]!;
        switch (fault)
        {
            case "rootless": f.Info["SecurityOptions"] = new JsonArray("name=seccomp,profile=builtin"); break;
            case "seccomp": f.Info["SecurityOptions"] = new JsonArray("name=rootless"); break;
            case "cgroup": f.Info["CgroupVersion"] = "1"; break;
            case "limits-supported": f.Info["MemoryLimit"] = false; break;
            case "engine": f.Info["ID"] = "other"; break;
            case "image": c["Image"] = "sha256:" + new string('b', 64); break;
            case "container": c["Id"] = new string('b', 64); break;
            case "scope": config["Labels"]!["fanasa.project-id"] = Guid.NewGuid().ToString("D"); break;
            case "binding": config["Labels"]!["fanasa.binding-fingerprint"] = new string('B', 64); break;
            case "root": config["User"] = "0:0"; break;
            case "named-user": config["User"] = "agent"; break;
            case "privileged": host["Privileged"] = true; break;
            case "read-write-root": host["ReadonlyRootfs"] = false; break;
            case "autoremove": host["AutoRemove"] = true; break;
            case "restart": host["RestartPolicy"]!["Name"] = "always"; break;
            case "cap-add": host["CapAdd"] = new JsonArray("SYS_ADMIN"); break;
            case "seccomp-disabled": host["SecurityOpt"] = new JsonArray("no-new-privileges", "seccomp=unconfined"); break;
            case "pid-host": host["PidMode"] = "host"; break;
            case "ipc-host": host["IpcMode"] = "host"; break;
            case "cgroup-host": host["CgroupnsMode"] = "host"; break;
            case "unlimited-memory": host["Memory"] = 0; break;
            case "unlimited-swap": host["MemorySwap"] = -1; break;
            case "unlimited-cpu": host["NanoCpus"] = 0; break;
            case "unlimited-pids": host["PidsLimit"] = -1; break;
            case "memory-over-budget": host["Memory"] = 2147483648L; host["MemorySwap"] = 2147483648L; break;
            case "cpu-over-budget": host["NanoCpus"] = 2000000000L; break;
            case "pids-over-budget": host["PidsLimit"] = 1000; break;
            case "network": host["NetworkMode"] = "host"; break;
            case "port": host["PortBindings"] = new JsonObject { ["80/tcp"] = new JsonArray(new JsonObject { ["HostPort"] = "80" }) }; break;
            case "bind-socket": c["Mounts"]![0]!["Type"] = "bind"; c["Mounts"]![0]!["Destination"] = "/var/run/docker.sock"; break;
            case "extra-volume": ((JsonArray)c["Mounts"]!).Add(new JsonObject { ["Destination"] = "/private" }); break;
            case "other-volume": c["Mounts"]![0]!["Name"] = "other-tenant"; break;
            case "secret-env": ((JsonArray)config["Env"]!).Add("FANASA_MODEL_KEY=private-secret-marker"); break;
            case "shared-home": config["Env"]![1] = "HOME=/root"; break;
            case "duplicate-env": ((JsonArray)config["Env"]!).Add("PATH=/private"); break;
            case "tmpfs": host["Tmpfs"]!["/tmp"] = "rw,size=4g"; break;
            case "paused": state["Paused"] = true; break;
            case "restarting": state["Restarting"] = true; break;
            case "oom": state["OOMKilled"] = true; break;
            case "exit-pid": state["Pid"] = 20; break;
            case "unfinished": state["FinishedAt"] = "0001-01-01T00:00:00Z"; break;
            case "future-exit": state["FinishedAt"] = DateTimeOffset.UtcNow.AddDays(1).ToString("O"); break;
            case "missing-running": ((JsonObject)state).Remove("Running"); break;
            case "missing-root-flag": ((JsonObject)host).Remove("ReadonlyRootfs"); break;
        }
        var result = DockerSandboxPolicy.Inspect(f.Expected, f.Info.ToJsonString(), f.Inspect.ToJsonString());
        Assert.False(result.PolicyCompliant); Assert.False(result.ExecutorExited); Assert.Null(result.ExitCode); Assert.NotEmpty(result.Reasons);
        Assert.DoesNotContain("private-secret-marker", System.Text.Json.JsonSerializer.Serialize(result));
    }

    [Fact]
    public void Malformed_duplicate_oversized_or_ambiguous_snapshots_fail_closed_without_echo()
    {
        var f = Fixture(); var info = f.Info.ToJsonString();
        foreach (var json in new[] { "private-error", "[]", "{}", "[{},{}]", "[{\"Id\":\"private\",\"Id\":\"other\"}]", new string('x', DockerSandboxPolicy.MaxSnapshotBytes + 1) })
        {
            var result = DockerSandboxPolicy.Inspect(f.Expected, info, json);
            Assert.False(result.PolicyCompliant); Assert.False(result.ExecutorExited); Assert.DoesNotContain("private", string.Join(',', result.Reasons));
        }
        Assert.False(DockerSandboxPolicy.Inspect(f.Expected, "{\"ID\":\"a\",\"ID\":\"b\"}", f.Inspect.ToJsonString()).PolicyCompliant);
    }

    [Fact]
    public void Docker_nanosecond_timestamps_are_culture_independent_and_local_date_strings_are_not_exit_evidence()
    {
        var f = Fixture(true); var state = f.Inspect[0]!["State"]!;
        state["StartedAt"] = DateTimeOffset.UtcNow.AddMinutes(-2).ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + ".123456789Z";
        state["FinishedAt"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("yyyy-MM-dd'T'HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture) + ".987654321Z";
        var culture = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new("fa-IR");
            var result = DockerSandboxPolicy.Inspect(f.Expected, f.Info.ToJsonString(), f.Inspect.ToJsonString());
            Assert.True(result.PolicyCompliant); Assert.True(result.ExecutorExited); Assert.Equal(TimeSpan.Zero, result.ObservedAtUtc.Offset);
            state["FinishedAt"] = "09/10/2026 13:00";
            Assert.False(DockerSandboxPolicy.Inspect(f.Expected, f.Info.ToJsonString(), f.Inspect.ToJsonString()).ExecutorExited);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = culture; }
    }

    [Fact]
    public void Invalid_operator_expectations_are_rejected_not_interpreted_as_task_authority()
    {
        var f = Fixture();
        foreach (var expected in new[] { f.Expected with { ContainerId = "--privileged" }, f.Expected with { ImageId = "latest" },
            f.Expected with { RepositoryUrl = "https://user:private@repository.invalid/" }, f.Expected with { MaxNanoCpus = 0 },
            f.Expected with { Scope = f.Expected.Scope with { ProjectId = Guid.Empty } } })
            Assert.Throws<ArgumentException>(() => DockerSandboxPolicy.Inspect(expected, "{}", "[]"));
    }

    private static (DockerSandboxExpectation Expected, JsonObject Info, JsonArray Inspect) Fixture(bool exited = false, int code = 0)
    {
        var scope = new ExternalAgentScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var e = new DockerSandboxExpectation(scope, new('a', 64), "rootless-engine", "28.5.1", new('A', 64), "pilot-lane",
            "sha256:" + new string('a', 64), new('a', 40), "https://repository.invalid/project", 536870912, 1000000000, 128);
        var labels = new JsonObject { ["fanasa.kind"] = "executor-v1", ["fanasa.organization-id"] = scope.OrganizationId.ToString("D"),
            ["fanasa.workspace-id"] = scope.WorkspaceId.ToString("D"), ["fanasa.project-id"] = scope.ProjectId.ToString("D"), ["fanasa.run-id"] = scope.RunId.ToString("D"),
            ["fanasa.binding-fingerprint"] = e.BindingFingerprint, ["fanasa.sandbox-id"] = e.SandboxId, ["fanasa.revision"] = e.Revision, ["fanasa.repository"] = e.RepositoryUrl };
        var info = new JsonObject { ["ID"] = e.EngineId, ["ServerVersion"] = e.EngineVersion, ["OSType"] = "linux", ["CgroupVersion"] = "2", ["CgroupDriver"] = "systemd",
            ["SecurityOptions"] = new JsonArray("name=rootless", "name=seccomp,profile=builtin"), ["MemoryLimit"] = true, ["SwapLimit"] = true, ["PidsLimit"] = true, ["CpuCfsQuota"] = true };
        var container = new JsonObject { ["Id"] = e.ContainerId, ["Image"] = e.ImageId,
            ["Config"] = new JsonObject { ["User"] = "10001:10001", ["WorkingDir"] = "/workspace", ["NetworkDisabled"] = true, ["Labels"] = labels,
                ["Env"] = new JsonArray("PATH=/usr/local/bin:/usr/bin:/bin", "HOME=/workspace/.home") },
            ["HostConfig"] = new JsonObject { ["Privileged"] = false, ["ReadonlyRootfs"] = true, ["AutoRemove"] = false, ["Init"] = true, ["OomKillDisable"] = false,
                ["CapDrop"] = new JsonArray("ALL"), ["SecurityOpt"] = new JsonArray("no-new-privileges"), ["PidMode"] = "", ["UsernsMode"] = "", ["IpcMode"] = "private", ["CgroupnsMode"] = "private", ["Runtime"] = "runc",
                ["RestartPolicy"] = new JsonObject { ["Name"] = "no", ["MaximumRetryCount"] = 0 }, ["Memory"] = 536870912, ["MemorySwap"] = 536870912,
                ["NanoCpus"] = 1000000000, ["PidsLimit"] = 128, ["NetworkMode"] = "none", ["Tmpfs"] = new JsonObject { ["/tmp"] = "rw,noexec,nosuid,nodev,size=64m" }, ["ShmSize"] = 67108864 },
            ["Mounts"] = new JsonArray(new JsonObject { ["Type"] = "volume", ["Name"] = e.WorkspaceVolume, ["Destination"] = "/workspace", ["RW"] = true, ["Propagation"] = "" }),
            ["State"] = new JsonObject { ["Status"] = exited ? "exited" : "running", ["Running"] = !exited, ["Pid"] = exited ? 0 : 10, ["ExitCode"] = code,
                ["StartedAt"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToString("O"), ["FinishedAt"] = exited ? DateTimeOffset.UtcNow.AddSeconds(-1).ToString("O") : "0001-01-01T00:00:00Z",
                ["Paused"] = false, ["Restarting"] = false, ["Dead"] = false, ["OOMKilled"] = false, ["Error"] = "" } };
        return (e, info, new JsonArray(container));
    }
}
