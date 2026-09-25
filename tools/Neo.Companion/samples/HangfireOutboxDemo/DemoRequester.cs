using System.Security.Claims;
using Neo.Domain.Entities.Common;
using Neo.Domain.Features.Client;

// Background teaching identity, not production authentication or authorization.
public sealed class DemoRequester : IRequesterUser
{
    public UserId? Id { get; set; }
    public string Platform => "demo";
    public string? AppName => "Neo.HangfireOutboxDemo";
    public string? Lang => "en";
    public string? Mobile => null;
    public string? CorrelationId => null;
    public Dictionary<string, object> Properties { get; set; } = [];
    public Task<LanguageId> GetLangIdAsync(CancellationToken cancellationToken = default) => Task.FromResult(default(LanguageId)!);
    public List<Claim> Claims() => [];
    public bool? IsInRole(string role) => false;
}
