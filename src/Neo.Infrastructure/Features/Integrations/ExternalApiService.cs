using System.Linq;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Neo.Domain.Entities.Integrations;
using Neo.Domain.Features.Integrations;

namespace Neo.Infrastructure.Features.Integrations;

public class ExternalApiService(
    IHttpClientFactory httpClientFactory,
    IMemoryCache memoryCache,
    ILogger<ExternalApiService> logger,
    TimeProvider timeProvider) : IExternalApiService
{
    private const string DefaultClientName = "ExternalApiService";

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<ExternalApiInvocationResult> InvokeAsync(ExternalApi externalApi, ExternalApiRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(externalApi);
        ArgumentNullException.ThrowIfNull(request);

        using HttpClient httpClient = httpClientFactory.CreateClient(DefaultClientName);
        if (request.Timeout.HasValue)
        {
            httpClient.Timeout = request.Timeout.Value;
        }

        string requestUri = BuildRequestUri(externalApi, request);
        HttpRequestMessage httpRequestMessage = new(GetHttpMethod(externalApi.Method), requestUri);

        await ApplyAuthenticationAsync(externalApi, httpRequestMessage, cancellationToken);
        ApplyHeaders(externalApi, httpRequestMessage, request);
        ApplyBody(httpRequestMessage, request);

        long startTimestamp = timeProvider.GetTimestamp();
        HttpResponseMessage response;
        try
        {
            response = await httpClient.SendAsync(httpRequestMessage, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "External API call failed: {Name} ({Url})", externalApi.Name, requestUri);
            throw;
        }

        string? content = response.Content != null
            ? await response.Content.ReadAsStringAsync(cancellationToken)
            : null;

        Dictionary<string, string[]> headers = CollectHeaders(response);
        TimeSpan duration = timeProvider.GetElapsedTime(startTimestamp);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning("External API call returned non-success status {StatusCode} for {Name}", response.StatusCode, externalApi.Name);
        }

        return new ExternalApiInvocationResult(
            (int)response.StatusCode,
            content,
            headers,
            response.IsSuccessStatusCode ? null : content,
            duration);
    }

    private static string BuildRequestUri(ExternalApi externalApi, ExternalApiRequest request)
    {
        string baseUrl = externalApi.BaseUrl?.TrimEnd('/') ?? string.Empty;
        string relativePath = externalApi.RelativePath?.TrimStart('/') ?? string.Empty;

        string path = ReplacePathParameters(relativePath, request.PathParameters);

        string url = string.IsNullOrEmpty(baseUrl) ? path : $"{baseUrl}/{path}".TrimEnd('/');

        string query = BuildQueryString(request.QueryParameters);
        if (!string.IsNullOrWhiteSpace(query))
        {
            url = $"{url}?{query}";
        }

        return url;
    }

    private static string ReplacePathParameters(string relativePath, IDictionary<string, string?> pathParameters)
    {
        if (pathParameters.Count == 0)
        {
            return relativePath;
        }

        string result = relativePath;
        foreach ((string key, string? value) in pathParameters)
        {
            string token = $"{{{key}}}";
            if (result.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                result = result.Replace(token, Uri.EscapeDataString(value ?? string.Empty), StringComparison.OrdinalIgnoreCase);
            }
        }

        return result;
    }

    private static string BuildQueryString(IDictionary<string, string?> queryParameters)
    {
        if (queryParameters.Count == 0)
        {
            return string.Empty;
        }

        return string.Join("&",
            queryParameters
                .Where(kvp => kvp.Value is not null)
                .Select(kvp => $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value!)}"));
    }

    private async Task ApplyAuthenticationAsync(ExternalApi externalApi, HttpRequestMessage httpRequestMessage, CancellationToken cancellationToken)
    {
        if (!externalApi.RequiresAuthentication)
        {
            return;
        }

        if (externalApi.UseOAuth2)
        {
            string? token = await GetOAuth2TokenAsync(externalApi, cancellationToken);
            if (!string.IsNullOrWhiteSpace(token))
            {
                httpRequestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            }
            return;
        }

        if (!string.IsNullOrWhiteSpace(externalApi.AuthenticationScheme))
        {
            string authenticationScheme = externalApi.AuthenticationScheme;
            if (authenticationScheme.Contains(' ', StringComparison.Ordinal))
            {
                httpRequestMessage.Headers.TryAddWithoutValidation("Authorization", authenticationScheme);
            }
            else
            {
                httpRequestMessage.Headers.Authorization = new AuthenticationHeaderValue(authenticationScheme);
            }
        }
    }

    private static void ApplyHeaders(ExternalApi externalApi, HttpRequestMessage httpRequestMessage, ExternalApiRequest request)
    {
        foreach ((string key, string value) in ReadJsonDictionary(externalApi.DefaultHeadersJson))
        {
            httpRequestMessage.Headers.TryAddWithoutValidation(key, value);
        }

        foreach ((string key, string? value) in request.Headers)
        {
            if (string.IsNullOrEmpty(value))
            {
                continue;
            }

            if (!httpRequestMessage.Headers.TryAddWithoutValidation(key, value))
            {
                httpRequestMessage.Content ??= new StringContent(string.Empty);
                httpRequestMessage.Content.Headers.TryAddWithoutValidation(key, value);
            }
        }
    }

    private static void ApplyBody(HttpRequestMessage httpRequestMessage, ExternalApiRequest request)
    {
        if (request.Body is null)
        {
            return;
        }

        StringContent stringContent = new(request.Body.Content, Encoding.UTF8, request.Body.ContentType);
        if (!string.IsNullOrWhiteSpace(request.Body.Charset))
        {
            stringContent.Headers.ContentType!.CharSet = request.Body.Charset;
        }

        httpRequestMessage.Content = stringContent;
    }

    private async Task<string?> GetOAuth2TokenAsync(ExternalApi externalApi, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(externalApi.TokenEndpoint) ||
            string.IsNullOrWhiteSpace(externalApi.ClientId))
        {
            logger.LogWarning("OAuth2 configuration is incomplete for external API {Name}", externalApi.Name);
            return null;
        }

        string cacheKey = $"ExternalApiOAuthToken:{externalApi.Id}:{externalApi.ClientId}:{externalApi.Scope}:{externalApi.Resource}:{externalApi.Audience}";
        if (memoryCache.TryGetValue(cacheKey, out string? token) && !string.IsNullOrEmpty(token))
        {
            return token;
        }

        Dictionary<string, string?> parameters = BuildTokenParameters(externalApi);

        using HttpRequestMessage tokenRequest = new(HttpMethod.Post, externalApi.TokenEndpoint);
        ApplyClientAuthentication(externalApi, tokenRequest, parameters);

        tokenRequest.Content = new FormUrlEncodedContent(
            parameters
                .Where(kvp => !string.IsNullOrWhiteSpace(kvp.Value))
                .Select(kvp => new KeyValuePair<string, string>(kvp.Key, kvp.Value!)));

        using HttpClient httpClient = httpClientFactory.CreateClient(DefaultClientName);
        HttpResponseMessage response = await httpClient.SendAsync(tokenRequest, cancellationToken);
        string responseContent = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            logger.LogError("OAuth2 token request failed for {Name} with status {StatusCode}. Response: {Content}",
                externalApi.Name, response.StatusCode, responseContent);
            return null;
        }

        using JsonDocument document = JsonDocument.Parse(responseContent);
        if (!document.RootElement.TryGetProperty("access_token", out JsonElement accessTokenElement))
        {
            logger.LogError("OAuth2 token response did not contain access_token for {Name}", externalApi.Name);
            return null;
        }

        string accessToken = accessTokenElement.GetString()!;
        TimeSpan cacheDuration = GetTokenCacheDuration(externalApi, document);
        memoryCache.Set(cacheKey, accessToken, cacheDuration);

        return accessToken;
    }

    private static Dictionary<string, string?> BuildTokenParameters(ExternalApi externalApi)
    {
        Dictionary<string, string?> parameters = new(StringComparer.Ordinal)
        {
            ["client_id"] = externalApi.ClientId,
            ["grant_type"] = GetGrantTypeValue(externalApi.OAuthGrantType)
        };

        if (!string.IsNullOrWhiteSpace(externalApi.Scope))
        {
            parameters["scope"] = externalApi.Scope;
        }
        if (!string.IsNullOrWhiteSpace(externalApi.Resource))
        {
            parameters["resource"] = externalApi.Resource;
        }
        if (!string.IsNullOrWhiteSpace(externalApi.Audience))
        {
            parameters["audience"] = externalApi.Audience;
        }

        if (externalApi.OAuthGrantType == ExternalApiOAuthGrantType.Password)
        {
            parameters["username"] = externalApi.Username;
            parameters["password"] = externalApi.Password;
        }

        foreach ((string key, string value) in ReadJsonDictionary(externalApi.AdditionalAuthParametersJson))
        {
            parameters[key] = value;
        }

        return parameters;
    }

    private static void ApplyClientAuthentication(ExternalApi externalApi, HttpRequestMessage tokenRequest, IDictionary<string, string?> parameters)
    {
        string method = externalApi.ClientAuthenticationMethod?.ToLowerInvariant() ?? "client_secret_post";

        if (method is "client_secret_basic")
        {
            if (!string.IsNullOrEmpty(externalApi.ClientSecret))
            {
                string clientId = externalApi.ClientId ?? string.Empty;
                string clientSecret = externalApi.ClientSecret ?? string.Empty;
                string credentials = $"{clientId}:{clientSecret}";
                string encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials));
                tokenRequest.Headers.Authorization = new AuthenticationHeaderValue("Basic", encoded);
            }
        }
        else
        {
            parameters["client_secret"] = externalApi.ClientSecret ?? string.Empty;
        }
    }

    private static string GetGrantTypeValue(ExternalApiOAuthGrantType grantType) =>
        grantType switch
        {
            ExternalApiOAuthGrantType.ClientCredentials => "client_credentials",
            ExternalApiOAuthGrantType.Password => "password",
            ExternalApiOAuthGrantType.AuthorizationCode => "authorization_code",
            ExternalApiOAuthGrantType.DeviceCode => "urn:ietf:params:oauth:grant-type:device_code",
            ExternalApiOAuthGrantType.Custom => "custom",
            _ => "client_credentials"
        };

    private static TimeSpan GetTokenCacheDuration(ExternalApi externalApi, JsonDocument tokenResponse)
    {
        int? configuredLifetime = externalApi.TokenLifetimeSeconds;
        int clockSkew = externalApi.TokenClockSkewSeconds ?? 30;

        int? expiresIn = tokenResponse.RootElement.TryGetProperty("expires_in", out JsonElement expiresInElement)
            ? expiresInElement.GetInt32()
            : null;

        int lifetimeSeconds = configuredLifetime ?? expiresIn ?? 3600;
        lifetimeSeconds = Math.Max(60, lifetimeSeconds - clockSkew);

        return TimeSpan.FromSeconds(lifetimeSeconds);
    }

    private static Dictionary<string, string[]> CollectHeaders(HttpResponseMessage response)
    {
        Dictionary<string, List<string>> temp = [];

        foreach (KeyValuePair<string, IEnumerable<string>> header in response.Headers)
        {
            temp[header.Key] = header.Value.ToList();
        }

        if (response.Content != null)
        {
            foreach (KeyValuePair<string, IEnumerable<string>> header in response.Content.Headers)
            {
                if (!temp.TryGetValue(header.Key, out List<string>? valueList))
                {
                    valueList = [];
                    temp[header.Key] = valueList;
                }

                valueList.AddRange(header.Value);
            }
        }

        return temp.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToArray(), StringComparer.OrdinalIgnoreCase);
    }

    private static HttpMethod GetHttpMethod(ExternalApiHttpMethod method) =>
        method switch
        {
            ExternalApiHttpMethod.Get => HttpMethod.Get,
            ExternalApiHttpMethod.Post => HttpMethod.Post,
            ExternalApiHttpMethod.Put => HttpMethod.Put,
            ExternalApiHttpMethod.Delete => HttpMethod.Delete,
            ExternalApiHttpMethod.Patch => HttpMethod.Patch,
            ExternalApiHttpMethod.Head => HttpMethod.Head,
            ExternalApiHttpMethod.Options => HttpMethod.Options,
            _ => HttpMethod.Get
        };

    private static Dictionary<string, string> ReadJsonDictionary(string? json)
    {
        Dictionary<string, string> result = new(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json))
        {
            return result;
        }

        try
        {
            Dictionary<string, JsonElement>? parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json, SerializerOptions);
            if (parsed is null)
            {
                return result;
            }

            foreach ((string key, JsonElement value) in parsed)
            {
                result[key] = value.ValueKind switch
                {
                    JsonValueKind.String => value.GetString() ?? string.Empty,
                    JsonValueKind.Number => value.ToString(),
                    JsonValueKind.True => "true",
                    JsonValueKind.False => "false",
                    JsonValueKind.Array => string.Join(",", value.EnumerateArray().Select(item => item.ToString())),
                    _ => value.ToString()
                };
            }
        }
        catch (JsonException)
        {
            // ignore malformed json and fallback to empty dictionary
        }

        return result;
    }
}
