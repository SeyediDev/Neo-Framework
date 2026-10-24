using System.Globalization;
using System.Security.Claims;
using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Domain.Access;
using Fanasa.AccessManagement.Web.Governance;
using Fanasa.AccessManagement.Web.Organization;
using Fanasa.AccessManagement.Web.Persistence;
using Fanasa.AccessManagement.Web.Platform;
using Fanasa.AccessManagement.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fanasa.AccessManagement.Web.Api;

public sealed class OrganizationHrAttributeReader
{
    public string ClientId { get; set; } = "";
    public string ServiceSubject { get; set; } = "";
    public string Scope { get; set; } = "organization.hr.attributes.read";
    public Guid[] TenantIds { get; set; } = [];
}
public sealed class OrganizationHrAttributeOptions
{
    public OrganizationHrAttributeReader[] Readers { get; set; } = [];
    public int ObservationLifetimeSeconds { get; set; } = 60;
    public void Validate()
    {
        if (Readers is null || Readers.Length > 100 || ObservationLifetimeSeconds is < 5 or > 300)
            throw new InvalidOperationException("Invalid HR attribute reader configuration.");
        var clients = new HashSet<string>(StringComparer.Ordinal); var subjects = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reader in Readers)
            if (reader is null || string.IsNullOrWhiteSpace(reader.ClientId) || reader.ClientId.Length > 200 || reader.ClientId.Any(char.IsWhiteSpace)
                || !Guid.TryParseExact(reader.ServiceSubject, "D", out var subject) || subject == Guid.Empty || subject.ToString("D") != reader.ServiceSubject
                || string.IsNullOrWhiteSpace(reader.Scope) || reader.Scope.Length > 200 || reader.Scope.Any(char.IsWhiteSpace)
                || !clients.Add(reader.ClientId) || !subjects.Add(reader.ServiceSubject)
                || reader.TenantIds is not { Length: > 0 and <= 2000 } || reader.TenantIds.Contains(Guid.Empty)
                || reader.TenantIds.Distinct().Count() != reader.TenantIds.Length)
                throw new InvalidOperationException("HR attribute readers require exact unique clients, service subjects, scope and tenant allowlists.");
    }
}

