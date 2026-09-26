using Neo.AgentOrchestration.Contracts;

namespace Neo.AgentOrchestration.Web;

public sealed class OrchestrationClient(HttpClient http)
{
    public Task<ProductInfo?> GetInfoAsync(CancellationToken ct)
        => http.GetFromJsonAsync<ProductInfo>("api/orchestration/v1/system", ct);
}
