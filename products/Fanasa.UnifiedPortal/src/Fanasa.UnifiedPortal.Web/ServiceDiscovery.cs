using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fanasa.UnifiedPortal.Web;

// Publicly publishable metadata only. No client, repository, attributes or launch URL.
public sealed record PublishedService(Guid ProductId, string Key, string DisplayName, string Center,
    string Description, string? DetailsUrl, string? RequestAccessUrl, bool Published);
public sealed record ServiceDiscovery(CatalogStatus Status, IReadOnlyList<PublishedService> Services);
public interface IServiceDiscoveryClient
{
    Task<ServiceDiscovery> GetAsync(CancellationToken cancellationToken);
}

public sealed class ServiceDiscoveryClient(IHttpClientFactory clients, IConfiguration configuration,
    ILogger<ServiceDiscoveryClient> logger) : IServiceDiscoveryClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    // Explicit configuration: never assume that the privileged product-list API is public discovery.
    public async Task<ServiceDiscovery> GetAsync(CancellationToken cancellationToken)
    {
        var origin = configuration["PlatformControlCenter:BaseUrl"];
        var path = configuration["PlatformControlCenter:DiscoveryPath"];
        var authority = configuration["PlatformControlCenter:Authority"];
        if (string.IsNullOrWhiteSpace(authority)) authority = configuration["Authentication:Authority"];
        var clientId = configuration["PlatformControlCenter:ClientId"];
        var secret = configuration["PlatformControlCenter:ClientSecret"];
        if (!ServiceUrls.IsOriginOrBase(origin) || !ServiceUrls.IsOriginOrBase(authority)
            || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(secret)
            || string.IsNullOrWhiteSpace(path) || !path.StartsWith("/api/platform/", StringComparison.Ordinal)
            || path.Contains('?') || path.Contains('#') || path.Contains('\\') || path.Contains('%')
            || path.Split('/').Any(segment => segment is "." or ".."))
            return new(CatalogStatus.NotConfigured, []);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            var transportToken = deadline.Token;
            using var tokenClient = clients.CreateClient("platform-control-token");
            using var tokenResponse = await tokenClient.PostAsync(authority!.TrimEnd('/') + "/protocol/openid-connect/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "client_credentials", ["client_id"] = clientId, ["client_secret"] = secret,
                    ["scope"] = configuration["PlatformControlCenter:DiscoveryScope"] ?? "platform.catalog"
                }), transportToken);
            if (tokenResponse.StatusCode == HttpStatusCode.BadRequest) return new(CatalogStatus.NotConfigured, []);
            if (!tokenResponse.IsSuccessStatusCode) return Failure(tokenResponse.StatusCode);
            var token = await tokenResponse.Content.ReadFromJsonAsync<ServiceToken>(JsonOptions, transportToken);
            if (string.IsNullOrWhiteSpace(token?.AccessToken)) return new(CatalogStatus.Unavailable, []);
            using var client = clients.CreateClient("platform-control-discovery");
            using var request = new HttpRequestMessage(HttpMethod.Get, origin!.TrimEnd('/') + path);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, transportToken);
            if (!response.IsSuccessStatusCode) return Failure(response.StatusCode);
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType != "application/json" && mediaType?.EndsWith("+json", StringComparison.OrdinalIgnoreCase) != true)
                return new(CatalogStatus.Unavailable, []);
            // Bound the entire body, not just Content-Length, including chunked responses.
            const int maximumBytes = 2 * 1024 * 1024;
            if (response.Content.Headers.ContentLength > maximumBytes) return new(CatalogStatus.Unavailable, []);
            using var body = await response.Content.ReadAsStreamAsync(transportToken);
            using var bounded = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await body.ReadAsync(buffer, transportToken)) > 0)
            {
                if (bounded.Length + read > maximumBytes) return new(CatalogStatus.Unavailable, []);
                bounded.Write(buffer, 0, read);
            }
            var services = JsonSerializer.Deserialize<PublishedService[]>(bounded.ToArray(), JsonOptions)
                ?? throw new JsonException("Expected published service array.");
            if (services.Length > 2000 || services.Any(x => x is null || x.ProductId == Guid.Empty
                || string.IsNullOrWhiteSpace(x.Key) || string.IsNullOrWhiteSpace(x.DisplayName)
                || string.IsNullOrWhiteSpace(x.Description) || !ServiceCatalogCenters.All.Any(c => c.Id == x.Center))
                || services.Select(x => x.ProductId).Distinct().Count() != services.Length
                || services.Select(x => x.Key).Distinct(StringComparer.Ordinal).Count() != services.Length)
                throw new JsonException("Invalid public service metadata.");
            return new(CatalogStatus.Ready, services.Where(x => x.Published).Select(x => x with
            {
                DetailsUrl = ServiceUrls.Safe(x.DetailsUrl), RequestAccessUrl = ServiceUrls.Safe(x.RequestAccessUrl)
            }).OrderBy(x => x.DisplayName).ToArray());
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Public service discovery unavailable ({FailureType}).", ex.GetType().Name);
            return new(CatalogStatus.Unavailable, []);
        }
    }

    private static ServiceDiscovery Failure(HttpStatusCode code) => new(
        code is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden ? CatalogStatus.Forbidden : CatalogStatus.Unavailable, []);
    private sealed record ServiceToken([property: JsonPropertyName("access_token")] string AccessToken);
}

