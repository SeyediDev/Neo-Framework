using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace Neo.Infrastructure.Features.Crud;

public sealed record CrudDoctorFinding(string Resource, string Code, string Detail);
public sealed record CrudDoctorReport(int ResourcesChecked, IReadOnlyList<CrudDoctorFinding> Findings)
{
    public bool Healthy => ResourcesChecked > 0 && Findings.Count == 0;
}

/// <summary>Resolves application services in fresh scopes; checks metadata and optionally connectivity. Never migrates or writes.</summary>
public static class CrudRuntimeDoctor
{
    public static async Task<CrudDoctorReport> CheckAsync(IServiceProvider services, bool checkConnectivity = false, CancellationToken ct = default)
    {
        var findings = new List<CrudDoctorFinding>();
        var registrations = services.GetServices<CrudResourceRegistration>().ToArray();
        if (registrations.Length == 0) findings.Add(new("application", "no_resources", "No Neo CRUD resources registered."));
        foreach (var registration in registrations)
        {
            ct.ThrowIfCancellationRequested();
            await using var scope = services.CreateAsyncScope();
            var sp = scope.ServiceProvider;
            var name = registration.DefinitionType.FullName ?? registration.DefinitionType.Name;
            try
            {
                foreach (var error in registration.Validate(sp)) findings.Add(new(name, "configuration", error));
                var definition = registration.Definition(sp);
                var provider = sp.GetService<IAuthorizationPolicyProvider>();
                foreach (var entry in definition.Policies.Where(x => definition.Operations.HasFlag(x.Key) && x.Value is not null))
                    if (provider is null || await provider.GetPolicyAsync(entry.Value!) is null)
                        findings.Add(new(name, "missing_policy", $"Authorization policy for {entry.Key} is not registered."));
                if (checkConnectivity && !await registration.CanConnect(sp, ct))
                    findings.Add(new(name, "database_unavailable", "Database connectivity check failed; no migration was attempted."));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                // Application constructors/provider exceptions can contain connection strings. Do not serialize their messages.
                findings.Add(new(name, "resolution_failed", $"Service or metadata resolution failed ({ex.GetType().Name}); inspect local application logs."));
            }
        }
        return new(registrations.Length, findings);
    }
}
