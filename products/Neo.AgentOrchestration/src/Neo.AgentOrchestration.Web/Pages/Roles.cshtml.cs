using Microsoft.AspNetCore.Mvc;
using Neo.AgentOrchestration.Contracts;
namespace Neo.AgentOrchestration.Web.Pages;

public sealed class RolesModel(OrchestrationClient client) : WorkPageModel(client)
{
    [BindProperty(SupportsGet = true)] public Guid? ProjectId { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? RoleId { get; set; }
    [BindProperty(SupportsGet = true)] public string? Domain { get; set; }
    public IReadOnlyList<WorkItemView> Items { get; private set; } = [];
    public Task OnGetAsync(CancellationToken ct) => Attempt(async () =>
    {
        await LoadCatalog(ct);
        var items = new List<WorkItemView>();
        var query = "items?take=200";
        if (ProjectId.HasValue) query += "&projectId=" + ProjectId;
        if (RoleId.HasValue) query += "&roleId=" + RoleId;
        if (!string.IsNullOrWhiteSpace(Domain)) query += "&domain=" + Uri.EscapeDataString(Domain);
        for (var skip = 0; ;)
        {
            var page = await Get<WorkBoard>(query + "&skip=" + skip, ct);
            items.AddRange(page.Items);
            skip += page.Items.Count;
            if (skip >= page.Total || page.Items.Count == 0) break;
        }
        Items = items.DistinctBy(x => x.Id).OrderBy(x => Array.IndexOf(Statuses, x.Status))
            .ThenByDescending(x => x.UpdatedAtUtc).ThenBy(x => x.Key).ToArray();
    });
}
