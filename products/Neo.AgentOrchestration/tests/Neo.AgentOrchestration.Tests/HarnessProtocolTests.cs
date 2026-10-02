using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Neo.AgentOrchestration.Infrastructure.Runs;
using Xunit;

namespace Neo.AgentOrchestration.Tests;

public sealed class HarnessProtocolTests
{
    [Fact]
    public void Legacy_fingerprint_stays_stable_while_v2_opt_in_changes_destination_identity()
    {
        var legacy = new HttpHarnessConnection("demo", new Uri("https://gateway.example/runs"),
            new Uri("https://api.example/"), "env:DISPATCH", "env:CALLBACK", false);
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            Key = legacy.Key, Endpoint = legacy.Endpoint, CallbackBaseUrl = legacy.CallbackBaseUrl,
            DispatchSecretRef = legacy.DispatchSecretRef, CallbackSecretRef = legacy.CallbackSecretRef
        }))));

        Assert.Equal(expected, legacy.Fingerprint);
        Assert.NotEqual(legacy.Fingerprint, (legacy with { CompactContextOptIn = true }).Fingerprint);
    }
}
