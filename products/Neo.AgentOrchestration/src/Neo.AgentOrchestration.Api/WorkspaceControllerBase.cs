using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Neo.AgentOrchestration.Domain.Projects;
using Neo.AgentOrchestration.Domain.Work;
using NSwag.Annotations;

namespace Neo.AgentOrchestration.Api;

[ApiController]
[OpenApiOperationProcessor(typeof(WorkspaceContractProcessor))]
[Route("api/orchestration/v1/organizations/{organizationId:guid}/workspaces/{workspaceId:guid}")]
[Authorize(Policy = WorkspaceSecurity.Read)]
[RequestSizeLimit(262144)]
[ProducesResponseType<ProblemDetails>(400)]
[ProducesResponseType(401)]
[ProducesResponseType(403)]
[ProducesResponseType<ProblemDetails>(404)]
[ProducesResponseType<ProblemDetails>(409)]
[ProducesResponseType<ProblemDetails>(503)]
public abstract class WorkspaceControllerBase(ISender sender) : ControllerBase
{
    protected ISender Sender => sender;
    protected WorkspaceScope Scope => new(Guid.Parse((string)RouteData.Values["organizationId"]!), Guid.Parse((string)RouteData.Values["workspaceId"]!));
    protected WorkActor Actor()
    {
        var subject = WorkspaceSecurity.Subject(User) ??
            (WorkspaceSecurity.IsLocalDevelopment(HttpContext) && Request.Headers.TryGetValue("X-Orchestration-Local", out var local) && local == "true" ? "local-web" : null)
            ?? throw new UnauthorizedAccessException();
        var chats = Request.Headers["X-Orchestration-Chat"];
        if (chats.Count != 1 || string.IsNullOrWhiteSpace(chats[0]) || chats[0]!.Length > 200 || chats[0]!.Any(char.IsControl))
            throw new ArgumentException("One chat correlation identifier is required.");
        // Chat is correlation/ownership context, not authentication. The agent
        // identity is always the validated issuer's subject, never a body value.
        return new(subject, chats[0]!);
    }
    protected static T Value<T>(string value) where T : struct, Enum
        => Enum.GetNames<T>().Any(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase)) &&
           Enum.TryParse<T>(value, true, out var parsed) ? parsed : throw new ArgumentException("Unknown enum name.");
}
