using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Fanasa.AccessManagement.Web.Application.Tenancy;

namespace Fanasa.AccessManagement.Web.Security;

public static class TenantAuthorization
{
    public static bool Allows(ClaimsPrincipal user, Guid tenant, string permission)
    {
        if (user.Identity?.IsAuthenticated != true) return false;
        if (user.HasClaim("tenant_permission", $"{tenant}:{permission}")) return true;
        // Compatibility for a single-tenant session; multiple tenant claims must use scoped pairs.
        var tenants = user.FindAll("tenant_id").Select(x => x.Value).Distinct().ToArray();
        return tenants.Length == 1 && tenants[0] == tenant.ToString() && user.HasClaim("permission", permission);
    }
}

public sealed class FabricClaimsTransformation(PersistentAccessManagement access) : IClaimsTransformation
{
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity?.IsAuthenticated != true) return Task.FromResult(principal);
        var subject = principal.FindFirst("sub")?.Value ?? principal.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        var result = principal.Clone();
        foreach (var identity in result.Identities)
            foreach (var claim in identity.Claims.Where(x => (x.Type == "permission" && TenantGrant.Permissions.Contains(x.Value)) || (x.Type == "tenant_permission" && TenantGrant.Permissions.Any(p => x.Value.EndsWith(":" + p, StringComparison.Ordinal)))).ToArray()) identity.RemoveClaim(claim);
        if (subject is not null)
        {
            var claims = access.GetGrants(subject).Where(x => !x.ExpiresAt.HasValue || x.ExpiresAt > DateTimeOffset.UtcNow)
                .Where(x => access.GetUsers(x.TenantId).Any(u => u.IsActive && u.KeycloakSubject == subject))
                .Select(x => new Claim("tenant_permission", $"{x.TenantId}:{x.Permission}", ClaimValueTypes.String, "fanasa.fabric"));
            result.AddIdentity(new ClaimsIdentity(claims));
        }
        return Task.FromResult(result);
    }
}
