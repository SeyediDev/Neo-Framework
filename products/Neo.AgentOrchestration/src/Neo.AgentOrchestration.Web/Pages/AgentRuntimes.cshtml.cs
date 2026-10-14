using Neo.AgentOrchestration.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Neo.AgentOrchestration.Web.Pages;

public sealed class AgentRuntimesModel(OrchestrationClient client) : WorkPageModel(client)
{
    [BindProperty(SupportsGet = true)] public Guid ProjectId { get; set; }
    public AgentRuntimeCatalog? RuntimeCatalog { get; private set; }
    public Task OnGetAsync(CancellationToken ct) => Attempt(async () =>
    {
        await LoadCatalog(ct);
        if (ProjectId != Guid.Empty) RuntimeCatalog = await Get<AgentRuntimeCatalog>($"projects/{ProjectId:D}/agent-runtimes", ct);
    });
    public static string Reason(string code) => code switch
    {
        "execution-gates-pending" => "اتصال سالم؛ گیت‌های اجرای تسک هنوز تکمیل نشده‌اند",
        "advisory-healthy" => "اتصال مشاور سالم است؛ خروجی پیشنهادی نیاز به بررسی دارد و تأیید یا تکمیل کار نیست",
        "advisory-unreachable" => "مشاور نصب است؛ بررسی اتصال خصوصی موفق نبود",
        "cli-installed" => "CLI نصب است؛ اتصال مدیریت‌شده هنوز آماده نیست",
        "native-unreachable" => "بررسی اتصال موفق نبود",
        "not-installed" => "نصب تأیید نشده است",
        "not-configured" => "برای این پروژه اتصالی پیکربندی نشده است",
        "report-stale" => "گزارش قدیمی است؛ سلامت فعلی نامعلوم",
        _ => "گزارش سلامت معتبر در دسترس نیست"
    };
}
