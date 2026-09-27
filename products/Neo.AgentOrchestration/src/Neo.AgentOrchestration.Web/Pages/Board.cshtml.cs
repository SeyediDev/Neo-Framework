using Microsoft.AspNetCore.Mvc;
using Neo.AgentOrchestration.Contracts;
namespace Neo.AgentOrchestration.Web.Pages;
public sealed class BoardModel(OrchestrationClient client) : WorkPageModel(client)
{
    [BindProperty(SupportsGet = true)] public Guid? ProjectId { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? RoleId { get; set; }
    [BindProperty(SupportsGet = true)] public string? Domain { get; set; }
    [BindProperty(SupportsGet = true)] public string? State { get; set; }
    [BindProperty(SupportsGet = true)] public bool IncludeArchived { get; set; }
    [BindProperty(SupportsGet = true)] public int Skip { get; set; }
    public WorkBoard? Board { get; private set; }
    public Task OnGetAsync(CancellationToken ct) => Attempt(async () =>
    {
        await LoadCatalog(ct);
        var query = $"items?take=50&skip={Skip}&includeArchived={IncludeArchived}";
        if (ProjectId.HasValue) query += "&projectId=" + ProjectId;
        if (RoleId.HasValue) query += "&roleId=" + RoleId;
        if (!string.IsNullOrWhiteSpace(Domain)) query += "&domain=" + Uri.EscapeDataString(Domain);
        if (!string.IsNullOrWhiteSpace(State)) query += "&status=" + Uri.EscapeDataString(State);
        Board = await Get<WorkBoard>(query, ct);
    });
}
