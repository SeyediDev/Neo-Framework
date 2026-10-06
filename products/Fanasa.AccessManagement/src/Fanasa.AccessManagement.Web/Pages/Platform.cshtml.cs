using System.Security.Claims;
using Fanasa.AccessManagement.Web.Platform;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Fanasa.AccessManagement.Web.Pages;

public sealed class PlatformModel(PlatformRegistry registry) : PageModel
{
    public PlatformProduct[] Products { get; private set; } = [];
    public PlatformGrant[] Grants { get; private set; } = [];
    public PlatformTenant[] Tenants { get; private set; } = [];
    [BindProperty] public string Subject { get; set; } = "";
    [BindProperty] public Guid TenantId { get; set; }
    [BindProperty] public string Permission { get; set; } = "developer.read";
    [BindProperty] public DateTimeOffset? ExpiresAt { get; set; }
    public string? Error { get; private set; }
    public bool IsAdmin => PlatformAuthorization.IsAdmin(User);
    public void OnGet() => Load();
    private void Load()
    {
        Products = registry.Products(IsAdmin ? null : User.FindFirstValue("sub") ?? User.FindFirstValue(ClaimTypes.NameIdentifier));
        Tenants = registry.Tenants(IsAdmin ? null : User.FindFirstValue("sub") ?? User.FindFirstValue(ClaimTypes.NameIdentifier));
        if (IsAdmin) Grants = registry.Grants();
    }
    public IActionResult OnPost(string operation)
    {
        if (!IsAdmin) return Forbid();
        try
        {
            var grant = new PlatformGrant(Subject, TenantId, Permission, ExpiresAt);
            var actor = User.FindFirstValue("sub") ?? User.FindFirstValue(ClaimTypes.NameIdentifier)!;
            if (operation == "grant") registry.Grant(grant, actor);
            else if (operation == "revoke") registry.Revoke(grant, actor);
            else return BadRequest();
            return RedirectToPage();
        }
        catch (ArgumentException ex) { Error = ex.Message; Load(); return Page(); }
    }
}
