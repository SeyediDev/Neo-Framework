using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fanasa.UnifiedPortal.Web;

public sealed record PlatformProductView(Guid Id, string Key, string DisplayName, string Audience, string Center, string? Url, string? Repository, Guid? TenantId, string Owner, Dictionary<string, string>? Attributes);
public sealed record PlatformOrganizationView(Guid Id, string Name, string Namespace);
public enum CatalogStatus { Ready, SignInRequired, NotConfigured, Forbidden, Unavailable }
public sealed record PlatformWorkspace(CatalogStatus Status, IReadOnlyCollection<PlatformOrganizationView> Organizations,
    IReadOnlyCollection<PlatformProductView> Products, Guid? ProductOrganizationId = null)
{
    public static Guid? SelectOrganization(IReadOnlyCollection<PlatformOrganizationView> organizations, Guid? requested)
        => requested.HasValue ? organizations.Any(x => x.Id == requested) ? requested : null
            : organizations.Count == 1 ? organizations.First().Id : null;
}

public interface IPlatformCatalogClient
{
    Task<PlatformWorkspace> GetWorkspaceAsync(string subject, CancellationToken cancellationToken, Guid? organizationId = null);
}

public sealed class PlatformCatalogClient(IHttpClientFactory clients, IConfiguration configuration, ILogger<PlatformCatalogClient> logger) : IPlatformCatalogClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<PlatformWorkspace> GetWorkspaceAsync(string subject, CancellationToken cancellationToken, Guid? organizationId = null)
    {
        if (string.IsNullOrWhiteSpace(subject)) return new(CatalogStatus.SignInRequired, [], []);
        var baseUrl = configuration["PlatformControlCenter:BaseUrl"];
        var authority = configuration["PlatformControlCenter:Authority"];
        if (string.IsNullOrWhiteSpace(authority)) authority = configuration["Authentication:Authority"];
        var clientId = configuration["PlatformControlCenter:ClientId"];
        var clientSecret = configuration["PlatformControlCenter:ClientSecret"];
        if (!IsServiceUrl(baseUrl) || !IsServiceUrl(authority) || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(clientSecret))
            return new(CatalogStatus.NotConfigured, [], []);

        IReadOnlyCollection<PlatformOrganizationView> organizations = [];
        try
        {
            using var tokenClient = clients.CreateClient("platform-control-token");
            using var tokenResponse = await tokenClient.PostAsync(authority!.TrimEnd('/') + "/protocol/openid-connect/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials", ["client_id"] = clientId, ["client_secret"] = clientSecret,
                    ["scope"] = configuration["PlatformControlCenter:Scope"] ?? "platform.catalog"
                }), cancellationToken);
            if (tokenResponse.StatusCode == HttpStatusCode.BadRequest)
            {
                logger.LogWarning("Platform catalog service token configuration was rejected.");
                return new(CatalogStatus.NotConfigured, [], []);
            }
            if (!tokenResponse.IsSuccessStatusCode) return Failure(tokenResponse.StatusCode);
            var token = await tokenResponse.Content.ReadFromJsonAsync<TokenResponse>(JsonOptions, cancellationToken);
            if (string.IsNullOrWhiteSpace(token?.AccessToken)) return new(CatalogStatus.Unavailable, [], []);

            using var client = clients.CreateClient("platform-control-catalog");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
            var query = "?subject=" + Uri.EscapeDataString(subject);
            using var membershipsResponse = await client.GetAsync(baseUrl!.TrimEnd('/') + "/api/platform/application-tenants" + query, cancellationToken);
            if (!membershipsResponse.IsSuccessStatusCode) return Failure(membershipsResponse.StatusCode);
            var memberships = await membershipsResponse.Content.ReadFromJsonAsync<PlatformOrganizationView[]>(JsonOptions, cancellationToken)
                ?? throw new JsonException("Expected a membership array.");
            if (memberships.Any(x => x is null || x.Id == Guid.Empty || string.IsNullOrWhiteSpace(x.Name)))
                throw new JsonException("Invalid membership record.");
            organizations = memberships.OrderBy(x => x.Name).ToArray();
            var selected = PlatformWorkspace.SelectOrganization(organizations, organizationId);
            // Never request the cross-tenant union or query products for an unverified tenant.
            if (!selected.HasValue) return new(CatalogStatus.Ready, organizations, []);
            using var productsResponse = await client.GetAsync(baseUrl.TrimEnd('/') + "/api/platform/products" + query + "&tenant=" + selected.Value, cancellationToken);
            if (!productsResponse.IsSuccessStatusCode) return new(Failure(productsResponse.StatusCode).Status, organizations, []);
            var products = await productsResponse.Content.ReadFromJsonAsync<PlatformProductView[]>(JsonOptions, cancellationToken)
                ?? throw new JsonException("Expected a product array.");
            if (products.Any(x => x is null || x.Id == Guid.Empty || string.IsNullOrWhiteSpace(x.Key) || string.IsNullOrWhiteSpace(x.DisplayName)))
                throw new JsonException("Invalid product record.");
            // Product.TenantId identifies the producer, not the consumer. The central API
            // enforces offers/grants for the selected consumer tenant supplied above.
            return new(CatalogStatus.Ready, organizations, products, selected);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            // Never log tokens, response bodies, credentials or user subjects.
            logger.LogWarning("Platform catalog unavailable ({FailureType}).", ex.GetType().Name);
            return new(CatalogStatus.Unavailable, organizations, []);
        }
    }

    private static bool IsServiceUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme is "https" or "http" && string.IsNullOrEmpty(uri.UserInfo)
        && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);

    private PlatformWorkspace Failure(HttpStatusCode status)
    {
        logger.LogWarning("Platform catalog request failed with HTTP {StatusCode}.", (int)status);
        return new(status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? CatalogStatus.Forbidden : CatalogStatus.Unavailable, [], []);
    }

    private sealed record TokenResponse([property: JsonPropertyName("access_token")] string AccessToken);
}
