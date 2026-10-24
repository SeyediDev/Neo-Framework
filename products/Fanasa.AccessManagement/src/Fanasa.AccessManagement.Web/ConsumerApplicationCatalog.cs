using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Persistence;
using Fanasa.AccessManagement.Web.Platform;
using Fanasa.AccessManagement.Web.ServiceCatalog;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace Fanasa.AccessManagement.Web.ConsumerCatalog;

public sealed record ConsumerApplicationView(Guid Id, string Key, string DisplayName, string Audience,
    string Center, string? Url, string? Repository, Guid? TenantId, string Owner,
    Dictionary<string, string>? Attributes);

/// <summary>
/// Read-only compatibility with ApplicationTenancy.cs at ef86d98.
/// Existing registry offers/grants remain authoritative; no schema or permission is created.
/// Fabric and registry snapshots are separate and are not a distributed transaction.
/// </summary>
public sealed class ConsumerApplicationCatalog(FabricDatabase database, PersistentAccessManagement access,
    IConfiguration configuration, TimeProvider clock)
{
    public const int MaxItems = 2000;
    public const int MaxBytes = 2 * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Regex ZonedTimestamp = new(
        @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public PlatformTenant[] Tenants(string subject, Func<bool> authorize)
    {
        var result = database.ReadSnapshot(() =>
        {
            Authority(authorize);
            var tenants = access.GetTenants();
            if (tenants.Count > 10000) throw new InvalidDataException();
            var values = tenants.Where(x => x.IsActive && Member(subject, x.Id))
                .Select(x => new PlatformTenant(x.Id, x.DisplayName, x.Key)).OrderBy(x => x.Id).ToArray();
            Bound(values);
            Authority(authorize);
            return values;
        });
        // Observe committed Fabric revocations after projection has released its snapshot.
        Authority(authorize);
        if (result.Any(x => !Member(subject, x.Id))) throw new UnauthorizedAccessException();
        return result;
    }

    public ConsumerApplicationView[] Products(string subject, Guid tenant, Func<bool> authorize)
    {
        DateTimeOffset? validUntil = null;
        var response = database.ReadSnapshot(() =>
        {
            Authority(authorize);
            if (!Member(subject, tenant)) throw new UnauthorizedAccessException();
            var path = configuration["Platform:RegistryPath"]
                ?? Path.Combine(AppContext.BaseDirectory, "App_Data", "platform-registry.db");
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            {
                DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false, DefaultTimeout = 5
            }.ToString());
            connection.Open();
            using var transaction = connection.BeginTransaction(deferred: true);
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            // Join only this consumer's explicit grants: unrelated new registrations need no legacy flags.
            // Missing historical schema or invalid candidate metadata fails closed.
            command.CommandText = """
                SELECT g.product,g.expires,
                       CASE WHEN length(o.data)<=2097152 THEN o.data END,
                       p.id,p.key,
                       CASE WHEN length(p.data)<=2097152 THEN p.data END
                FROM platform_application_grants g
                LEFT JOIN platform_application_offers o ON o.product=g.product AND o.tenant=g.tenant
                LEFT JOIN platform_products p ON p.key=g.product
                WHERE g.tenant=$tenant AND g.subject=$subject
                ORDER BY g.product LIMIT 2001
                """;
            command.Parameters.AddWithValue("$tenant", tenant.ToString("D"));
            command.Parameters.AddWithValue("$subject", subject);
            using var reader = command.ExecuteReader();
            var result = new List<ConsumerApplicationView>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var scanned = 0;
            var now = clock.GetUtcNow();
            while (reader.Read())
            {
                if (++scanned > MaxItems) throw new InvalidDataException();
                var grantExpiry = reader.IsDBNull(1) ? (DateTimeOffset?)null : Date(reader.GetString(1));
                if (grantExpiry.HasValue && grantExpiry <= now) continue;
                if (reader.IsDBNull(2) || reader.IsDBNull(3) || reader.IsDBNull(5))
                    throw new InvalidDataException();
                var key = reader.GetString(0);
                if (!seen.Add(key)) throw new InvalidDataException();
                using var offerDocument = Document(reader.GetString(2));
                var offer = offerDocument.RootElement;
                if (Text(offer, "productKey", 90) != key || Id(offer, "tenantId") != tenant)
                    throw new InvalidDataException();
                var active = offer.GetProperty("active");
                if (active.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new InvalidDataException();
                var seats = offer.GetProperty("seatLimit").GetInt32();
                if (seats is < 1 or > 100000) throw new InvalidDataException();
                _ = Text(offer, "plan", 120);
                var expiryValue = offer.GetProperty("expiresAt");
                if (expiryValue.ValueKind is not (JsonValueKind.Null or JsonValueKind.String))
                    throw new InvalidDataException();
                var offerExpiry = expiryValue.ValueKind == JsonValueKind.Null
                    ? (DateTimeOffset?)null : Date(expiryValue.GetString()!);
                if (!active.GetBoolean() || offerExpiry.HasValue && offerExpiry <= now) continue;
                DateTimeOffset? subscriptionExpiry = null;
                // Older persisted offers predate the optional subscriptionId property.
                if (offer.TryGetProperty("subscriptionId", out var subscriptionValue)
                    && subscriptionValue.ValueKind != JsonValueKind.Null)
                {
                    var subscriptionId = Id(offer, "subscriptionId");
                    var subscriptions = access.GetSubscriptions(tenant)
                        .Where(x => x.Id == subscriptionId).Take(2).ToArray();
                    if (subscriptions.Length != 1) continue;
                    var subscription = subscriptions[0];
                    if (subscription.TenantId != tenant || subscription.ProductKey != key
                        || subscription.Status != "active" || subscription.StartedAt > now
                        || subscription.RenewsAt.HasValue && subscription.RenewsAt <= now
                        || subscription.SeatLimit < seats || subscription.ActiveSeats < 0
                        || subscription.ActiveSeats > subscription.SeatLimit) continue;
                    subscriptionExpiry = subscription.RenewsAt;
                }
                using var productDocument = Document(reader.GetString(5));
                var product = productDocument.RootElement;
                if (!product.TryGetProperty("publishAsApplication", out var published)
                    || published.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                    throw new InvalidDataException();
                if (!published.GetBoolean()) continue;
                var mode = Text(product, "tenancyMode", 20);
                var origin = Text(product, "origin", 20);
                if (mode is not ("saas" or "dedicated") || origin is not ("managed" or "external"))
                    throw new InvalidDataException();
                var owner = Id(product, "tenantId");
                if (access.GetTenants().Count(x => x.Id == owner && x.IsActive) != 1
                    || mode == "dedicated" && owner != tenant) continue;
                var id = Id(product, "id");
                if (reader.GetString(3) != id.ToString("D") || reader.GetString(4) != key
                    || Text(product, "key", 90) != key) throw new InvalidDataException();
                var center = Text(product, "center", 100);
                if (!CapabilityCatalog.All.Any(x => x.Audience == center)) throw new InvalidDataException();
                var launch = product.GetProperty("url");
                string? url = null;
                if (launch.ValueKind != JsonValueKind.Null)
                {
                    url = Text(product, "url", 2048);
                    if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https"
                        || uri.Port != 443 || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0
                        || url.Contains('\\') || uri.HostNameType != UriHostNameType.Dns
                        || origin == "managed" && !uri.Host.EndsWith(".fanasa.net.local", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException();
                }
                if (origin == "external" && url is null) throw new InvalidDataException();
                result.Add(new(id, key, Text(product, "displayName", 160), Text(product, "audience", 200),
                    center, url, Repository: null, TenantId: owner, Owner: "", Attributes: null));
                foreach (var deadline in new[] { grantExpiry, offerExpiry, subscriptionExpiry })
                    if (deadline.HasValue && (!validUntil.HasValue || deadline < validUntil)) validUntil = deadline;
            }
            reader.Close();
            var values = result.ToArray();
            Bound(values);
            Authority(authorize);
            transaction.Commit();
            return values;
        });
        Authority(authorize);
        if (!Member(subject, tenant) || validUntil.HasValue && clock.GetUtcNow() >= validUntil)
            throw new UnauthorizedAccessException();
        return response;
    }

    private bool Member(string subject, Guid tenant)
    {
        if (access.GetTenants().Count(x => x.Id == tenant && x.IsActive) != 1) return false;
        var users = access.GetUsers(tenant);
        if (users.Count > 10000) throw new InvalidDataException();
        return users.Count(x => x.KeycloakSubject == subject && x.IsActive) == 1;
    }
    private static void Authority(Func<bool> authorize)
    {
        if (!authorize()) throw new UnauthorizedAccessException();
    }
    private static void Bound<T>(T[] rows)
    {
        if (rows.Length > MaxItems || JsonSerializer.SerializeToUtf8Bytes(rows, Json).Length > MaxBytes)
            throw new InvalidDataException();
    }
    private static JsonDocument Document(string text)
    {
        if (text.Length > MaxBytes) throw new InvalidDataException();
        var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 12 });
        if (document.RootElement.ValueKind != JsonValueKind.Object
            || document.RootElement.EnumerateObject().Select(x => x.Name).Distinct(StringComparer.Ordinal).Count()
                != document.RootElement.EnumerateObject().Count())
        {
            document.Dispose();
            throw new InvalidDataException();
        }
        return document;
    }
    private static string Text(JsonElement value, string name, int maximum)
    {
        var result = value.GetProperty(name).GetString();
        if (string.IsNullOrWhiteSpace(result) || result.Length > maximum || result != result.Trim()
            || result.Any(char.IsControl)) throw new InvalidDataException();
        return result;
    }
    private static Guid Id(JsonElement value, string name)
    {
        var id = Guid.ParseExact(Text(value, name, 36), "D");
        return id != Guid.Empty ? id : throw new InvalidDataException();
    }
    private static DateTimeOffset Date(string text)
    {
        if (text.Length > 40 || !ZonedTimestamp.IsMatch(text)) throw new InvalidDataException();
        return JsonSerializer.Deserialize<DateTimeOffset>(JsonSerializer.Serialize(text));
    }
}

public static class ConsumerApplicationResponses
{
    public static bool IsConsumerRequest(HttpRequest request, ClaimsPrincipal user, IConfiguration configuration)
        => request.Query.ContainsKey("tenant")
            || user.FindAll("scope").Any(x => x.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Contains(PublishedServiceCatalog.ReadScope, StringComparer.Ordinal))
            || !string.IsNullOrWhiteSpace(configuration["Platform:ServiceCatalog:ClientId"])
                && user.FindAll("azp").Any(x => x.Value == configuration["Platform:ServiceCatalog:ClientId"]);

    public static IActionResult Read(ControllerBase controller, bool products, PlatformRegistry registry,
        FabricDatabase database, IConfiguration configuration, TimeProvider time, PersistentAccessManagement access)
    {
        controller.Response.Headers.CacheControl = "no-store";
        if (!configuration.GetValue<bool>("Platform:ConsumerCatalog:Enabled")
            || !PublishedServiceCatalog.ReaderConfigured(configuration))
            return controller.StatusCode(503, new { error = "Consumer application catalog is not configured." });
        bool AllowsDelegation()
        {
            if (!PublishedServiceCatalog.AllowsReader(controller.User, configuration, registry, access, time)) return false;
            var productsForClient = registry.Products()
                .Where(x => x.Key == configuration["Platform:ServiceCatalog:ClientProductKey"]).Take(2).ToArray();
            if (productsForClient.Length != 1) return false;
            var clients = access.GetClients(productsForClient[0].Id)
                .Where(x => x.Key == configuration["Platform:ServiceCatalog:ClientId"]).Take(2).ToArray();
            return clients.Length == 1 && clients[0].ClientType == "service";
        }
        try
        {
            if (!AllowsDelegation()) return controller.Forbid("PlatformBearer");
            var allowed = products ? new[] { "subject", "tenant" } : new[] { "subject" };
            var query = controller.Request.Query;
            var spoofedHeaders = new[] { "X-Subject", "X-Subject-Id", "X-User-Id", "X-Tenant", "X-Tenant-Id", "X-Consumer-Tenant-Id" };
            if (query.Count != allowed.Length
                || query.Any(x => !allowed.Contains(x.Key, StringComparer.Ordinal) || x.Value.Count != 1)
                || controller.Request.Headers.Keys.Any(x => spoofedHeaders.Contains(x, StringComparer.OrdinalIgnoreCase))
                || !CanonicalGuid(query["subject"].ToString(), out var subject))
                return controller.BadRequest(new { error = "A single canonical subject and selected tenant are required." });
            var store = new ConsumerApplicationCatalog(database, access, configuration, time);
            if (!products) return controller.Ok(store.Tenants(subject.ToString("D"), AllowsDelegation));
            if (!CanonicalGuid(query["tenant"].ToString(), out var tenant))
                return controller.BadRequest(new { error = "A selected consumer tenant is required." });
            return controller.Ok(store.Products(subject.ToString("D"), tenant, AllowsDelegation));
        }
        catch (UnauthorizedAccessException) { return controller.Forbid("PlatformBearer"); }
        catch (Exception error) when (error is SqliteException or InvalidDataException or JsonException
            or ArgumentException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            return controller.StatusCode(503, new { error = "Consumer application catalog is unavailable." });
        }
    }
    private static bool CanonicalGuid(string text, out Guid value)
        => Guid.TryParseExact(text, "D", out value) && value != Guid.Empty && text == value.ToString("D");
}

[ApiController, Route("api/platform/application-tenants")]
[Authorize(AuthenticationSchemes = "PlatformBearer")]
public sealed class ConsumerApplicationsApi(PlatformRegistry registry, FabricDatabase database,
    IConfiguration configuration, TimeProvider time, PersistentAccessManagement access) : ControllerBase
{
    [HttpGet]
    public IActionResult Tenants() => ConsumerApplicationResponses.Read(this, false, registry, database, configuration, time, access);
}
