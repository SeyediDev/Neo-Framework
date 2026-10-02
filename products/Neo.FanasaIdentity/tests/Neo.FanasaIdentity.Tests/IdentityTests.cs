using Neo.FanasaIdentity.Application;
using Neo.FanasaIdentity.Contracts;
using Xunit;

namespace Neo.FanasaIdentity.Tests;

public sealed class IdentityTests
{
    [Fact]
    public async Task Same_external_subject_is_idempotently_registered()
    {
        var registry = new IdentityRegistry(TimeProvider.System);
        var human = new AuthenticatedHuman("subject-1", "alice@fanasa.test", "Alice", "fanasa", ["tenant-a"], ["operator"]);
        var request = new IdentityRegistrationRequest("Alice", "alice@fanasa.test", ["tenant-a"], ["operator"]);

        var first = await registry.RegisterAsync(human, request, CancellationToken.None);
        var second = await registry.RegisterAsync(human, request, CancellationToken.None);

        Assert.Equal(first.Subject, second.Subject);
        Assert.Equal(first.RegisteredAt, second.RegisteredAt);
    }
}
