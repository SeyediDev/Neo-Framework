using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Neo.AgentOrchestration.Application.Templates;
using Neo.AgentOrchestration.Contracts;

namespace Neo.AgentOrchestration.Api;

public sealed class ProjectTemplatesController(ISender sender) : WorkspaceControllerBase(sender)
{
    [HttpGet("templates")]
    public Task<ProjectTemplateCatalog> Catalog(CancellationToken ct) => Sender.Send(new GetProjectTemplates(Scope), ct);
    [HttpPost("templates"), Authorize(Policy = WorkspaceSecurity.Configure)]
    public Task<ProjectTemplateView> Publish(PublishProjectTemplateRequest body, CancellationToken ct)
        => Sender.Send(new PublishProjectTemplate(Scope, body, Actor()), ct);
    [HttpPost("templates/{id:guid}/preview"), Authorize(Policy = WorkspaceSecurity.Configure)]
    public Task<ProjectTemplatePreview> Preview(Guid id, InstantiateProjectTemplateRequest body, CancellationToken ct)
        => Sender.Send(new PreviewProjectTemplate(Scope, id, body, Actor()), ct);
    [HttpPost("templates/{id:guid}/instantiate"), Authorize(Policy = WorkspaceSecurity.Configure), Authorize(Policy = WorkspaceSecurity.Write)]
    public Task<TemplateInstantiationView> Instantiate(Guid id, InstantiateProjectTemplateRequest body, CancellationToken ct)
        => Sender.Send(new InstantiateProjectTemplate(Scope, id, body, Actor()), ct);
}
