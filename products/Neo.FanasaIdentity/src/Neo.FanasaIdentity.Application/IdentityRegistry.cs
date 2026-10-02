using Neo.FanasaIdentity.Contracts;
using Neo.FanasaIdentity.Domain;

namespace Neo.FanasaIdentity.Application;

public interface IIdentityRegistry
{
    Task<IdentityRegistrationResponse> RegisterAsync(AuthenticatedHuman human, IdentityRegistrationRequest request, CancellationToken cancellationToken);
}

public sealed class IdentityRegistry(TimeProvider timeProvider) : IIdentityRegistry
{
    private readonly Dictionary<string, HumanIdentity> _identities = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public Task<IdentityRegistrationResponse> RegisterAsync(AuthenticatedHuman human, IdentityRegistrationRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            var key = $"{human.Provider}:{human.Subject}";
            if (!_identities.TryGetValue(key, out var identity))
            {
                identity = HumanIdentity.Register(human.Subject, human.Provider, timeProvider.GetUtcNow());
                _identities.Add(key, identity);
            }

            identity.SynchronizeProfile(request.Email ?? human.Email, request.DisplayName, request.TenantIds, request.Roles);
            return Task.FromResult(identity.ToResponse());
        }
    }
}
