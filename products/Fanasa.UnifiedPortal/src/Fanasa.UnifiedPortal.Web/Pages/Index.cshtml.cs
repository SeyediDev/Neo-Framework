using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Fanasa.UnifiedPortal.Web.Pages;

public sealed class IndexModel(IPlatformCatalogClient catalog) : PageModel
{
    public IReadOnlyCollection<PlatformOrganizationView> Organizations { get; private set; } = [];
    public IReadOnlyCollection<PlatformProductView> Products { get; private set; } = [];
    public Guid? ActiveOrganizationId { get; private set; }
    public string? ActiveOrganizationName => Organizations.FirstOrDefault(x => x.Id == ActiveOrganizationId)?.Name;
    public CatalogStatus Status { get; private set; }
    public string Message { get; private set; } = "";

    public async Task OnGetAsync(Guid? tenantId, CancellationToken cancellationToken) => await LoadAsync(tenantId, cancellationToken);

    public async Task<IActionResult> OnGetWorkspaceAsync(Guid? tenantId, CancellationToken cancellationToken)
    {
        await LoadAsync(tenantId, cancellationToken);
        return new JsonResult(new { status = Status.ToString(), organizations = Organizations,
            activeOrganizationId = ActiveOrganizationId, activeOrganizationName = ActiveOrganizationName, products = Products, message = Message });
    }

    private async Task LoadAsync(Guid? tenantId, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        if (User.Identity?.IsAuthenticated != true)
        {
            Status = CatalogStatus.SignInRequired;
            Message = "برای مشاهده سازمان‌ها و سامانه‌های خود وارد شوید.";
            return;
        }
        var subject = User.FindFirstValue("sub") ?? User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(subject))
        {
            Status = CatalogStatus.SignInRequired;
            Message = "نشست شما نیاز به تازه‌سازی دارد. دوباره وارد شوید.";
            return;
        }
        var workspace = await catalog.GetWorkspaceAsync(subject, cancellationToken);
        Organizations = workspace.Organizations;
        Status = workspace.Status;
        ActiveOrganizationId = SelectOrganization(Organizations, tenantId);
        Products = workspace.Products.Where(x => ActiveOrganizationId.HasValue && x.TenantId == ActiveOrganizationId).ToArray();
        Message = Status switch
        {
            CatalogStatus.NotConfigured => "اتصال سازمان‌ها هنوز آماده نیست. لطفاً با پشتیبانی تماس بگیرید.",
            CatalogStatus.Forbidden => "دریافت فهرست سازمان‌ها مجاز نشد. لطفاً با پشتیبانی تماس بگیرید.",
            CatalogStatus.Unavailable => "دریافت اطلاعات ممکن نشد. دوباره تلاش کنید.",
            _ when Organizations.Count == 0 => "سازمان قابل‌دسترسی برای حساب شما یافت نشد. دسترسی سازمانی خود را با مدیر سازمان بررسی کنید.",
            _ when tenantId.HasValue && !ActiveOrganizationId.HasValue => "این سازمان در دسترس شما نیست. یک سازمان از فهرست انتخاب کنید.",
            _ when !ActiveOrganizationId.HasValue => "سازمان موردنظر را انتخاب کنید تا سامانه‌های آن نمایش داده شوند.",
            _ when Products.Count == 0 => "هنوز سامانه‌ای برای شما در این سازمان فعال نشده است. با مدیر سازمان در ارتباط باشید.",
            _ => $"سامانه‌های {ActiveOrganizationName} آماده استفاده‌اند."
        };
    }

    public static Guid? SelectOrganization(IReadOnlyCollection<PlatformOrganizationView> organizations, Guid? requested)
        => requested.HasValue ? organizations.Any(x => x.Id == requested) ? requested : null
            : organizations.Count == 1 ? organizations.First().Id : null;

    public static string? SafeProductUrl(string? url) => Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme is "https" or "http" && string.IsNullOrEmpty(uri.UserInfo) ? uri.AbsoluteUri : null;
}
