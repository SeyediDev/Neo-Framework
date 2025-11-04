using Neo.Domain.Features.Client;
using Microsoft.Extensions.Configuration;

namespace Neo.Infrastructure.Features.Client;

public class ClientProfileReaderFromConfig(IConfiguration configuration) : IClientProfileReader
{
    public TProfileModel? GetProfile<TProfileModel>(string clientId, string profileKey)
    {
        var profile = configuration.GetSection($"ClientProfileSettings:{clientId?.ToUpper() ?? "DIGIPAY"}:{profileKey}").Get<TProfileModel>();
        return profile;
    }
}