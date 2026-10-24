using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Fanasa.AccessManagement.Web.Metering;
using Fanasa.AccessManagement.Web.Organization;
using Fanasa.AccessManagement.Web.Persistence;
using Fanasa.AccessManagement.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fanasa.AccessManagement.Web.Api;

[ApiController, Route("api/organization-hr/v1/tenants/{tenantId:guid}")]
[Authorize(AuthenticationSchemes = OrganizationHrSecurity.Scheme)]
public sealed class OrganizationHrApi(OrganizationStore store, FabricDatabase database, OrganizationHrBoundary boundary,
    OrganizationUsageMetering metering, TemporalAccessGate? temporalAccess = null, TemplateOrganizationStore? examples = null) : ControllerBase
{
    public const int MaximumBodyBytes = 65536;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    { PropertyNameCaseInsensitive = false, NumberHandling = JsonNumberHandling.Strict, MaxDepth = 16 };
    private static readonly HashSet<string> Fields = new(StringComparer.Ordinal)
    { "expectedRevision", "operation", "id", "name", "code", "parentId", "kind", "unitId", "capacity", "positionId", "subject", "from", "to", "reason", "roleId", "requestId" };
    private static readonly HashSet<string> Operations = new(StringComparer.Ordinal)
    { "role.save", "unit.save", "unit.archive", "position.save", "position.archive", "appointment.add", "appointment.end" };

    // Only the private Gateway auth subrequest routes here; business requests repeat this check.
    [HttpGet("gateway-access")]
    public async Task<IActionResult> GatewayAccess(Guid tenantId, string operation, CancellationToken cancellationToken)
    {
        if (!await boundary.IsGatewayAsync(HttpContext, cancellationToken)) return Forbid(OrganizationHrSecurity.Scheme);
        if (operation is not ("read" or "write")) return BadRequest();
        return database.ReadSnapshot<IActionResult>(() => boundary.Allows(User, tenantId, operation == "write")
            && boundary.IsGatewayCurrent(HttpContext) ? NoContent() : Forbid(OrganizationHrSecurity.Scheme));
    }

    [HttpGet("chart"), TemporalPermission("organization.read")]
    public Task<IActionResult> Chart(Guid tenantId, CancellationToken cancellationToken) => Read(tenantId, null, cancellationToken);

    [HttpGet("history/{revision:long}"), TemporalPermission("organization.read")]
    public Task<IActionResult> History(Guid tenantId, long revision, CancellationToken cancellationToken) => Read(tenantId, revision, cancellationToken);

    private async Task<IActionResult> Read(Guid tenantId, long? revision, CancellationToken cancellationToken)
    {
        if (!await boundary.IsGatewayAsync(HttpContext, cancellationToken)) return Forbid(OrganizationHrSecurity.Scheme);
        if (revision < 1) return BadRequest(new { error = "Revision must be positive." });
        try
        {
            var result = database.ReadSnapshot(() =>
            {
                RequireAccess(tenantId, false);
                var chart = revision.HasValue ? store.History(tenantId, revision.Value) : store.Read(tenantId);
                RequireAccess(tenantId, false);
                return chart;
            });
            if (temporalAccess?.IsEnabled(tenantId) == true && !temporalAccess.Allows(tenantId, OrgAuthorization.Subject(User), "organization.read"))
                throw new UnauthorizedAccessException();
            return Ok(result);
        }
        catch (UnauthorizedAccessException) { return Forbid(OrganizationHrSecurity.Scheme); }
        catch (KeyNotFoundException) { return NotFound(); }
    }

    [HttpPost("commands"), RequestSizeLimit(MaximumBodyBytes), TemporalPermission("organization.write")]
    public async Task<IActionResult> Execute(Guid tenantId, CancellationToken cancellationToken)
    {
        if (!await boundary.IsGatewayAsync(HttpContext, cancellationToken)) return Forbid(OrganizationHrSecurity.Scheme);
        if (!boundary.Allows(User, tenantId, true)) return Forbid(OrganizationHrSecurity.Scheme);
        if (TemplateOrganizationWriteGuard.Reject(examples, tenantId) is { } readOnly) return readOnly;
        if (!MediaTypeHeaderValue.TryParse(Request.ContentType, out var contentType)
            || !string.Equals(contentType.MediaType, "application/json", StringComparison.OrdinalIgnoreCase)
            || contentType.CharSet is not null && !contentType.CharSet.Trim('"').Equals("utf-8", StringComparison.OrdinalIgnoreCase)
            || Request.Headers.ContainsKey("Content-Encoding"))
            return StatusCode(StatusCodes.Status415UnsupportedMediaType);
        if (Request.ContentLength > MaximumBodyBytes) return StatusCode(StatusCodes.Status413PayloadTooLarge);
        try
        {
            using var buffer = new MemoryStream(); var chunk = new byte[8192]; int count;
            while ((count = await Request.Body.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + count > MaximumBodyBytes) return StatusCode(StatusCodes.Status413PayloadTooLarge);
                buffer.Write(chunk, 0, count);
            }
            using var document = JsonDocument.Parse(buffer.ToArray(), new() { MaxDepth = 16 }); var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new ArgumentException("A JSON command object is required.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in root.EnumerateObject())
                if (!Fields.Contains(field.Name) || !seen.Add(field.Name)) throw new ArgumentException("Unknown or duplicate command field.");
            foreach (var name in new[] { "expectedRevision", "operation", "reason", "requestId" })
                if (!seen.Contains(name)) throw new ArgumentException("Missing required command field.");
            if (root.GetProperty("expectedRevision").ValueKind != JsonValueKind.Number
                || !root.GetProperty("expectedRevision").TryGetInt64(out var expected) || expected < 0)
                throw new ArgumentException("expectedRevision must be a nonnegative integer.");
            foreach (var name in new[] { "from", "to" })
                if (root.TryGetProperty(name, out var time) && time.ValueKind != JsonValueKind.Null
                    && (time.ValueKind != JsonValueKind.String || !Regex.IsMatch(time.GetString()!,
                        @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(?:\.\d{1,7})?(?:Z|[+-]\d{2}:\d{2})$")))
                    throw new ArgumentException("from/to require an ISO timestamp with an explicit offset or Z.");
            var command = root.Deserialize<OrgCommand>(JsonOptions) ?? throw new ArgumentException("Invalid command.");
            if (!Operations.Contains(command.Operation) || string.IsNullOrWhiteSpace(command.Reason) || command.Reason.Length > 1000
                || command.Name?.Length > 200 || command.Code?.Length > 80 || command.Capacity is < 0 or > 10000)
                throw new ArgumentException("Unsupported operation or invalid command bounds.");
            if (!Guid.TryParseExact(command.RequestId, "D", out var requestId) || requestId == Guid.Empty
                || requestId.ToString("D") != command.RequestId || !Request.Headers.TryGetValue("Idempotency-Key", out var keys)
                || keys.Count != 1 || keys[0] != command.RequestId)
                throw new ArgumentException("One UUID Idempotency-Key must equal requestId.");
            if (metering.Enabled && command.Operation == "unit.save") return Forbid(OrganizationHrSecurity.Scheme);
            return Ok(database.Transaction(() =>
            {
                RequireAccess(tenantId, true);
                if (command.Operation == "appointment.add" && !boundary.CanAppoint(tenantId, command.Subject))
                    throw new ArgumentException("Appointment requires an existing active tenant member other than the HR service subject.");
                var result = store.Execute(tenantId, command, User.FindFirst("sub")!.Value, User);
                if (command.Operation == "appointment.add" && !boundary.CanAppoint(tenantId, command.Subject)) throw new UnauthorizedAccessException();
                RequireAccess(tenantId, true);
                return result;
            }));
        }
        catch (UnauthorizedAccessException) { return Forbid(OrganizationHrSecurity.Scheme); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (JsonException) { return BadRequest(new { error = "Invalid JSON command." }); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }
        catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
        { return StatusCode(StatusCodes.Status413PayloadTooLarge); }
    }
    private void RequireAccess(Guid tenant, bool write)
    {
        if (!boundary.Allows(User, tenant, write) || !boundary.IsGatewayCurrent(HttpContext)) throw new UnauthorizedAccessException();
    }
}

