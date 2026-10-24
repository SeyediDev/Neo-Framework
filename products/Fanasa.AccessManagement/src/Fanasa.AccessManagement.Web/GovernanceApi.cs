using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Governance;
using Fanasa.AccessManagement.Web.Organization;
using Fanasa.AccessManagement.Web.Security;
using Fanasa.AccessManagement.Web.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fanasa.AccessManagement.Web.Api;

[ApiController, Authorize, Route("api/governance/tenants/{tenantId:guid}")]
public sealed class GovernanceApi(AccessPolicyStore policies, IdentityLifecycle lifecycle, PersistentAccessManagement access, OrganizationStore organization, FabricDatabase database,
    OrganizationCalendarStore? calendars = null, TemplateOrganizationStore? examples = null, TemporalAccessGate? temporalAccess = null, PipStore? pip = null,
    PipDiagnostics? diagnostics = null) : ControllerBase
{
    private bool Readable(Guid tenant) => Admin && examples?.IsTemplateTenant(tenant) == true
        || TenantAuthorization.Allows(User, tenant, "tenancy.read") && TenantAuthorization.Allows(User, tenant, "organization.read");
    private bool Admin => User.Identity?.IsAuthenticated == true && User.HasClaim("permission", "platform.admin");
    [HttpGet]
    public IActionResult Read(Guid tenantId)
    {
        if (!Readable(tenantId)) return Forbid();
        return Execute(() => {
            var chart = Chart(tenantId, out var restricted);
            return new { policies = policies.Read(tenantId), requests = lifecycle.List(tenantId), members = access.GetUsers(tenantId), chart, chartRestrictedByTime = restricted, permissions = TenantGrant.Permissions,
            calendars = (calendars ?? new OrganizationCalendarStore(database, access)).Read(tenantId), liveEnforcementAvailable = temporalAccess?.IsEnabled(tenantId) == true,
            pipSources = Admin || TenantAuthorization.Allows(User, tenantId, "tenancy.manage") ? (pip?.ListSources(tenantId) ?? []) : [],
            liveEnforcedPermissions = TemporalAccessGate.SupportedPermissions }; });
    }
    private OrgState Chart(Guid tenant, out bool restricted)
    {
        restricted = false;
        if (Admin && examples?.IsTemplateTenant(tenant) == true) return examples.Read().Chart;
        bool Allowed() => temporalAccess?.IsEnabled(tenant) != true || temporalAccess.Allows(tenant, OrgAuthorization.Subject(User), "organization.read");
        if (!Allowed()) { restricted = true; return new(tenant, 0, [], [], [], [], []); }
        try
        {
            var chart = organization.Read(tenant);
            if (!Allowed()) { restricted = true; return new(tenant, 0, [], [], [], [], []); }
            return chart;
        }
        catch (UnauthorizedAccessException)
        {
            // Permit safe offboarding requests after SaaS expiry without exposing subscribed chart content.
            var revision = database.Read<OrgState>("organization", tenant.ToString())?.Revision ?? 0;
            return new(tenant, revision, [], [], [], [], []);
        }
    }
    [HttpPost("patterns"), ValidateAntiForgeryToken]
    public IActionResult Draft(Guid tenantId, PatternCommand command)
    {
        if (!Admin) return Forbid();
        if (examples?.IsTemplateTenant(tenantId) == true) return Conflict(new { error = "سیاست مرجع آموزشی فقط‌خواندنی است؛ یک نسخه در سازمان خود بسازید." });
        return Execute(() => policies.Draft(tenantId, command, OrgAuthorization.Subject(User)));
    }
    [HttpPost("patterns/{id:guid}"), ValidateAntiForgeryToken]
    public IActionResult Pattern(Guid tenantId, Guid id, PatternTransition command)
    {
        if (!Admin) return Forbid();
        if (examples?.IsTemplateTenant(tenantId) == true) return Conflict(new { error = "پیش‌نویس‌های سازمان الگو برای آموزش‌اند و در همین سازمان منتشر نمی‌شوند." });
        return Execute(() => policies.Transition(tenantId, id, command.ExpectedRevision, command.Operation, OrgAuthorization.Subject(User), command.Reason));
    }
    [HttpPost("simulate"), ValidateAntiForgeryToken]
    public IActionResult Simulate(Guid tenantId, DecisionSimulation command)
    {
        if (!Readable(tenantId) || (!Admin && (command.Subject != OrgAuthorization.Subject(User) || command.IncludeDrafts))) return Forbid();
        return Execute(() => database.ReadSnapshot(() =>
        {
            var at = (command.At ?? DateTimeOffset.UtcNow).ToUniversalTime();
            var result = command.IncludeDrafts ? policies.EvaluateDraftsForSimulation(tenantId, command.Subject, command.Permission, at)
                : policies.Evaluate(tenantId, command.Subject, command.Permission, at);
            var state = policies.Read(tenantId);
            var canExplain = Admin || TenantAuthorization.Allows(User, tenantId, "tenancy.manage");
            var candidates = canExplain ? state.Patterns.Where(x => x.Permission == command.Permission
                && (x.Status == "published" || command.IncludeDrafts && x.Status == "draft")).ToArray() : [];
            var sources = new List<object>();
            if (diagnostics is not null)
                foreach (var sourceId in candidates.Where(x => x.PipSourceId.HasValue).Select(x => x.PipSourceId!.Value).Distinct().Take(50))
                    try { sources.Add(diagnostics.StatusAt(tenantId, sourceId, command.Subject, at)); }
                    catch (KeyNotFoundException) { sources.Add(new { sourceId, status = "missing", usable = false, error = "source-not-found" }); }
            return new { simulation = true, enforced = false, includesDrafts = command.IncludeDrafts, result,
                explanation = new { evaluatedAt = at, policyRevision = state.Revision, algorithm = state.Algorithm,
                    configurationOnly = true, historicalSnapshotsReconstructed = false,
                    policies = candidates.Select(x => new { x.Id, x.Name, x.Template, x.Effect, x.Status, x.Enforce,
                        x.AttributeName, x.AttributeValue, x.AttributeOperator, x.PipSourceId, x.CalendarId, x.CalendarMode,
                        x.StartsAt, x.EndsAt, x.UtcStartHour, x.UtcEndHour, decisionReferenced = result.Policies.Contains(x.Id) }), sources } };
        }));
    }
    [HttpPost("requests"), ValidateAntiForgeryToken]
    public IActionResult Submit(Guid tenantId, LifecycleCommand command)
    {
        if (!Readable(tenantId)) return Forbid();
        if (examples?.IsTemplateTenant(tenantId) == true) return Conflict(new { error = "سازمان الگو فقط‌خواندنی است." });
        return Execute(() => lifecycle.Submit(tenantId, command, OrgAuthorization.Subject(User)));
    }
    [HttpPost("requests/{id:guid}"), ValidateAntiForgeryToken]
    public IActionResult TransitionRequest(Guid tenantId, Guid id, LifecycleTransition command)
    {
        if (!Readable(tenantId)) return Forbid();
        if (examples?.IsTemplateTenant(tenantId) == true) return Conflict(new { error = "سازمان الگو فقط‌خواندنی است." });
        return Execute(() => command.Operation switch
        {
            "approve" => lifecycle.Review(tenantId, id, command.Version, true, OrgAuthorization.Subject(User), command.Reason),
            "reject" => lifecycle.Review(tenantId, id, command.Version, false, OrgAuthorization.Subject(User), command.Reason),
            "cancel" => lifecycle.Cancel(tenantId, id, command.Version, OrgAuthorization.Subject(User), command.Reason),
            _ => throw new ArgumentException("Execution is not enabled; only request and review are available.")
        });
    }
    private IActionResult Execute(Func<object> action)
    {
        try { return Ok(action()); }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException error) { return BadRequest(new { error = error.Message }); }
        catch (InvalidOperationException error) { return Conflict(new { error = error.Message }); }
    }
}
public sealed record PatternTransition(long ExpectedRevision, string Operation, string Reason);
public sealed record LifecycleTransition(long Version, string Operation, string Reason);
public sealed record DecisionSimulation(string Subject, string Permission, DateTimeOffset? At, bool IncludeDrafts = false);
