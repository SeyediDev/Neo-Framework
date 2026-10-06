using System.Security.Claims;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Fanasa.UnifiedPortal.Web.Pages;

public sealed class IndexModel(IPlatformCatalogClient catalog) : PageModel
{
    public IReadOnlyCollection<PlatformProductView> Products { get; private set; } = [];
    public bool CatalogConfigured { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var subject = User.FindFirstValue("sub");
        Products = string.IsNullOrWhiteSpace(subject) ? [] : await catalog.GetProductsAsync(subject, cancellationToken);
        CatalogConfigured = Products.Count > 0;
    }
}
