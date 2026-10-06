using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Governance;
using Fanasa.AccessManagement.Web.Organization;
using Fanasa.AccessManagement.Web.Security;
using Fanasa.AccessManagement.Web.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fanasa.AccessManagement.Web.Api;

[ApiController, Authorize, Route("api/governance/tenants/{tenantId:guid}")]
public sealed class GovernanceApi(AccessPolicyStore policies, IdentityLifecycle lifecycle, PersistentAccessManagement access, OrganizationStore organization, FabricDatabase database) : ControllerBase
{
    private bool Readable(Guid tenant) => TenantAuthorization.Allows(User, tenant, "tenancy.read") && TenantAuthorization.Allows(User, tenant, "organization.read");
    private bool Admin => User.Identity?.IsAuthenticated == true && User.HasClaim("permission", "platform.admin");
    [HttpGet]
    public IActionResult Read(Guid tenantId)
    {
        if (!Readable(tenantId)) return Forbid();
        return Execute(() => new { policies = policies.Read(tenantId), requests = lifecycle.List(tenantId), members = access.GetUsers(tenantId), chart = Chart(tenantId), permissions = TenantGrant.Permissions });
    }
    private OrgState Chart(Guid tenant)
    {
        try { return organization.Read(tenant); }
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
        return Execute(() => policies.Draft(tenantId, command, OrgAuthorization.Subject(User)));
    }
    [HttpPost("patterns/{id:guid}"), ValidateAntiForgeryToken]
    public IActionResult Pattern(Guid tenantId, Guid id, PatternTransition command)
    {
        if (!Admin) return Forbid();
        return Execute(() => policies.Transition(tenantId, id, command.ExpectedRevision, command.Operation, OrgAuthorization.Subject(User), command.Reason));
    }
    [HttpPost("simulate"), ValidateAntiForgeryToken]
    public IActionResult Simulate(Guid tenantId, DecisionSimulation command)
    {
        if (!Readable(tenantId) || (!Admin && command.Subject != OrgAuthorization.Subject(User))) return Forbid();
        return Execute(() => new { simulation = true, enforced = false, result = policies.Evaluate(tenantId, command.Subject, command.Permission, command.At ?? DateTimeOffset.UtcNow) });
    }
    [HttpPost("requests"), ValidateAntiForgeryToken]
    public IActionResult Submit(Guid tenantId, LifecycleCommand command)
    {
        if (!Readable(tenantId)) return Forbid();
        return Execute(() => lifecycle.Submit(tenantId, command, OrgAuthorization.Subject(User)));
    }
    [HttpPost("requests/{id:guid}"), ValidateAntiForgeryToken]
    public IActionResult TransitionRequest(Guid tenantId, Guid id, LifecycleTransition command)
    {
        if (!Readable(tenantId)) return Forbid();
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
public sealed record DecisionSimulation(string Subject, string Permission, DateTimeOffset? At);
