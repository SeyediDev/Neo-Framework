using Neo.Domain.Features.Client.Dto;

namespace Neo.Domain.Features.Client;
public interface IAdminIdpService
{
    Task<IdpClientCredentialResponseDtp?> GetAdminClientCredentialTokenAsync(CancellationToken cancellationToken);
}
