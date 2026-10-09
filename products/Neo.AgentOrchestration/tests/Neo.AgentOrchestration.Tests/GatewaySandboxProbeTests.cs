using System.Text.Json;
using Neo.AgentOrchestration.Application.ExternalAgents;
using Neo.AgentOrchestration.Infrastructure.ExternalExecution;
using Xunit;

namespace Neo.AgentOrchestration.Tests;

public sealed partial class SqlGatewayJournalTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Sandbox_cli_requires_opt_in_and_exact_argument_shape_without_reading_secrets(bool excess)
    {
        var args = excess ? new[] { "gateway-sandbox-health", "private-manifest-marker", "--allow-docker-inspection", "private-extra-marker" }
            : new[] { "gateway-sandbox-health", "private-manifest-marker" };
        var result = await GatewayCli("private-gateway-connection", "private-product-connection", args);
        Assert.Equal(2, result.Code); Assert.Contains("--allow-docker-inspection", result.Error);
        foreach (var marker in new[] { "private-manifest-marker", "private-extra-marker", "private-gateway-connection", "private-product-connection" })
            Assert.DoesNotContain(marker, result.Output + result.Error);
    }

    [Theory]
    [InlineData("malformed")] [InlineData("oversized")] [InlineData("unknown-field")]
    [InlineData("invalid-container")] [InlineData("remote-socket")] [InlineData("relative-executable")]
    public async Task Sandbox_cli_rejects_unsafe_operator_manifest_without_docker_sql_or_secret_echo(string fault)
    {
        var path = Path.Combine(Path.GetTempPath(), "fanasa-sandbox-probe-" + Guid.NewGuid().ToString("N") + ".json");
        var expected = new DockerSandboxExpectation(new ExternalAgentScope(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()),
            fault == "invalid-container" ? "--private-marker" : new string('a', 64), "engine", "28.5.1", new('A', 64), "pilot",
            "sha256:" + new string('a', 64), new('a', 40), "https://repository.invalid/project", 536870912, 1000000000, 128);
        var json = fault switch
        {
            "malformed" => "private-marker",
            "oversized" => new string('x', 65537),
            "unknown-field" => "{\"privateBearer\":\"private-marker\"}",
            _ => JsonSerializer.Serialize(new { executable = fault == "relative-executable" ? "../private-marker" : Path.Combine(Path.GetTempPath(), "missing-private-docker-marker"),
                socket = fault == "remote-socket" ? "tcp://private-marker:2375" : "unix:///run/user/1001/docker.sock",
                emptyDockerConfigDirectory = Path.GetTempPath(), expected }, new JsonSerializerOptions(JsonSerializerDefaults.Web))
        };
        try
        {
            await File.WriteAllTextAsync(path, json, Ct);
            var result = await GatewayCli("private-gateway-connection", "private-product-connection", "gateway-sandbox-health", path, "--allow-docker-inspection");
            Assert.Equal(2, result.Code); Assert.DoesNotContain("private-", result.Output + result.Error);
        }
        finally { File.Delete(path); }
    }
}
