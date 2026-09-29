using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.Filters;

namespace Neo.AgentOrchestration.Api;

public static class WorkspaceSecurity
{
    public static bool IsLocalDevelopment(HttpContext http) =>
        http.RequestServices.GetRequiredService<IHostEnvironment>().IsDevelopment() &&
        string.Equals(http.RequestServices.GetRequiredService<IConfiguration>()["NEO_LOCAL_DEVELOPMENT"], "true", StringComparison.OrdinalIgnoreCase) &&
        http.Connection.RemoteIpAddress is not null && System.Net.IPAddress.IsLoopback(http.Connection.RemoteIpAddress) &&
        !http.Request.Headers.ContainsKey("Forwarded") && !http.Request.Headers.ContainsKey("X-Forwarded-For");
    public const string Read = "workspace.read";
    public const string Write = "workspace.write";
    public const string Configure = "workspace.configure";
    public const string Approve = "workspace.approve";
    public const string Execute = "workspace.execute";

    public static void AddPolicies(AuthorizationOptions options)
    {
        options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
        foreach (var permission in new[] { "read", "write", "configure", "approve", "execute" })
            options.AddPolicy("workspace." + permission, policy => policy.RequireAssertion(context =>
            {
                var http = context.Resource as HttpContext ?? (context.Resource as AuthorizationFilterContext)?.HttpContext;
                if (http is null || !Guid.TryParse(http.Request.RouteValues["organizationId"]?.ToString(), out var org) ||
                    !Guid.TryParse(http.Request.RouteValues["workspaceId"]?.ToString(), out var workspace) || org == Guid.Empty || workspace == Guid.Empty)
                    return false;
                // A single issuer-signed grant binds the organization, workspace
                // AND permission; independently matching claim lists is unsafe.
                if (http is not null && IsLocalDevelopment(http) && http.Request.Headers.TryGetValue("X-Orchestration-Local", out var local) && local == "true") return true;
                return context.User.Identity?.IsAuthenticated == true && Subject(context.User) is not null &&
                    context.User.HasClaim("nao_grant", $"{org:D}/{workspace:D}/{permission}");
            }));
    }

    public static string? Subject(ClaimsPrincipal user)
    {
        var subjects = user.FindAll("sub").Select(x => x.Value).ToArray();
        return subjects.Length == 1 && subjects[0].Length is > 0 and <= 200 &&
            subjects[0] == subjects[0].Trim() && !subjects[0].Any(char.IsControl) &&
            !subjects[0].StartsWith("neo-run:", StringComparison.OrdinalIgnoreCase) ? subjects[0] : null;
    }
}
