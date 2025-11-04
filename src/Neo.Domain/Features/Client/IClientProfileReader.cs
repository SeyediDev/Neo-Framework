namespace Neo.Domain.Features.Client;
public interface IClientProfileReader
{
    TProfileModel? GetProfile<TProfileModel>(string clientId, string profileKey);
}
