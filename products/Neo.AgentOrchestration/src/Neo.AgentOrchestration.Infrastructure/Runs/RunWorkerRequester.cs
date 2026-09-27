using System.Security.Claims;
using Neo.Domain.Entities.Common;
using Neo.Domain.Features.Client;

namespace Neo.AgentOrchestration.Infrastructure.Runs;

// Queue telemetry identity only; supplies no membership/authentication grants.
// Business identity is bound to the scoped durable run, not this host identity.
internal sealed class RunWorkerRequester : IRequesterUser
{
    public UserId? Id { get; set; }
    public string Platform => "worker";
    public string? AppName => "Neo.AgentOrchestration.Worker";
    public string? Lang => "en";
    public string? Mobile => null;
    public string? CorrelationId => null;
    public Dictionary<string, object> Properties { get; set; } = [];
    public Task<LanguageId> GetLangIdAsync(CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(default(LanguageId)!); }
    public List<Claim> Claims() => [];
    public bool? IsInRole(string role) => false;
}
