//using System.Net.Http.Json;
//using System.Text;
//using Microsoft.Extensions.Logging;
//using Microsoft.Extensions.Options;

//namespace Club.Channel.Application.Features.Auth.Services;

///// <summary>
///// تنظیمات OAuth 2.0
///// </summary>
//public class OAuth2Options
//{
//    public string TokenProvider { get; set; } = "Memory";
//    public KeycloakOptions? Keycloak { get; set; }
//}

//public class KeycloakOptions
//{
//    public string Authority { get; set; } = null!;
//    public string ClientId { get; set; } = null!;
//    public string ClientSecret { get; set; } = null!;
//    public string TokenEndpoint { get; set; } = "/protocol/openid-connect/token";
//}

///// <summary>
///// پیاده‌سازی Token Service با استفاده از Keycloak (برای Staging/Production)
///// </summary>
//internal sealed class KeycloakTokenService(
//    IHttpClientFactory httpClientFactory,
//    IOptions<OAuth2Options> options,
//    ILogger<KeycloakTokenService> logger
//) : ITokenService
//{
//    private readonly OAuth2Options _options = options.Value;

//    public async Task<TokenResponse> GetTokenAsync(string clientId, string clientSecret, CancellationToken cancellationToken = default)
//    {
//        if (_options.Keycloak == null)
//        {
//            throw new InvalidOperationException("Keycloak configuration is not set");
//        }

//        var httpClient = httpClientFactory.CreateClient();
//        var tokenEndpoint = $"{_options.Keycloak.Authority}{_options.Keycloak.TokenEndpoint}";

//        var requestBody = new Dictionary<string, string>
//        {
//            { "grant_type", "client_credentials" },
//            { "client_id", clientId },
//            { "client_secret", clientSecret }
//        };

//        var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
//        {
//            Content = new FormUrlEncodedContent(requestBody)
//        };

//        try
//        {
//            var response = await httpClient.SendAsync(request, cancellationToken);
//            response.EnsureSuccessStatusCode();

//            var tokenData = await response.Content.ReadFromJsonAsync<KeycloakTokenResponse>(cancellationToken: cancellationToken);
            
//            if (tokenData == null)
//            {
//                throw new InvalidOperationException("Failed to parse token response from Keycloak");
//            }

//            logger.LogInformation("Token obtained from Keycloak for client {ClientId}", clientId);

//            return new TokenResponse(
//                AccessToken: tokenData.AccessToken,
//                ExpiresIn: tokenData.ExpiresIn,
//                TokenType: tokenData.TokenType ?? "Bearer",
//                RefreshToken: tokenData.RefreshToken,
//                Scope: tokenData.Scope
//            );
//        }
//        catch (HttpRequestException ex)
//        {
//            logger.LogError(ex, "Failed to get token from Keycloak");
//            throw new UnauthorizedAccessException("Failed to authenticate with Keycloak", ex);
//        }
//    }

//    public async Task<bool> ValidateTokenAsync(string token, CancellationToken cancellationToken = default)
//    {
//        if (_options.Keycloak == null)
//        {
//            return false;
//        }

//        var httpClient = httpClientFactory.CreateClient();
//        var introspectionEndpoint = $"{_options.Keycloak.Authority}/protocol/openid-connect/token/introspect";

//        var requestBody = new Dictionary<string, string>
//        {
//            { "token", token },
//            { "client_id", _options.Keycloak.ClientId },
//            { "client_secret", _options.Keycloak.ClientSecret }
//        };

//        var request = new HttpRequestMessage(HttpMethod.Post, introspectionEndpoint)
//        {
//            Content = new FormUrlEncodedContent(requestBody)
//        };

//        try
//        {
//            var response = await httpClient.SendAsync(request, cancellationToken);
//            response.EnsureSuccessStatusCode();

//            var introspection = await response.Content.ReadFromJsonAsync<KeycloakIntrospectionResponse>(cancellationToken: cancellationToken);
//            return introspection?.Active ?? false;
//        }
//        catch
//        {
//            return false;
//        }
//    }

//    private record KeycloakTokenResponse(
//        string AccessToken,
//        int ExpiresIn,
//        string? TokenType,
//        string? RefreshToken,
//        string? Scope
//    );

//    private record KeycloakIntrospectionResponse(
//        bool Active
//    );
//}

