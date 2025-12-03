using Neo.Domain.Features.Cache;
using Neo.Domain.Features.Client.Dto;
using Neo.Infrastructure.Features.Client.Keycloak;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Neo.Infrastructure.Features.Client;
public class IdpClientCredentialService(HttpClient httpClient, ICacheService cacheService, ILogger<KeycloakIdpService> logger, IConfiguration configuration)
{
    public async Task<IdpClientCredentialResponseDtp?> GetClientCredentialToken(CancellationToken cancellationToken)
    {
        try
        {
            var cacheToken = await cacheService.GetAsync<IdpClientCredentialResponseDtp>("client-credential-admin-token", cancellationToken);
            if (cacheToken is null)
            {
                FormUrlEncodedContent content = new(
                [
                    new KeyValuePair<string, string>("client_id", configuration["IdpSetting:ClientId"]!),
                new KeyValuePair<string, string>("client_secret", configuration["IdpSetting:ClientSecret"]!),
                new KeyValuePair<string, string>("grant_type", "client_credentials")
                ]);
                content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
                HttpResponseMessage response = await httpClient.PostAsync(configuration["IdpSetting:TokenUri"], content, cancellationToken);
                _ = response.EnsureSuccessStatusCode();
                var result = await response.Content.ReadFromJsonAsync<IdpClientCredentialResponseDtp>(cancellationToken);
                await cacheService.SetAsync("client-credential-admin-token", result!, new Microsoft.Extensions.Caching.Distributed.DistributedCacheEntryOptions
                                        {
                                            AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(result!.expires_in)
                                        }, cancellationToken: cancellationToken);
                return result;
            }
            return cacheToken;
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("{@idp}", ex);
            return null;
        }
    }
}
