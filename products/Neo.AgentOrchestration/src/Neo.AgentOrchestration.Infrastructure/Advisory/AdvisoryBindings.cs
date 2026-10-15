using Microsoft.Extensions.Configuration;
using Neo.AgentOrchestration.Domain.Projects;

namespace Neo.AgentOrchestration.Infrastructure.Advisory;

public static class AdvisoryBindings
{
    // Credentials/destination are operator configuration, never request fields.
    public static void RequireProject(IConfiguration config, WorkspaceScope scope, Guid projectId)
    {
        if (!SubmissionEnabled(config, scope, projectId)) throw new InvalidOperationException("Advisory is not enabled for this project.");
    }
    public static bool SubmissionEnabled(IConfiguration config, WorkspaceScope scope, Guid projectId)
    {
        if (!config.GetValue<bool>("AgnoAdvisory:Enabled")) return false;
        var matches = config.GetSection("AgnoAdvisory:Bindings").GetChildren().Count(x =>
            Guid.TryParse(x["OrganizationId"], out var org) && org == scope.OrganizationId &&
            Guid.TryParse(x["WorkspaceId"], out var work) && work == scope.WorkspaceId &&
            Guid.TryParse(x["ProjectId"], out var project) && project == projectId);
        return matches == 1;
    }
}
