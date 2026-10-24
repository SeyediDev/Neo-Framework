using System.Security.Claims;
using Fanasa.AccessManagement.Web.Platform;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Fanasa.AccessManagement.Web.Pages;

public sealed class ArchitectureModel(PlatformRegistry registry) : PageModel
{
    public PlatformProduct[] Products { get; private set; } = [];
    public void OnGet()
    {
        var subject = User.FindFirstValue("sub") ?? User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
        Products = (PlatformAuthorization.IsAdmin(User) ? registry.Products() : registry.Products(subject))
            .Where(p => !p.Key.Contains("hyper", StringComparison.OrdinalIgnoreCase)
                && !p.DisplayName.Contains("هایپر", StringComparison.OrdinalIgnoreCase)).ToArray();
    }
    public static string? SafeUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == "https" && string.IsNullOrEmpty(uri.UserInfo) ? uri.AbsoluteUri : null;
}
