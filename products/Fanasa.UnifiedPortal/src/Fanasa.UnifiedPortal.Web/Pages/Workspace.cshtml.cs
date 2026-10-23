using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Fanasa.UnifiedPortal.Web.Pages;

[Authorize]
public sealed class WorkspaceModel(IPlatformCatalogClient catalog, IWorkspaceJourneySource journeys,
    IConfiguration configuration, IServiceDiscoveryClient? discovery = null) : PageModel
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    public IReadOnlyCollection<PlatformOrganizationView> Organizations { get; private set; } = [];
    public Guid? ActiveOrganizationId { get; private set; }
    public string ActiveOrganizationName => Organizations.FirstOrDefault(x => x.Id == ActiveOrganizationId)?.Name ?? "";
    public bool IsPreview => journeys.IsSyntheticPreview;
    public bool IsAdmin => IsPreview && User.Identity?.IsAuthenticated == true && User.IsInRole("PlatformAdmin");
    public CatalogStatus Status { get; private set; } = CatalogStatus.SignInRequired;
    public string? AccountingUrl { get; private set; }
    public string BootstrapJson { get; private set; } = "{}";

    public async Task OnGetAsync(Guid? tenantId, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        var published = discovery?.GetAsync(cancellationToken)
            ?? Task.FromResult(new ServiceDiscovery(CatalogStatus.NotConfigured, []));
        Organizations = [];
        ActiveOrganizationId = null;
        AccountingUrl = null;
        Status = CatalogStatus.SignInRequired;
        var subject = User.Identity?.IsAuthenticated == true
            ? User.FindFirstValue("sub") ?? User.FindFirstValue(ClaimTypes.NameIdentifier) : null;
        IReadOnlyList<WorkspaceActiveService> active = [];
        IReadOnlyCollection<PlatformProductView> authorizedProducts = [];
        WorkspaceJourneyData data = new([], [], [], WorkspaceUsage.NotConnected, Guid.Empty, "");
        var message = "برای مشاهده خدمات سازمان وارد حساب شوید.";
        if (!string.IsNullOrWhiteSpace(subject))
        {
            var workspace = await catalog.GetWorkspaceAsync(subject, cancellationToken, tenantId);
            Organizations = workspace.Organizations;
            ActiveOrganizationId = PlatformWorkspace.SelectOrganization(Organizations, tenantId);
            Status = workspace.Status;
            if (Status == CatalogStatus.Ready && ActiveOrganizationId.HasValue && workspace.ProductOrganizationId != ActiveOrganizationId)
                Status = CatalogStatus.Unavailable;
            if (Status == CatalogStatus.Ready && ActiveOrganizationId.HasValue)
            {
                authorizedProducts = workspace.Products;
                if (!IsPreview)
                    AccountingUrl = CentralAccountingUrl(configuration["PlatformControlCenter:BaseUrl"], ActiveOrganizationId.Value);
                active = workspace.Products.Select(x => new WorkspaceActiveService(x.Id.ToString(), x.Key,
                    x.DisplayName, x.Center, IndexModel.SafeProductUrl(x.Url))).ToArray();
                try
                {
                    data = await journeys.GetAsync(subject, ActiveOrganizationId.Value, cancellationToken);
                    if (data.OrganizationId != ActiveOrganizationId || !string.Equals(data.Subject, subject, StringComparison.Ordinal))
                        data = new([], [], [], WorkspaceUsage.Unavailable, Guid.Empty, "");
                    else
                        data = data with
                        {
                            Requests = data.Requests.Where(x => x.OrganizationId == ActiveOrganizationId
                                && string.Equals(x.RequestedBySubject, subject, StringComparison.Ordinal)).ToArray(),
                            Publications = data.Publications.Where(x => x.OrganizationId == ActiveOrganizationId
                                && string.Equals(x.SubmittedBySubject, subject, StringComparison.Ordinal))
                                .Select(x => x with { Url = IndexModel.SafeProductUrl(x.Url) ?? "" }).ToArray()
                        };
                }
                catch (Exception ex) when (ex is HttpRequestException or JsonException || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
                {
                    data = new([], [], [], WorkspaceUsage.Unavailable, Guid.Empty, "");
                }
            }
            message = Status switch
            {
                CatalogStatus.NotConfigured => "اتصال فهرست سازمان‌ها هنوز آماده نیست.",
                CatalogStatus.Forbidden => "دسترسی به اطلاعات سازمان برقرار نشد.",
                CatalogStatus.Unavailable => "اطلاعات سازمان دریافت نشد. دوباره تلاش کنید.",
                CatalogStatus.SignInRequired => "نشست شما نیاز به تازه‌سازی دارد. دوباره وارد شوید.",
                _ when Organizations.Count == 0 => "هنوز عضویت سازمانی برای حساب شما ثبت نشده است.",
                _ when !ActiveOrganizationId.HasValue => "یک سازمان قابل‌دسترسی را انتخاب کنید.",
                _ => $"خدمات و مصرف {ActiveOrganizationName}"
            };
        }
        var subjectKey = string.IsNullOrWhiteSpace(subject) ? "" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(subject))).ToLowerInvariant();
        var status = JsonNamingPolicy.CamelCase.ConvertName(Status.ToString());
        // Default encoder escapes HTML delimiters, including closing script tags in provider names.
        BootstrapJson = JsonSerializer.Serialize(new WorkspaceBootstrap(IsPreview ? "synthetic-preview" : "connected",
            subjectKey, ActiveOrganizationId, Organizations, ActiveOrganizationName, status, message, IsAdmin,
            data.Catalog, active, data.Requests, data.Publications, data.Usage, WorkspaceCenters.All,
            ServiceCatalogPresentation.Create(await published, Status, ActiveOrganizationId, authorizedProducts), AccountingUrl), JsonOptions);
    }

    public async Task<IActionResult> OnGetSnapshotAsync(Guid? tenantId, CancellationToken cancellationToken)
    {
        await OnGetAsync(tenantId, cancellationToken);
        return Content(BootstrapJson, "application/json; charset=utf-8");
    }

    public static string? CentralAccountingUrl(string? baseUrl, Guid organizationId)
    {
        if (organizationId == Guid.Empty || !Uri.TryCreate(baseUrl, UriKind.Absolute, out var origin)
            || origin.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(origin.UserInfo)
            || !string.IsNullOrEmpty(origin.Query) || !string.IsNullOrEmpty(origin.Fragment))
            return null;
        return origin.AbsoluteUri.TrimEnd('/') + "/Accounting?tenant=" + organizationId.ToString("D");
    }
}
