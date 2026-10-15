using Microsoft.AspNetCore.Mvc;
using Neo.AgentOrchestration.Contracts;

namespace Neo.AgentOrchestration.Web.Pages;

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdvisoriesModel(OrchestrationClient client) : WorkPageModel(client)
{
    [BindProperty(SupportsGet = true)] public Guid Id { get; set; }
    [BindProperty] public Guid RequestId { get; set; } = Guid.NewGuid();
    [BindProperty] public Guid Version { get; set; }
    [BindProperty] public Guid AdvisoryId { get; set; }
    [BindProperty] public string Brief { get; set; } = "";
    [BindProperty] public bool Consent { get; set; }
    public WorkItemView? Item { get; private set; }
    public bool SubmissionEnabled { get; private set; }
    public IReadOnlyList<AdvisoryRequestView> Requests { get; private set; } = [];
    public bool HasOutstanding => Requests.Any(x => x.State is "Queued" or "Submitting" or "Held");
    public Task OnGetAsync(CancellationToken ct) => Attempt(() => Load(ct));
    public async Task<IActionResult> OnPostCreateAsync(CancellationToken ct)
    {
        if (await Attempt(async () =>
        {
            if (!Consent || string.IsNullOrWhiteSpace(Brief) || Brief.Length > 2000) throw new WebApiException(400);
            _ = await Send<AdvisoryRequestView>($"items/{Id:D}/advisories", new CreateAdvisoryRequest(RequestId, Version, Brief, Consent), ct);
        })) return RedirectToPage(new { OrganizationId, WorkspaceId, Id });
        await Attempt(() => Load(ct)); return Page();
    }
    public async Task<IActionResult> OnPostCancelAsync(CancellationToken ct)
    {
        if (await Attempt(async () => _ = await Send<AdvisoryRequestView>($"advisories/{AdvisoryId:D}/cancel", new VersionRequest(Version), ct)))
            return RedirectToPage(new { OrganizationId, WorkspaceId, Id });
        await Attempt(() => Load(ct)); return Page();
    }
    private async Task Load(CancellationToken ct)
    {
        Item = (await Get<WorkItemDetails>($"items/{Id:D}", ct)).Item;
        var catalog = await Get<AdvisoryRequestCatalog>($"items/{Id:D}/advisories", ct);
        Requests = catalog.Requests; SubmissionEnabled = catalog.SubmissionEnabled;
    }
    public static string State(string value) => value switch {
        "Queued" => "در صف", "Submitting" => "در حال تهیهٔ پیشنهاد", "Draft" => "پیش‌نویس آمادهٔ بررسی",
        "Held" => "نیازمند بررسی اپراتور؛ ارسال مجدد متوقف است", "Cancelled" => "لغوشده",
        "Expired" => "مهلت پایان یافته", "Rejected" => "ارسال انجام نشد", _ => "نامعلوم" };
}
