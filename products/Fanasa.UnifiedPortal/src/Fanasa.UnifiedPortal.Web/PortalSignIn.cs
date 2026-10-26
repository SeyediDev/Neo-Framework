using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.WebUtilities;

namespace Fanasa.UnifiedPortal.Web;

public static class PortalSignIn
{
    // Return destinations are navigation hints only. The destination validates
    // the renewed human session and organization membership independently.
    public static string SafeDestination(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 2048 || value.Any(char.IsControl) || value.Contains('\\')) return "/";
        var fragmentIndex = value.IndexOf('#');
        var fragment = fragmentIndex < 0 ? "" : value[(fragmentIndex + 1)..];
        var address = fragmentIndex < 0 ? value : value[..fragmentIndex];
        var queryIndex = address.IndexOf('?');
        var path = queryIndex < 0 ? address : address[..queryIndex];
        var workspace = path.Equals("/Workspace", StringComparison.OrdinalIgnoreCase);
        if (!workspace && path != "/" && !path.Equals("/Index", StringComparison.OrdinalIgnoreCase)) return "/";

        var destination = workspace ? "/Workspace" : "/";
        if (queryIndex >= 0)
        {
            var query = QueryHelpers.ParseQuery(address[queryIndex..]);
            if (query.TryGetValue("tenantId", out var organization) && organization.Count == 1
                && Guid.TryParseExact(organization[0], "D", out var id) && id != Guid.Empty)
                destination += "?tenantId=" + id.ToString("D");
        }
        var allowedFragment = workspace
            ? fragment is "catalog" or "requests" or "services" or "usage" or "publish" or "review"
            : fragment is "top" or "workspace" or "centers";
        return destination + (allowedFragment ? "#" + fragment : "");
    }

    public static string LoginUrl(string destination) => QueryHelpers.AddQueryString("/login", "returnUrl", SafeDestination(destination));

    public static IResult Challenge(HttpContext context)
    {
        var requested = context.Request.Query["returnUrl"];
        return Results.Challenge(new AuthenticationProperties
        {
            RedirectUri = SafeDestination(requested.Count == 1 ? requested[0] : null)
        }, [OpenIdConnectDefaults.AuthenticationScheme]);
    }
}
