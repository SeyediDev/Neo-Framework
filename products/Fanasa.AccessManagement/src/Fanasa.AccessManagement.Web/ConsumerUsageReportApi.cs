using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Metering;
using Fanasa.AccessManagement.Web.Persistence;
using Fanasa.AccessManagement.Web.Platform;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.IdentityModel.Tokens;

namespace Fanasa.AccessManagement.Web.Api;

public sealed record ConsumerUsageReportEnvelope(string Subject, ConsumerUsageReportBody Report);

public sealed record ConsumerUsageReportBody(
    Guid ConsumerTenantId,
    DateTimeOffset From,
    DateTimeOffset To,
    DateTimeOffset AsOf,
    string Status,
    string Basis,
    string? Currency,
    string? TotalAmount,
    IReadOnlyList<ConsumerUsageItem> Items);

/// <summary>
/// Read-only Portal transport. The existing PlatformBearer scheme validates
/// signature, issuer and API audience. No new identity or scope is provisioned.
/// </summary>
[ApiController, Authorize(AuthenticationSchemes = "PlatformBearer")]
[Route("api/accounting/v1/consumers/{consumerTenantId:guid}/usage-report")]
public sealed class ConsumerUsageReportApi(
    FabricDatabase database,
    PersistentAccessManagement access,
    PlatformRegistry registry,
    IConfiguration configuration,
    TimeProvider clock) : ControllerBase
{
    private static readonly Regex UtcTimestamp = new(
        @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|\+00:00)$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    [HttpGet]
    public IActionResult Read(Guid consumerTenantId)
    {
        Response.Headers.CacheControl = "no-store";

        var client = configuration["ConsumerUsageReport:PortalClientId"];
        var productKey = configuration["ConsumerUsageReport:PortalProductKey"];
        var scope = configuration["ConsumerUsageReport:Scope"];

        if (!configuration.GetValue<bool>("ConsumerUsageReport:Enabled")
            || !Label(client) || !Label(productKey) || !Label(scope)
            || scope!.Any(char.IsWhiteSpace))
            return Unavailable();

        if (Request.Query.Count != 2
            || Request.Query.Keys.Any(x => x is not ("from" or "to"))
            || Request.Headers.ContainsKey("X-Subject")
            || Request.Headers.ContainsKey("X-User-Id")
            || !TryUtc("from", out var from)
            || !TryUtc("to", out var to)
            || from >= to
            || to - from >= TimeSpan.FromDays(366))
            return BadRequest(new
            {
                error = "A UTC half-open period shorter than 366 days is required."
            });

        try
        {
            var report = new ConsumerUsageReportStore(database, clock).Read(
                consumerTenantId, from, to,
                () => Allows(consumerTenantId, productKey!, client!, scope!));

            // Recheck outside the completed snapshot so revocations committed while
            // projecting are observed before returning the report.
            if (!Allows(consumerTenantId, productKey!, client!, scope!))
                return Forbid("PlatformBearer");

            // ReasonCode and all raw ledger/evidence fields are deliberately omitted.
            return Ok(new ConsumerUsageReportEnvelope(Single("sub")!, new(
                report.ConsumerTenantId,
                report.From,
                report.To,
                report.AsOf,
                report.Status,
                report.Basis,
                report.Currency,
                report.TotalAmount,
                report.Items)));
        }
        catch (UnauthorizedAccessException)
        {
            return Forbid("PlatformBearer");
        }
        catch (Exception error) when (
            error is SqliteException or IOException or InvalidDataException
                or JsonException or InvalidOperationException)
        {
            return Unavailable();
        }
    }

    private bool Allows(Guid consumer, string productKey, string client, string scope)
    {
        if (consumer == Guid.Empty || !HasHumanCredential(client, scope))
            return false;

        var subject = Single("sub")!;
        var product = registry.Products().SingleOrDefault(x => x.Key == productKey);

        if (product is null
            || product.TenantId is not { } owner
            || access.GetTenants().Count(x => x.Id == owner && x.IsActive) != 1
            || !access.GetTenants().Any(x => x.Id == consumer && x.IsActive)
            || !access.GetUsers(consumer).Any(x =>
                x.IsActive && x.KeycloakSubject == subject))
            return false;

        var clients = access.GetClients(product.Id).Where(x => x.Key == client).Take(2).ToArray();
        if (clients.Length != 1 || !clients[0].IsActive
            || clients[0].AllowedScopes?.Contains(scope, StringComparer.Ordinal) != true)
            return false;

        var grants = access.GetGrants(subject);

        // Recheck signed timing after authoritative reads. The store invokes this
        // boundary again after projection and before returning its snapshot.
        return HasHumanCredential(client, scope)
            && grants.Any(x =>
                x.TenantId == consumer
                && x.Permission == "billing.read"
                && (!x.ExpiresAt.HasValue || x.ExpiresAt > clock.GetUtcNow()));
    }

    // Activation requires the owning Keycloak realm to verify that sid/auth_time
    // occur only on real human-session tokens for this exact Portal client.
    // Client-credentials tokens lack that session in this supported contract.
    private bool HasHumanCredential(string client, string scope)
    {
        if (User.Identity?.IsAuthenticated != true || !CanonicalPayload())
            return false;

        var now = clock.GetUtcNow().ToUnixTimeSeconds();
        var tokenScope = Single("scope");

        if (!CanonicalGuid(Single("sub"))
            || !CanonicalGuid(Single("sid"))
            || Single("azp") != client
            || tokenScope is null
            || !tokenScope.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Contains(scope, StringComparer.Ordinal)
            || User.FindAll("client_id").Any(x => x.Value != client)
            || User.FindAll("active").Any(x =>
                !x.Value.Equals("true", StringComparison.OrdinalIgnoreCase))
            || User.FindAll("grant_type").Concat(User.FindAll("gty"))
                .Any(x => x.Value == "client_credentials")
            || !NumericDate("exp", out var expiry)
            || now >= expiry
            || !NumericDate("auth_time", out var authenticated)
            || authenticated > now
            || authenticated >= expiry)
            return false;

        return !User.HasClaim(x => x.Type == "nbf")
            || NumericDate("nbf", out var start) && now >= start;
    }

    // This checks only the shape of the bearer token already authenticated by
    // PlatformBearer. Identity/permissions are taken exclusively from User.
    // It prevents JWT claim normalization from accepting duplicate/noncanonical
    // timing or identity values.
    private bool CanonicalPayload()
    {
        var headers = Request.Headers.Authorization;
        if (headers.Count != 1
            || headers[0] is not { Length: <= 16384 } header
            || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return false;

        var parts = header[7..].Split('.');
        if (parts.Length != 3) return false;

        try
        {
            using var document = JsonDocument.Parse(
                Base64UrlEncoder.DecodeBytes(parts[1]),
                new JsonDocumentOptions { MaxDepth = 16 });

            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in document.RootElement.EnumerateObject())
                if (!names.Add(item.Name)) return false;

            foreach (var name in new[] { "sub", "azp", "scope", "iss", "sid" })
                if (!document.RootElement.TryGetProperty(name, out var item)
                    || item.ValueKind != JsonValueKind.String)
                    return false;

            foreach (var name in new[] { "client_id", "grant_type", "gty" })
                if (document.RootElement.TryGetProperty(name, out var item)
                    && item.ValueKind != JsonValueKind.String)
                    return false;

            foreach (var name in new[] { "exp", "auth_time" })
                if (!document.RootElement.TryGetProperty(name, out var item)
                    || !CanonicalNumericDate(item))
                    return false;

            return !document.RootElement.TryGetProperty("nbf", out var start)
                || CanonicalNumericDate(start);
        }
        catch (Exception error) when (
            error is JsonException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    private static bool CanonicalNumericDate(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out var seconds)
        && seconds >= 0
        && seconds.ToString(CultureInfo.InvariantCulture) == value.GetRawText();

    private string? Single(string name)
    {
        var claims = User.FindAll(name).ToArray();
        return claims.Length == 1 ? claims[0].Value : null;
    }

    private bool NumericDate(string name, out long seconds)
    {
        var value = Single(name);
        seconds = 0;
        return value is not null
            && long.TryParse(value, NumberStyles.None,
                CultureInfo.InvariantCulture, out seconds)
            && seconds >= 0
            && seconds.ToString(CultureInfo.InvariantCulture) == value;
    }

    private static bool CanonicalGuid(string? value) =>
        Guid.TryParseExact(value, "D", out var id)
        && id != Guid.Empty
        && id.ToString("D") == value;

    private static bool Label(string? value) =>
        !string.IsNullOrWhiteSpace(value)
        && value.Length <= 200
        && !value.Any(char.IsControl)
        && value == value.Trim();

    private bool TryUtc(string name, out DateTimeOffset value)
    {
        value = default;
        var values = Request.Query[name];
        return values.Count == 1
            && values[0] is { Length: <= 40 } text
            && UtcTimestamp.IsMatch(text)
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out value)
            && value.Offset == TimeSpan.Zero;
    }

    private ObjectResult Unavailable() =>
        StatusCode(StatusCodes.Status503ServiceUnavailable, new
        {
            error = "Usage report is temporarily unavailable."
        });
}
