using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Neo.AgentOrchestration.Contracts;

namespace Neo.AgentOrchestration.Web.Pages;

public abstract class WorkPageModel(OrchestrationClient client) : PageModel
{
    [BindProperty(SupportsGet = true)] public Guid OrganizationId { get; set; }
    [BindProperty(SupportsGet = true)] public Guid WorkspaceId { get; set; }
    public WorkspaceCatalog? Catalog { get; protected set; }
    public string? Error { get; protected set; }
    protected Task<T> Get<T>(string resource, CancellationToken ct) => client.SendAsync<T>(OrganizationId, WorkspaceId, resource, ct);
    protected Task<T> Send<T>(string resource, object? body, CancellationToken ct, bool put = false)
        => client.SendAsync<T>(OrganizationId, WorkspaceId, resource, ct, put ? HttpMethod.Put : HttpMethod.Post, body);
    protected async Task<bool> Attempt(Func<Task> action)
    {
        if (!ModelState.IsValid || OrganizationId == Guid.Empty || WorkspaceId == Guid.Empty)
        { Error = "مقادیر فرم یا شناسه‌های محدوده معتبر نیستند."; Response.StatusCode = 400; return false; }
        try { await action(); return true; }
        catch (WebApiException ex) { Error = ex.Message; Response.StatusCode = ex.Status; return false; }
    }
    protected async Task LoadCatalog(CancellationToken ct) => Catalog = await Get<WorkspaceCatalog>("catalog", ct);
    public string Role(Guid? id) => Catalog?.Roles.FirstOrDefault(x => x.Id == id)?.Name ?? "بدون رول";
    public string Project(Guid id) => Catalog?.Projects.FirstOrDefault(x => x.Id == id)?.Name ?? id.ToString();
    public static string Time(long seconds) => $"{seconds / 3600:N0} ساعت و {seconds % 3600 / 60:00} دقیقه و {seconds % 60:00} ثانیه";
    public static string Status(string value) => value switch {
        "Backlog" => "بک‌لاگ", "Ready" => "آماده", "InProgress" => "در حال انجام", "Review" => "بازبینی",
        "Blocked" => "مسدود", "Done" => "تکمیل‌شده", "Cancelled" => "لغوشده", _ => value };
    public static readonly string[] Statuses = ["Backlog", "Ready", "InProgress", "Review", "Blocked", "Done", "Cancelled"];
}
