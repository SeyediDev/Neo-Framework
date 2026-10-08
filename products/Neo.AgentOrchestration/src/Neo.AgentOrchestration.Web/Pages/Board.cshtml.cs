using Microsoft.AspNetCore.Mvc;
using Neo.AgentOrchestration.Contracts;
namespace Neo.AgentOrchestration.Web.Pages;
public sealed class BoardModel(OrchestrationClient client) : WorkPageModel(client)
{
    [BindProperty(SupportsGet = true)] public Guid? ProjectId { get; set; }
    [BindProperty(SupportsGet = true)] public Guid? RoleId { get; set; }
    [BindProperty(SupportsGet = true)] public string? Domain { get; set; }
    [BindProperty(SupportsGet = true)] public string? State { get; set; }
    [BindProperty(SupportsGet = true)] public string? ItemType { get; set; }
    [BindProperty(SupportsGet = true)] public bool IncludeArchived { get; set; }
    [BindProperty(SupportsGet = true)] public int Skip { get; set; }
    public WorkBoard? Board { get; private set; }
    [BindProperty] public Guid MoveItemId { get; set; }
    [BindProperty] public Guid MoveVersion { get; set; }
    [BindProperty] public string Destination { get; set; } = "";
    [BindProperty] public Guid? ClaimRoleId { get; set; }
    [BindProperty] public string? Branch { get; set; }
    [BindProperty] public string? MoveNote { get; set; }

    // Presentation hints only. API v1 owns permissions, versions and lifecycle gates.
    public static string[] MoveDestinations(string status, bool archived = false) => archived ? [] : status switch
    {
        "Backlog" => ["Ready", "Cancelled"],
        "Ready" => ["InProgress", "Blocked", "Cancelled"],
        "InProgress" => ["Review", "Blocked", "Cancelled"],
        "Blocked" => ["Ready", "Cancelled"],
        "Review" => ["Ready", "Done", "Blocked", "Cancelled"],
        _ => []
    };
    public async Task<IActionResult> OnPostMoveAsync(CancellationToken ct)
    {
        if (MoveItemId == Guid.Empty || MoveVersion == Guid.Empty || !Statuses.Contains(Destination))
            ModelState.AddModelError(nameof(Destination), "انتقال معتبر نیست.");
        if (Destination == "InProgress" && (!ClaimRoleId.HasValue || ClaimRoleId == Guid.Empty))
            ModelState.AddModelError(nameof(ClaimRoleId), "رول را برای برداشتن کار انتخاب کنید.");
        if (await Attempt(async () =>
        {
            if (Destination == "InProgress")
                await Send<WorkItemDetails>($"items/{MoveItemId}/claim", new ClaimWorkItemRequest(MoveVersion, ClaimRoleId!.Value, Branch), ct);
            else
                await Send<WorkItemDetails>($"items/{MoveItemId}/status", new ChangeStatusRequest(MoveVersion, Destination, MoveNote), ct);
        })) return RedirectToPage(new { OrganizationId, WorkspaceId, ProjectId, RoleId, Domain, State, ItemType, IncludeArchived, Skip });
        await OnGetAsync(ct);
        return Page();
    }
    public Task OnGetAsync(CancellationToken ct) => Attempt(async () =>
    {
        await LoadCatalog(ct);
        var query = $"items?take=50&skip={Skip}&includeArchived={IncludeArchived}";
        if (ProjectId.HasValue) query += "&projectId=" + ProjectId;
        if (RoleId.HasValue) query += "&roleId=" + RoleId;
        if (!string.IsNullOrWhiteSpace(Domain)) query += "&domain=" + Uri.EscapeDataString(Domain);
        if (!string.IsNullOrWhiteSpace(State)) query += "&status=" + Uri.EscapeDataString(State);
        if (!string.IsNullOrWhiteSpace(ItemType)) query += "&type=" + Uri.EscapeDataString(ItemType);
        Board = await Get<WorkBoard>(query, ct);
    });
}
