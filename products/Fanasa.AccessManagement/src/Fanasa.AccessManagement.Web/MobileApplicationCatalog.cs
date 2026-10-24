using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.ConsumerCatalog;
using Fanasa.AccessManagement.Web.Domain.Access;
using Fanasa.AccessManagement.Web.Persistence;
using Fanasa.AccessManagement.Web.Platform;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace Fanasa.AccessManagement.Web.MobileCatalog;

/// <summary>Self-only public-client access; never delegates a caller-supplied subject.</summary>
public static class MobileCatalogAuthorization
{
    public const string ReadScope = "platform.catalog.self";
    public const string Audience = "fanasa-access-management-web";
    public const string AndroidClientId = "fanasa-mobile-android";
    public const string AndroidRedirectUri = "net.fanasa.portal.mobile:/oauth2redirect";

    public static bool Configured(IConfiguration configuration)
        => configuration.GetValue<bool>("Platform:MobileCatalog:Enabled")
            && configuration["Platform:MobileCatalog:ClientId"] == AndroidClientId
            && !string.IsNullOrWhiteSpace(configuration["Platform:MobileCatalog:ClientProductKey"]);

    public static bool AllowsAndroidRedirect(RegisterClientRequest request, string redirect)
        => request.Key == AndroidClientId && request.ClientType == "public"
            && request.RedirectUris is { Count: 1 } && redirect == AndroidRedirectUri
            && request.AllowedScopes?.Contains(ReadScope, StringComparer.Ordinal) == true;

    public static bool CanonicalGuid(string? text, out Guid value)
        => Guid.TryParseExact(text, "D", out value) && value != Guid.Empty && text == value.ToString("D");

    public static bool Allows(ClaimsPrincipal user, IConfiguration configuration, PlatformRegistry registry,
        PersistentAccessManagement access, TimeProvider time, out string subject)
    {
        subject = "";
        if (!Configured(configuration)) return false;
        var identities = user.Identities.Where(x => x.IsAuthenticated).ToArray();
        if (identities.Length != 1) return false;
        var identity = identities[0];
        string? Single(string name)
        {
            var values = identity.FindAll(name).Select(x => x.Value).Take(2).ToArray();
            return values.Length == 1 ? values[0] : null;
        }
        var client = Single("azp");
        var signedSubject = Single("sub");
        var scope = Single("scope");
        if (client != configuration["Platform:MobileCatalog:ClientId"]
            || Single("aud") != Audience || !CanonicalGuid(signedSubject, out _)
            || scope is null || !scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(ReadScope, StringComparer.Ordinal))
            return false;
        bool NumericDate(string name, out long value)
        {
            var text = Single(name);
            return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value)
                && value >= 0 && value.ToString(CultureInfo.InvariantCulture) == text;
        }
        var now = time.GetUtcNow().ToUnixTimeSeconds();
        var alternateClients = identity.FindAll("client_id").ToArray();
        var active = identity.FindAll("active").ToArray();
        if (!NumericDate("exp", out var expiry) || now >= expiry
            || identity.HasClaim(x => x.Type == "nbf") && (!NumericDate("nbf", out var start) || now < start)
            || alternateClients.Length > 1 || alternateClients.Any(x => x.Value != client)
            || active.Length > 1 || active.Any(x => !x.Value.Equals("true", StringComparison.OrdinalIgnoreCase)))
            return false;
        var products = registry.Products()
            .Where(x => x.Key == configuration["Platform:MobileCatalog:ClientProductKey"]).Take(2).ToArray();
        if (products.Length != 1 || products[0].TenantId is not { } owner
            || access.GetTenants().Count(x => x.Id == owner && x.IsActive) != 1) return false;
        var registrations = access.GetClients(products[0].Id).Where(x => x.Key == client).Take(2).ToArray();
        if (registrations.Length != 1 || !registrations[0].IsActive || registrations[0].ClientType != "public"
            || registrations[0].AllowedScopes?.Contains(ReadScope, StringComparer.Ordinal) != true
            || registrations[0].RedirectUris?.SequenceEqual([AndroidRedirectUri], StringComparer.Ordinal) != true)
            return false;
        subject = signedSubject!;
        return true;
    }
}

[ApiController, Route("api/mobile/catalog")]
[Authorize(AuthenticationSchemes = "PlatformBearer")]
public sealed class MobileApplicationsApi(PlatformRegistry registry, FabricDatabase database,
    IConfiguration configuration, TimeProvider time, PersistentAccessManagement access) : ControllerBase
{
    [HttpGet("tenants")]
    public IActionResult Tenants() => Read(null);

    [HttpGet("tenants/{tenantId}/apps")]
    public IActionResult Applications(string tenantId) => Read(tenantId);

    private IActionResult Read(string? tenantId)
    {
        Response.Headers.CacheControl = "no-store";
        if (!MobileCatalogAuthorization.Configured(configuration))
            return StatusCode(503, new { error = "Mobile application catalog is not configured." });
        try
        {
            bool Authorized() => MobileCatalogAuthorization.Allows(User, configuration, registry, access, time, out _);
            if (!MobileCatalogAuthorization.Allows(User, configuration, registry, access, time, out var subject))
                return Forbid("PlatformBearer");
            var spoofedHeaders = new[] { "X-Subject", "X-Subject-Id", "X-User-Id", "X-Tenant", "X-Tenant-Id", "X-Consumer-Tenant-Id" };
            if (Request.Query.Count != 0
                || Request.Headers.Keys.Any(x => spoofedHeaders.Contains(x, StringComparer.OrdinalIgnoreCase)))
                return BadRequest(new { error = "Identity is taken only from the access token; query parameters and identity headers are not accepted." });
            var store = new ConsumerApplicationCatalog(database, access, configuration, time);
            if (tenantId is null) return Ok(store.Tenants(subject, Authorized));
            if (!MobileCatalogAuthorization.CanonicalGuid(tenantId, out var tenant))
                return BadRequest(new { error = "A canonical selected consumer tenant is required." });
            return Ok(store.Products(subject, tenant, Authorized));
        }
        catch (UnauthorizedAccessException) { return Forbid("PlatformBearer"); }
        catch (Exception error) when (error is SqliteException or InvalidDataException or JsonException
            or ArgumentException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            return StatusCode(503, new { error = "Mobile application catalog is unavailable." });
        }
    }
}
