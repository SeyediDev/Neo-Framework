using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fanasa.UnifiedPortal.Web;

public sealed record PlatformProductView(Guid Id, string Key, string DisplayName, string Audience, string Center, string? Url, string? Repository, Guid? TenantId, string Owner, Dictionary<string, string>? Attributes);

public interface IPlatformCatalogClient
{
    Task<IReadOnlyCollection<PlatformProductView>> GetProductsAsync(string subject, CancellationToken cancellationToken);
}

public sealed class PlatformCatalogClient(IHttpClientFactory clients, IConfiguration configuration, ILogger<PlatformCatalogClient> logger) : IPlatformCatalogClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyCollection<PlatformProductView>> GetProductsAsync(string subject, CancellationToken cancellationToken)
    {
        var baseUrl = configuration["PlatformControlCenter:BaseUrl"];
        var authority = configuration["PlatformControlCenter:Authority"] ?? configuration["Authentication:Authority"];
        var clientId = configuration["PlatformControlCenter:ClientId"];
        var clientSecret = configuration["PlatformControlCenter:ClientSecret"];
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(authority) || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret) || string.IsNullOrWhiteSpace(subject))
        {
            logger.LogWarning("Platform Control Center catalog is not configured; no product catalog is shown.");
            return [];
        }

        using var tokenClient = clients.CreateClient("platform-control-token");
        using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, authority.TrimEnd('/') + "/protocol/openid-connect/token")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = clientId,
                ["client_secret"] = clientSecret,
                ["scope"] = "openid"
            })
        };
        using var tokenResponse = await tokenClient.SendAsync(tokenRequest, cancellationToken);
        if (!tokenResponse.IsSuccessStatusCode)
        {
            logger.LogWarning("Platform Control Center token request failed with status {StatusCode}.", tokenResponse.StatusCode);
            return [];
        }
        var token = await tokenResponse.Content.ReadFromJsonAsync<TokenResponse>(JsonOptions, cancellationToken);
        if (string.IsNullOrWhiteSpace(token?.AccessToken)) return [];

        using var catalogClient = clients.CreateClient("platform-control-catalog");
        using var catalogRequest = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl.TrimEnd('/')}/api/platform/products?subject={Uri.EscapeDataString(subject)}");
        catalogRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        using var catalogResponse = await catalogClient.SendAsync(catalogRequest, cancellationToken);
        if (!catalogResponse.IsSuccessStatusCode)
        {
            logger.LogWarning("Platform Control Center catalog request failed with status {StatusCode}.", catalogResponse.StatusCode);
            return [];
        }
        return await catalogResponse.Content.ReadFromJsonAsync<PlatformProductView[]>(JsonOptions, cancellationToken) ?? [];
    }

    private sealed record TokenResponse([property: JsonPropertyName("access_token")] string AccessToken);
}