public sealed class OrganizationHrAttributeBoundary(OrganizationHrAttributeOptions settings, OrganizationHrOptions hrSettings,
    PersistentAccessManagement access, PlatformRegistry registry, TimeProvider clock)
{
    public bool Allows(ClaimsPrincipal principal, Guid tenant)
    {
        if (!hrSettings.IsConfigured || principal.Identity?.IsAuthenticated != true || tenant == Guid.Empty) return false;
        var subjects = principal.FindAll("sub").Select(x => x.Value).ToArray();
        var clients = principal.FindAll("azp").Select(x => x.Value).ToArray();
        var scopes = principal.FindAll("scope").Select(x => x.Value).ToArray();
        var issuers = principal.FindAll("iss").Select(x => x.Value).ToArray();
        if (subjects.Length != 1 || clients.Length != 1 || scopes.Length != 1 || issuers.Length != 1 || issuers[0] != hrSettings.Authority
            || !principal.FindAll("aud").Any(x => x.Value == hrSettings.Audience)
            || principal.FindAll("client_id").Any(x => x.Value != clients[0])
            || principal.FindAll("active").Any(x => !x.Value.Equals("true", StringComparison.OrdinalIgnoreCase)) || !Live(principal)) return false;
        var reader = settings.Readers.SingleOrDefault(x => x.ClientId == clients[0] && x.ServiceSubject == subjects[0] && x.TenantIds.Contains(tenant));
        if (reader is null || !scopes[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(reader.Scope, StringComparer.Ordinal)) return false;
        var product = registry.Products().SingleOrDefault(x => x.Key == "organization-fabric");
        if (product is null || product.Audience != hrSettings.Audience
            || !access.GetClients(product.Id).Any(x => x.Key == reader.ClientId && x.IsActive && x.AllowedScopes.Contains(reader.Scope, StringComparer.Ordinal))
            || !access.GetTenants().Any(x => x.Id == tenant && x.IsActive)
            || !access.GetUsers(tenant).Any(x => x.KeycloakSubject == reader.ServiceSubject && x.IsActive)) return false;
        var now = clock.GetUtcNow();
        return access.GetSubscriptions(tenant).Any(x => x.ProductKey == "organization-fabric" && x.Status == "active"
            && x.StartedAt <= now && (!x.RenewsAt.HasValue || now < x.RenewsAt))
            && access.GetGrants(reader.ServiceSubject).Any(x => x.TenantId == tenant && x.Permission == "organization.read"
            && (!x.ExpiresAt.HasValue || x.ExpiresAt > now)) && Live(principal);
    }
    private bool Live(ClaimsPrincipal principal)
    {
        static bool Number(Claim[] claims, out long value)
        { value = 0; return claims.Length == 1 && long.TryParse(claims[0].Value, NumberStyles.None, CultureInfo.InvariantCulture, out value)
            && value >= 0 && value.ToString(CultureInfo.InvariantCulture) == claims[0].Value; }
        var now = clock.GetUtcNow().ToUnixTimeSeconds(); var starts = principal.FindAll("nbf").ToArray();
        return Number(principal.FindAll("exp").ToArray(), out var end) && now < end
            && (starts.Length == 0 || Number(starts, out var start) && start <= now);
    }
}

public sealed record OrganizationHrAttributeEnvelope(int SchemaVersion, Guid TenantId, string Subject, string SourceVersion,
    DateTimeOffset ObservedAt, DateTimeOffset ValidUntil, IReadOnlyDictionary<string, string> Attributes);

/// <summary>Membership and appointments are authoritative HR facts; they do not assert a legal employment relationship.</summary>
public sealed class OrganizationHrAttributeProjector(TimeProvider clock, OrganizationHrAttributeOptions options)
{
    public static PipAttributeDefinition[] Definitions => [new("org.member.active", "boolean"), new("org.assignment.active", "boolean"),
        new("org.assignment.count", "integer"), new("org.unit.code", "string", false), new("org.position.code", "string", false), new("org.role.code", "string", false)];

    public OrganizationHrAttributeEnvelope Project(OrgState state, TenantUser member)
    {
        if (member.TenantId != state.TenantId) throw new InvalidDataException("HR attribute membership is not correlated.");
        var now = clock.GetUtcNow(); // Called inside the shared Fabric read snapshot, after authorization.
        var until = now.AddSeconds(options.ObservationLifetimeSeconds);
        var appointments = state.Appointments.Where(x => x.Subject == member.KeycloakSubject).ToArray();
        foreach (var appointment in appointments)
        {
            if (appointment.To is { } end && end <= appointment.From) throw new InvalidDataException("Invalid HR appointment interval.");
            if (appointment.From > now && appointment.From < until) until = appointment.From;
            if (appointment.To is { } boundary && boundary > now && boundary < until) until = boundary;
        }
        var effective = member.IsActive ? appointments.Where(x => x.From <= now && (!x.To.HasValue || now < x.To)).ToArray() : [];
        var assignments = new List<(OrgPosition Position, OrgUnit Unit, OrgRole? Role)>();
        foreach (var appointment in effective)
        {
            var position = state.Positions.SingleOrDefault(x => x.Id == appointment.PositionId && x.Active);
            var unit = position is null ? null : state.Units.SingleOrDefault(x => x.Id == position.UnitId && x.Active);
            var role = position?.RoleId is { } roleId ? (state.Roles ?? []).SingleOrDefault(x => x.Id == roleId) : null;
            if (position is null || unit is null || position.RoleId.HasValue && role is null)
                throw new InvalidDataException("HR assignment refers to missing or inactive organization data.");
            assignments.Add((position, unit, role));
        }
        var attributes = new Dictionary<string, string>(StringComparer.Ordinal)
        { ["org.member.active"] = member.IsActive ? "true" : "false", ["org.assignment.active"] = assignments.Count > 0 ? "true" : "false",
            ["org.assignment.count"] = assignments.Count.ToString(CultureInfo.InvariantCulture) };
        if (assignments.Count == 1)
        {
            attributes["org.unit.code"] = assignments[0].Unit.Code; attributes["org.position.code"] = assignments[0].Position.Code;
            if (assignments[0].Role is { } role) attributes["org.role.code"] = role.Code;
        }
        foreach (var attribute in attributes) PipSchema.ValidateValue(Definitions.Single(x => x.Name == attribute.Key), attribute.Value);
        return new(2, state.TenantId, member.KeycloakSubject, $"hr-r{state.Revision}-{Guid.NewGuid():N}", now, until, attributes);
    }
}

[ApiController, Route("api/organization-hr/v1/tenants/{tenantId:guid}")]
[Authorize(AuthenticationSchemes = OrganizationHrSecurity.Scheme)]
public sealed class OrganizationHrAttributesApi(OrganizationStore store, FabricDatabase database, PersistentAccessManagement access,
    OrganizationHrBoundary gateway, OrganizationHrAttributeBoundary readers, OrganizationHrAttributeProjector projector) : ControllerBase
{
    // Separate bootstrap authorization: business chart/history routes retain their temporal/PIP gates.
    [HttpGet("attributes-access")]
    public Task<IActionResult> GatewayAccess(Guid tenantId, CancellationToken cancellationToken) => Read(tenantId, true, cancellationToken);
    [HttpGet("attributes")]
    public Task<IActionResult> Attributes(Guid tenantId, CancellationToken cancellationToken) => Read(tenantId, false, cancellationToken);

    private async Task<IActionResult> Read(Guid tenantId, bool authorizationOnly, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        if (!await gateway.IsGatewayAsync(HttpContext, cancellationToken)) return Forbid(OrganizationHrSecurity.Scheme);
        if (Request.Query.Any(x => x.Key is not ("subject" or "tenantId") || x.Value.Count != 1)
            || Request.Query.TryGetValue("tenantId", out var queryTenant) && queryTenant[0] != tenantId.ToString("D")) return BadRequest();
        var subject = Request.Query["subject"].ToString();
        if (!authorizationOnly && (!Guid.TryParseExact(subject, "D", out var id) || id == Guid.Empty || id.ToString("D") != subject)) return BadRequest();
        try
        {
            return database.ReadSnapshot<IActionResult>(() =>
            {
                Require();
                var chart = store.Read(tenantId); // Also verifies the live organization-fabric subscription.
                OrganizationHrAttributeEnvelope? envelope = null;
                if (!authorizationOnly)
                {
                    var member = access.GetUsers(tenantId).SingleOrDefault(x => x.KeycloakSubject == subject);
                    if (member is null) { Require(); return NotFound(); }
                    envelope = projector.Project(chart, member);
                }
                Require();
                return authorizationOnly ? NoContent() : Ok(envelope);
            });
        }
        catch (UnauthorizedAccessException) { return Forbid(OrganizationHrSecurity.Scheme); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidDataException) { return StatusCode(503, new { error = "hr-attributes-unavailable" }); }
        void Require() { if (!gateway.IsGatewayCurrent(HttpContext) || !readers.Allows(User, tenantId)) throw new UnauthorizedAccessException(); }
    }
}
