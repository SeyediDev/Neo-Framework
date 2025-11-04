using Neo.Domain.Features.Client;
using Neo.Domain.Features.Client.Dto;
using Neo.Infrastructure.Features.Client.Keycloak;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace Neo.Infrastructure.Features.Client;

public class AdminIdpService(HttpClient httpClient, ILogger<IdpService> logger, IConfiguration configuration) : IAdminIdpService
{
    private string AdminBaseUri => configuration["IdpSetting:Admin:BaseUri"]!;
    private string ClientId => configuration["IdpSetting:Admin:ClientId"]!;
    private string ClientSecret => configuration["IdpSetting:Admin:ClientSecret"]!;
    public async Task<IdpClientCredentialResponseDtp?> GetAdminClientCredentialTokenAsync(CancellationToken cancellationToken)
    {
        try
        {
            var content = new FormUrlEncodedContent(
            [
                new KeyValuePair<string, string>("client_id", ClientId!),
                new KeyValuePair<string, string>("client_secret", ClientSecret),
                new KeyValuePair<string, string>("grant_type", "client_credentials")
            ]);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/x-www-form-urlencoded");
            HttpResponseMessage response = await httpClient.PostAsync(AdminBaseUri, content, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<IdpClientCredentialResponseDtp>(cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("{@idp}", ex);
            return null;
        }
    }
}