public static class ServiceUrls
{
    public static string? Safe(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https" && string.IsNullOrEmpty(uri.UserInfo) ? uri.AbsoluteUri : null;
    public static bool IsOriginOrBase(string? value) => Safe(value) is not null && Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);
}

public sealed record ServiceCatalogCard(Guid ProductId, string Key, string DisplayName, string Center,
    string Description, string? DetailsUrl, string? RequestAccessUrl, string? LaunchUrl, string AccessState);
public sealed record ServiceCatalogPresentation(string Status, string Message, IReadOnlyList<ServiceCatalogCard> Services)
{
    public static ServiceCatalogPresentation Create(ServiceDiscovery discovery, CatalogStatus workspaceStatus,
        Guid? consumer, IReadOnlyCollection<PlatformProductView> allowed)
    {
        var permissions = workspaceStatus == CatalogStatus.Ready && consumer.HasValue
            ? allowed.ToLookup(x => (x.Id, x.Key, x.Center)) : null;
        var cards = discovery.Status == CatalogStatus.Ready ? discovery.Services.Where(x => x.Published).Select(x =>
        {
            // Match immutable product identity AND key/center. Metadata must never issue a grant.
            var accessible = permissions?[(x.ProductId, x.Key, x.Center)].FirstOrDefault();
            var state = workspaceStatus == CatalogStatus.SignInRequired ? "signInRequired"
                : workspaceStatus != CatalogStatus.Ready ? "unverified" : !consumer.HasValue ? "selectOrganization"
                : accessible is null ? "requestRequired" : "allowed";
            return new ServiceCatalogCard(x.ProductId, x.Key, x.DisplayName, x.Center, x.Description,
                ServiceUrls.Safe(x.DetailsUrl), ServiceUrls.Safe(x.RequestAccessUrl), ServiceUrls.Safe(accessible?.Url), state);
        }).ToArray() : [];
        var message = discovery.Status switch
        {
            CatalogStatus.Ready when cards.Length == 0 => "هنوز سرویسی در کاتالوگ منتشر نشده است.",
            CatalogStatus.Ready => "همهٔ سرویس‌های منتشرشده؛ ورود متناسب با دسترسی سازمان فعال شماست.",
            CatalogStatus.NotConfigured => "اتصال کاتالوگ خدمات هنوز آماده نیست؛ فهرست سرویس‌ها دریافت نشده است.",
            CatalogStatus.Forbidden => "دسترسی خواندن کاتالوگ برقرار نشد؛ با پشتیبانی تماس بگیرید.",
            _ => "کاتالوگ خدمات دریافت نشد؛ دوباره تلاش کنید."
        };
        return new(discovery.Status.ToString(), message, cards);
    }
}

public sealed record ServiceCatalogCenter(string Id, string AssetKey);
public static class ServiceCatalogCenters
{
    public static IReadOnlyList<ServiceCatalogCenter> All { get; } = new[]
    { "abrgan", "karavan", "kavan", "peyvand", "dana", "bonyan", "fanasa-cloud", "asman", "kerane", "rayan", "dejan", "ayin", "didban", "tarazu" }
        .Select(key => new ServiceCatalogCenter(key == "fanasa-cloud" ? "fanasa.cloud" : "fanasa." + key, key)).ToArray();
}
