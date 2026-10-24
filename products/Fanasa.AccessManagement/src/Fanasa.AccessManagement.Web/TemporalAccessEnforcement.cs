using Fanasa.AccessManagement.Web.Governance;
using Fanasa.AccessManagement.Web.Organization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Fanasa.AccessManagement.Web.Security;

/// <summary>Additional restrictions on existing organization access. Disabled unless exact tenants are configured.</summary>
public sealed class TemporalAccessOptions
{
    public bool Enabled { get; init; }
    public Guid[] TenantIds { get; init; } = [];
    public void Validate()
    {
        if (TenantIds is null || TenantIds.Length > 256 || TenantIds.Contains(Guid.Empty) || TenantIds.Distinct().Count() != TenantIds.Length
            || Enabled && TenantIds.Length == 0) throw new InvalidOperationException("Temporal access requires an explicit, unique tenant allowlist.");
    }
}

[AttributeUsage(AttributeTargets.Method)]
public sealed class TemporalPermissionAttribute(string permission) : Attribute
{
    public string Permission { get; } = permission;
}

public sealed class TemporalAccessGate
{
    public static readonly string[] SupportedPermissions = ["organization.read", "organization.write"];
    private readonly HashSet<Guid> tenants;
    private readonly bool enabled;
    private readonly AccessPolicyStore policies;
    private readonly TimeProvider clock;
    public TemporalAccessGate(TemporalAccessOptions options, AccessPolicyStore policies, TimeProvider clock)
    {
        options.Validate(); this.enabled = options.Enabled; tenants = options.TenantIds.ToHashSet();
        this.policies = policies; this.clock = clock;
    }
    public bool IsEnabled(Guid tenant) => enabled && tenants.Contains(tenant);
    public bool Allows(Guid tenant, string subject, string permission)
    {
        if (!IsEnabled(tenant)) return true;
        if (!SupportedPermissions.Contains(permission, StringComparer.Ordinal)) return false;
        // This can only veto. The endpoint still requires its original claims, grants,
        // subscription, JWT, client certificate and other domain checks.
        return policies.AuthorizeLiveNow(tenant, subject, permission, clock.GetUtcNow);
    }
}

public sealed class TemporalAccessFilter(TemporalAccessGate gate) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var requirement = context.ActionDescriptor.EndpointMetadata.OfType<TemporalPermissionAttribute>().SingleOrDefault();
        if (requirement is null || !context.ActionArguments.TryGetValue("tenantId", out var value) || value is not Guid tenant || !gate.IsEnabled(tenant))
        { await next(); return; }
        context.HttpContext.Response.Headers.CacheControl = "no-store";
        try
        {
            if (context.HttpContext.User.Identity?.IsAuthenticated != true
                || !gate.Allows(tenant, OrgAuthorization.Subject(context.HttpContext.User), requirement.Permission))
            { context.Result = new ObjectResult(new { error = "این درخواست با محدودیت زمانی یا سیاست جاری سازمان سازگار نیست.", code = "temporal-access-denied" }) { StatusCode = 403 }; return; }
        }
        catch (Exception error) when (error is UnauthorizedAccessException or KeyNotFoundException or InvalidDataException or System.Text.Json.JsonException)
        { context.Result = new StatusCodeResult(403); return; }
        await next();
    }
}
