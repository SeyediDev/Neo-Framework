using System.Text.Json;
using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Domain.Access;

namespace Fanasa.AccessManagement.Web.Platform;

public sealed class PlatformInitialMembership(PersistentAccessManagement access, IConfiguration configuration,
    ILogger<PlatformInitialMembership> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var path = configuration["Platform:BootstrapMembershipFile"];
        if (string.IsNullOrEmpty(path)) return Task.CompletedTask;
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var identity = document.RootElement;
        var subject = identity.GetProperty("subject").GetString();
        var username = identity.GetProperty("username").GetString();
        if (!Guid.TryParse(subject, out _) || username != "developer.admin"
            || subject != configuration["Platform:BootstrapSubject"]
            || identity.GetProperty("issuer").GetString() != configuration["Authentication:Authority"])
            throw new InvalidOperationException("Bootstrap identity must match the explicitly verified Keycloak identity.");
        var tenant = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        if (!access.GetTenants().Any(x => x.Id == tenant && x.IsActive))
            throw new InvalidOperationException("The central internal tenant must exist before developer enrollment.");
        // Existing or offboarded memberships are never restored by restarting the service.
        if (!access.GetUsers(tenant).Any(x => x.KeycloakSubject == subject))
            access.Audited(() => access.AddUser(new AddTenantUserRequest(tenant, subject!, username)),
                "platform-bootstrap", "Explicit initial enrollment of user-requested developer.admin", "PlatformDeveloperEnrolled");
        logger.LogInformation("Initial developer membership checked; no realm or cluster administrator privileges assigned.");
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
