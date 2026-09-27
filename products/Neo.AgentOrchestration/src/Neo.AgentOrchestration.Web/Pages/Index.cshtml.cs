using Microsoft.AspNetCore.Mvc.RazorPages;
using Neo.AgentOrchestration.Contracts;

namespace Neo.AgentOrchestration.Web.Pages;

[Microsoft.AspNetCore.Authorization.AllowAnonymous]
public sealed class IndexModel(OrchestrationClient client, ILogger<IndexModel> logger) : PageModel
{
    public ProductInfo? Info { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        try { Info = await client.GetInfoAsync(ct); }
        catch (Exception error) when (error is HttpRequestException or System.Text.Json.JsonException ||
                                    error is OperationCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Orchestration API status unavailable ({ErrorType}).", error.GetType().Name);
        }
    }
}
