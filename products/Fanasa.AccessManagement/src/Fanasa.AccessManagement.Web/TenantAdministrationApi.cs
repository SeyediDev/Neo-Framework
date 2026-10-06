using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Domain.Access;
using Fanasa.AccessManagement.Web.Organization;
using Fanasa.AccessManagement.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fanasa.AccessManagement.Web.Api;

[ApiController, Authorize, Route("api/tenancy/tenants/{tenantId:guid}")]
public sealed class TenantAdministrationApi(PersistentAccessManagement access) : ControllerBase
{
    private bool Allows(Guid tenant, bool write) => User.Identity?.IsAuthenticated == true &&
        (User.HasClaim("permission", "platform.admin") || TenantAuthorization.Allows(User, tenant, write ? "tenancy.manage" : "tenancy.read"));
    [HttpGet]
    public IActionResult Read(Guid tenantId)
    {
        if (!Allows(tenantId, false)) return Forbid();
        if (!access.GetTenants().Any(x => x.Id == tenantId && x.IsActive)) return NotFound();
        return Ok(new { users = access.GetUsers(tenantId), subscriptions = access.GetSubscriptions(tenantId), plans = access.GetPlans(), products = access.GetProducts().Select(x => new { x.Key, x.DisplayName }),
            grants = access.GetUsers(tenantId).SelectMany(x => access.GetGrants(x.KeycloakSubject)).Where(x => x.TenantId == tenantId) });
    }
    [HttpPost("members"), ValidateAntiForgeryToken]
    public IActionResult AddMember(Guid tenantId, TenantMemberCommand command) => Execute(tenantId, () => access.Audited(() => access.AddUser(new AddTenantUserRequest(tenantId, command.Subject, command.DisplayName)), OrgAuthorization.Subject(User), command.Reason, "TenantMemberAdded"));
    [HttpPost("members/{memberId:guid}"), ValidateAntiForgeryToken]
    public IActionResult Membership(Guid tenantId, Guid memberId, MembershipStateCommand command) => Execute(tenantId, () => access.SetMembership(tenantId, memberId, command.Active, OrgAuthorization.Subject(User), command.Reason));
    [HttpPost("subscriptions"), ValidateAntiForgeryToken]
    public IActionResult Subscribe(Guid tenantId, TenantSubscriptionCommand command) => Execute(tenantId, () => access.Audited(() => access.Subscribe(new CreateSubscriptionRequest(tenantId, command.ProductKey, command.PlanId, command.SeatLimit)), OrgAuthorization.Subject(User), command.Reason, "TenantSubscribed"));
    [HttpPost("plans"), ValidateAntiForgeryToken]
    public IActionResult Plan(Guid tenantId, TenantPlanCommand command)
    {
        if (!User.HasClaim("permission", "platform.admin")) return Forbid();
        return Execute(tenantId, () => access.Audited(() => access.RegisterPlan(new RegisterPlanRequest(command.ProductKey, command.Key, command.Name, command.Mode, command.Currency, 0, 0)), OrgAuthorization.Subject(User), command.Reason, "PricingPlanCreated"));
    }
    [HttpPost("/api/tenancy/tenants"), ValidateAntiForgeryToken]
    public IActionResult CreateTenant(TenantCreateCommand command)
    {
        if (User.Identity?.IsAuthenticated != true || !User.HasClaim("permission", "platform.admin")) return Forbid();
        try { return Ok(access.Audited(() => access.CreateTenant(command.Key, command.Name), OrgAuthorization.Subject(User), command.Reason, "TenantCreated")); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }
        catch (UnauthorizedAccessException) { return Forbid(); }
    }
    [HttpPost("subscriptions/{subscriptionId:guid}"), ValidateAntiForgeryToken]
    public IActionResult Subscription(Guid tenantId, Guid subscriptionId, SubscriptionStateCommand command) => Execute(tenantId, () => access.SetSubscription(tenantId, subscriptionId, command.Status, command.RenewsAt, OrgAuthorization.Subject(User), command.Reason));
    [HttpPost("grants"), ValidateAntiForgeryToken]
    public IActionResult Grant(Guid tenantId, TenantGrantCommand command)
    {
        // Assigning permissions is platform administration; tenant managers cannot escalate their own authority.
        if (!User.HasClaim("permission", "platform.admin")) return Forbid();
        return Execute(tenantId, () => { var grant = new TenantGrant(tenantId, command.Subject, command.Permission, command.ExpiresAt); access.SetGrant(grant, OrgAuthorization.Subject(User), command.Reason); return grant; });
    }
    private IActionResult Execute(Guid tenant, Func<object> action)
    {
        if (!Allows(tenant, true)) return Forbid();
        try { return Ok(action()); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }
    }
}
public sealed record TenantMemberCommand(string Subject, string? DisplayName, string Reason);
public sealed record MembershipStateCommand(bool Active, string Reason);
public sealed record TenantSubscriptionCommand(string ProductKey, Guid PlanId, int SeatLimit, string Reason);
public sealed record SubscriptionStateCommand(string Status, DateTimeOffset? RenewsAt, string Reason);
public sealed record TenantGrantCommand(string Subject, string Permission, DateTimeOffset? ExpiresAt, string Reason);
public sealed record TenantPlanCommand(string ProductKey, string Key, string Name, string Mode, string Currency, string Reason);
public sealed record TenantCreateCommand(string Key, string Name, string Reason);
