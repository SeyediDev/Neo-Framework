using Microsoft.AspNetCore.Mvc;
using Neo.AgentOrchestration.Contracts;

namespace Neo.AgentOrchestration.Web.Pages;

public sealed class DeleteProjectModel(OrchestrationClient client) : WorkPageModel(client)
{
    [BindProperty(SupportsGet = true)] public Guid ProjectId { get; set; }
    [BindProperty] public string ExpectedSnapshot { get; set; } = "";
    [BindProperty] public string ConfirmProjectKey { get; set; } = "";
    [BindProperty] public bool IncludeActiveManualAssignments { get; set; }
    public ProjectDeletionPreview? Preview { get; private set; }
    public Task OnGetAsync(CancellationToken ct) => Attempt(() => Load(ct));
    private async Task Load(CancellationToken ct)
    {
        Preview = await Get<ProjectDeletionPreview>($"projects/{ProjectId}/deletion-preview", ct);
        await LoadCatalog(ct);
    }
    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (await Attempt(async () =>
        {
            var deleted = await Delete<ProjectDeletionResult>($"projects/{ProjectId}",
                new DeleteProjectRequest(ExpectedSnapshot, ConfirmProjectKey, IncludeActiveManualAssignments), ct);
            TempData["ProjectDeleted"] = $"پروژهٔ {deleted.Key} و تمام {deleted.DeletedRecords["WorkItems"]} کار وابسته حذف شدند.";
        })) return RedirectToPage("/Manage", new { OrganizationId, WorkspaceId });
        await Attempt(() => Load(ct));
        return Page();
    }
    public static string RecordLabel(string table) => table switch
    {
        "Projects" => "پروژه", "WorkItems" => "کارها، شامل زیرتسک‌ها و بایگانی‌ها", "WorkItemDependencies" => "وابستگی کارها",
        "WorkItemOwnerHistory" => "سوابق مالکیت", "WorkItemEvidence" => "شواهد کار", "WorkItemLogs" => "یادداشت‌ها و سابقه",
        "WorkItemTimeEntries" => "بازه‌های زمان", "WorkTokenUsage" => "گزارش توکن کارها", "AgentRuns" => "اجرای ایجنت",
        "TokenUsageReports" => "گزارش توکن اجراها", "WorkflowDefinitions" => "فلوهای پروژه", "WorkflowTransitions" => "گام‌های فلو",
        "WorkflowApprovals" => "تأییدهای فلو", "InboxReceipts" => "رسید رویداد", "Deliveries" => "رکورد تحویل",
        "OutboxMessages" => "پیام صف", "TemplateInstantiations" => "رسید ساخت پروژه از الگو",
        "ProjectRepositoryBindings" => "اتصال ریپازیتوری", _ => table
    };
}
