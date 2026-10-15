using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Neo.AgentOrchestration.Application.Advisory;
using Neo.AgentOrchestration.Contracts;
using Neo.AgentOrchestration.Domain.Work;

namespace Neo.AgentOrchestration.Api;

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdvisoryRequestsController(ISender sender) : WorkspaceControllerBase(sender)
{
    [HttpGet("items/{id:guid}/advisories")]
    public Task<AdvisoryRequestCatalog> List(Guid id, CancellationToken ct)
        => Sender.Send(new ListAdvisories(Scope, id, Reader()), ct);
    [HttpGet("advisories/{id:guid}")]
    public Task<AdvisoryRequestView> Get(Guid id, CancellationToken ct) => Sender.Send(new GetAdvisory(Scope, id, Reader()), ct);
    private WorkActor Reader() => new(WorkspaceSecurity.Subject(User) ??
        (WorkspaceSecurity.IsLocalDevelopment(HttpContext) && Request.Headers["X-Orchestration-Local"] == "true" ? "local-web" : null)
        ?? throw new UnauthorizedAccessException(), "read-only");
    [HttpPost("items/{id:guid}/advisories"), Authorize(Policy = WorkspaceSecurity.Execute)]
    public async Task<ActionResult<AdvisoryRequestView>> Create(Guid id, CreateAdvisoryRequest body, CancellationToken ct)
    {
        var value = await Sender.Send(new StartAdvisory(Scope, id, Actor(), body), ct);
        return AcceptedAtAction(nameof(Get), new { organizationId = Scope.OrganizationId, workspaceId = Scope.WorkspaceId, id = value.Id }, value);
    }
    [HttpPost("advisories/{id:guid}/cancel"), Authorize(Policy = WorkspaceSecurity.Execute)]
    public Task<AdvisoryRequestView> Cancel(Guid id, VersionRequest body, CancellationToken ct)
        => Sender.Send(new CancelAdvisory(Scope, id, Actor(), body.ExpectedVersion), ct);
}
