using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Neo.AgentOrchestration.Api;

public static class WorkspaceSecurity
{
    public const string Read = "workspace.read";
    public const string Write = "workspace.write";
    public const string Configure = "workspace.configure";
    public const string Approve = "workspace.approve";

    public static void AddPolicies(AuthorizationOptions options)
    {
        options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
        foreach (var permission in new[] { "read", "write", "configure", "approve" })
            options.AddPolicy("workspace." + permission, policy => policy.RequireAuthenticatedUser().RequireAssertion(context =>
            {
                var http = context.Resource as HttpContext ?? (context.Resource as AuthorizationFilterContext)?.HttpContext;
                if (http is null || !Guid.TryParse(http.Request.RouteValues["organizationId"]?.ToString(), out var org) ||
                    !Guid.TryParse(http.Request.RouteValues["workspaceId"]?.ToString(), out var workspace) || org == Guid.Empty || workspace == Guid.Empty)
                    return false;
                // A single issuer-signed grant binds the organization, workspace
                // AND permission; independently matching claim lists is unsafe.
                return Subject(context.User) is not null && context.User.HasClaim("nao_grant", $"{org:D}/{workspace:D}/{permission}");
            }));
    }

    public static string? Subject(ClaimsPrincipal user)
    {
        var subjects = user.FindAll("sub").Select(x => x.Value).ToArray();
        return subjects.Length == 1 && subjects[0].Length is > 0 and <= 200 &&
            subjects[0] == subjects[0].Trim() && !subjects[0].Any(char.IsControl) ? subjects[0] : null;
    }
}
