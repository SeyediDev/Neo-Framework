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

        if (consumerTenantId == Guid.Empty)
            return BadRequest(new { error = "A nonempty consumer tenant ID is required." });

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
        => new ConsumerBillingAuthorization(HttpContext, access, registry, clock)
            .Allows(consumer, productKey, client, scope);

    private string? Single(string name)
    {
        var claims = User.FindAll(name).ToArray();
        return claims.Length == 1 ? claims[0].Value : null;
    }
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
