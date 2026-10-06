using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Domain.Access;
using Fanasa.AccessManagement.Web.Security;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Fanasa.AccessManagement.Web.Api;

/// <summary>Scopes the legacy Access surface without changing the central registry's controller contract.</summary>
public sealed class AccessBoundaryFilter(IAccessManagement access, IAntiforgery antiforgery) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.ActionDescriptor is not ControllerActionDescriptor descriptor || descriptor.ControllerTypeInfo.AsType() != typeof(AccessApi)) { await next(); return; }
        var user = context.HttpContext.User;
        if (user.Identity?.IsAuthenticated != true) { context.Result = new UnauthorizedResult(); return; }
        // The registry's own product discovery/registration policies remain authoritative.
        if (descriptor.ActionName is "Products" or "RegisterProduct") { await next(); return; }
        var admin = user.HasClaim("permission", "platform.admin");
        if (descriptor.ActionName == "Tenants")
        {
            context.Result = new OkObjectResult(access.GetTenants().Where(x => admin || TenantAuthorization.Allows(user, x.Id, "tenancy.read") || TenantAuthorization.Allows(user, x.Id, "organization.read") || TenantAuthorization.Allows(user, x.Id, "billing.read")).ToArray()); return;
        }
        Guid? tenant = context.ActionArguments.TryGetValue("tenantId", out var value) && value is Guid id ? id : null;
        if (context.ActionArguments.TryGetValue("request", out var request)) tenant ??= request switch
        {
            CreateSubscriptionRequest r => r.TenantId, AddTenantUserRequest r => r.TenantId,
            CreateRoleRequest r => r.TenantId, RecordUsageCommand r => r.TenantId,
            ContractLifecycleEvent r => r.TenantId, _ => null
        };
        var permission = descriptor.ActionName switch
        {
            "Users" or "Roles" or "Subscriptions" or "Entitlements" => "tenancy.read",
            "Subscribe" or "AddUser" or "CreateRole" => "tenancy.manage",
            "UsageSummary" or "Balance" => "billing.read", "Usage" => "billing.meter", _ => null
        };
        if (!admin && (tenant is null || permission is null || !TenantAuthorization.Allows(user, tenant.Value, permission))) { context.Result = new ForbidResult(); return; }
        if (HttpMethods.IsPost(context.HttpContext.Request.Method))
        {
            try { await antiforgery.ValidateRequestAsync(context.HttpContext); }
            catch (AntiforgeryValidationException) { context.Result = new BadRequestObjectResult(new { error = "Invalid CSRF token." }); return; }
        }
        await next();
    }
}
