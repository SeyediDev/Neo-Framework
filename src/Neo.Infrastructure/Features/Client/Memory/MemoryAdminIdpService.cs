using Neo.Domain.Features.Client;
using Neo.Domain.Features.Client.Dto;

namespace Neo.Infrastructure.Features.Client.Memory;

public sealed class MemoryAdminIdpService
    : IAdminIdpService
{
    public Task<IdpClientCredentialResponseDtp?> GetAdminClientCredentialTokenAsync(CancellationToken cancellationToken)
    {
        throw new NotImplementedException();
    }
}

