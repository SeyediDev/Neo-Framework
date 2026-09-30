using System.Text.Json;
using MediatR;
using Neo.AgentOrchestration.Application.Work;
using Neo.AgentOrchestration.Contracts;
using Neo.AgentOrchestration.Domain.Agents;
using Neo.AgentOrchestration.Domain.Projects;
using Neo.AgentOrchestration.Domain.Templates;
using Neo.AgentOrchestration.Domain.Work;
using Neo.AgentOrchestration.Domain.Workflows;

namespace Neo.AgentOrchestration.Application.Templates;

public interface ITemplateSession : IWorkItemSession
{
    Task<IReadOnlyList<ProjectTemplate>> GetTemplatesAsync(CancellationToken ct);
    Task<IReadOnlyList<TemplateInstantiation>> GetTemplateInstantiationsAsync(CancellationToken ct);
    void Add(ProjectTemplate template);
    void Add(TemplateInstantiation instantiation);
}
public sealed record GetProjectTemplates(WorkspaceScope Scope) : IRequest<ProjectTemplateCatalog>;
public sealed record PublishProjectTemplate(WorkspaceScope Scope, PublishProjectTemplateRequest Value, WorkActor Actor) : IRequest<ProjectTemplateView>;
public sealed record PreviewProjectTemplate(WorkspaceScope Scope, Guid TemplateId, InstantiateProjectTemplateRequest Value, WorkActor Actor) : IRequest<ProjectTemplatePreview>;
public sealed record InstantiateProjectTemplate(WorkspaceScope Scope, Guid TemplateId, InstantiateProjectTemplateRequest Value, WorkActor Actor) : IRequest<TemplateInstantiationView>;

public sealed class TemplateHandlers(IWorkspaceWorkStore store, TimeProvider clock) :
    IRequestHandler<GetProjectTemplates, ProjectTemplateCatalog>, IRequestHandler<PublishProjectTemplate, ProjectTemplateView>,
    IRequestHandler<PreviewProjectTemplate, ProjectTemplatePreview>, IRequestHandler<InstantiateProjectTemplate, TemplateInstantiationView>
{
    public Task<ProjectTemplateCatalog> Handle(GetProjectTemplates r, CancellationToken ct)
        => store.ExecuteAsync(r.Scope, async (raw, token) =>
        {
            var session = Session(raw);
            return new ProjectTemplateCatalog((await session.GetTemplatesAsync(token)).Select(View).ToArray(),
                (await session.GetTemplateInstantiationsAsync(token)).Select(View).ToArray());
        }, ct);
    public Task<ProjectTemplateView> Handle(PublishProjectTemplate r, CancellationToken ct)
        => store.ExecuteAsync(r.Scope, async (raw, token) =>
        {
            var session = Session(raw);
            var versions = await session.GetTemplatesAsync(token);
            var latest = versions.Where(x => TemplateDefinition.SameKey(x.Key, r.Value.Key)).Select(x => x.Revision).DefaultIfEmpty(0).Max();
            if (latest != r.Value.ExpectedLatestRevision) throw new WorkItemConflictException("Template revision changed; reload before publishing.");
            var template = ProjectTemplate.Publish(r.Scope, r.Value.Key, r.Value.Name, checked(latest + 1), r.Value.DefinitionJson, r.Actor, clock.GetUtcNow());
            var definition = TemplateDefinition.Parse(template.DefinitionJson);
            var validation = new InstantiateProjectTemplateRequest(Guid.NewGuid(), "template-validation", "Template validation",
                definition.Parameters.ToDictionary(x => x, _ => "sample", StringComparer.Ordinal));
            // Materialize only in memory to validate domain rules/graphs. Existing
            // workspace roles are checked during preview/instantiation, not publish.
            _ = Build(r.Scope, template, validation, await session.GetWorkspaceAsync(token), [], r.Actor, clock.GetUtcNow());
            session.Add(template);
            return View(template);
        }, ct);
    public Task<ProjectTemplatePreview> Handle(PreviewProjectTemplate r, CancellationToken ct)
        => store.ExecuteAsync(r.Scope, async (raw, token) =>
        {
            var session = Session(raw); var template = await Find(session, r.TemplateId, token);
            var plan = Build(r.Scope, template, r.Value, await session.GetWorkspaceAsync(token), await session.GetRolesAsync(token), r.Actor, clock.GetUtcNow());
            if ((await session.GetProjectsAsync(token)).Any(x => x.Key == plan.Project.Key)) throw new WorkItemConflictException("Project key already exists.");
            return new ProjectTemplatePreview(template.Id, template.Revision, template.ContentHash, plan.Project.Key, plan.Project.Name,
                plan.Items.Select(item => new TemplatePreviewItem(item.Key, item.Title, item.Type.ToString(), item.Domain,
                    item.Description, item.AcceptanceCriteria, plan.Items.SingleOrDefault(x => x.Id == item.ParentWorkItemId)?.Key,
                    item.Dependencies.Select(d => plan.Items.Single(x => x.Id == d.DependsOnWorkItemId).Key).ToArray())).ToArray(),
                plan.Roles.Select(x => x.Key).ToArray(), plan.Flows.Select(x => x.Key).ToArray());
        }, ct);
    public Task<TemplateInstantiationView> Handle(InstantiateProjectTemplate r, CancellationToken ct)
        => store.ExecuteAsync(r.Scope, async (raw, token) =>
        {
            var session = Session(raw);
            if (r.Value.RequestId == Guid.Empty) throw new ArgumentException("Stable requestId required.");
            var parameters = JsonSerializer.Serialize((r.Value.Parameters ?? []).OrderBy(x => x.Key, StringComparer.Ordinal).ToDictionary(x => x.Key, x => x.Value), TemplateDefinition.Json);
            var fingerprint = ProjectTemplate.Hash(JsonSerializer.Serialize(new { r.TemplateId, r.Value.ProjectKey, r.Value.ProjectName, parameters }));
            var receipt = (await session.GetTemplateInstantiationsAsync(token)).SingleOrDefault(x => x.RequestId == r.Value.RequestId);
            if (receipt is not null)
            {
                if (receipt.Fingerprint != fingerprint || receipt.CreatedByAgent != r.Actor.AgentId || receipt.CreatedByChat != r.Actor.ChatId)
                    throw new WorkItemConflictException("Instantiation requestId already used with different content/source.");
                return View(receipt);
            }
            var template = await Find(session, r.TemplateId, token);
            var roles = await session.GetRolesAsync(token);
            var plan = Build(r.Scope, template, r.Value, await session.GetWorkspaceAsync(token), roles, r.Actor, clock.GetUtcNow());
            if ((await session.GetProjectsAsync(token)).Any(x => x.Key == plan.Project.Key)) throw new WorkItemConflictException("Project key already exists.");
            session.Add(plan.Project);
            foreach (var role in plan.Roles.Where(x => !roles.Any(existing => existing.Id == x.Id))) session.Add(role);
            foreach (var item in plan.Items) session.Add(item);
            foreach (var flow in plan.Flows) session.Add(flow);
            var mapping = JsonSerializer.Serialize(new { items = plan.Items.ToDictionary(x => x.Key, x => x.Id), roles = plan.Roles.ToDictionary(x => x.Key, x => x.Id), workflows = plan.Flows.ToDictionary(x => x.Key, x => x.Id) }, TemplateDefinition.Json);
            var created = TemplateInstantiation.Create(r.Scope, template, plan.Project, r.Value.RequestId, fingerprint, parameters, mapping, r.Actor, clock.GetUtcNow());
            session.Add(created);
            return View(created);
        }, ct);

    private static Plan Build(WorkspaceScope scope, ProjectTemplate template, InstantiateProjectTemplateRequest request,
        Domain.Projects.Workspace workspace, IReadOnlyList<RoleProfile> existingRoles, WorkActor actor, DateTimeOffset now)
    {
        template.RequireScope(scope);
        var project = Project.Create(workspace, request.ProjectKey, request.ProjectName);
        var definition = TemplateDefinition.Parse(template.DefinitionJson);
        var parameters = definition.Bind(request.Parameters, project.Name);
        var roles = new List<RoleProfile>();
        foreach (var value in definition.Roles)
        {
            var proposed = RoleProfile.Create(workspace, value.Key, value.Name, value.ScopeDescription);
            var existing = existingRoles.SingleOrDefault(x => TemplateDefinition.SameKey(x.Key, proposed.Key));
            if (existing is not null && (!existing.IsEnabled || existing.Name != proposed.Name || existing.ScopeDescription != proposed.ScopeDescription))
                throw new WorkItemConflictException("An existing workspace role conflicts with the template; no role was overwritten.");
            roles.Add(existing ?? proposed);
        }
        var items = new Dictionary<string,WorkItem>(StringComparer.OrdinalIgnoreCase);
        var pending = definition.Items.ToList();
        while (pending.Count > 0)
        {
            var ready = pending.Where(x => x.ParentKey is null || items.ContainsKey(x.ParentKey.Trim())).ToArray();
            if (ready.Length == 0) throw new ArgumentException("Template parent graph contains a cycle.");
            foreach (var value in ready)
            {
                var item = WorkItem.Create(scope, project, value.Key, TemplateDefinition.Render(value.Title, parameters)!, value.Domain,
                    actor, now, TemplateDefinition.Render(value.Description, parameters), Named<WorkItemPriority>(value.Priority),
                    value.ParentKey is null ? null : items[value.ParentKey.Trim()], value.EstimatedSeconds,
                    Named<WorkItemType>(value.Type), TemplateDefinition.Render(value.AcceptanceCriteria, parameters));
                item.AddLog(scope, actor, $"Template {template.Key} revision {template.Revision}; id={template.Id:D}; hash={template.ContentHash}", now);
                items.Add(item.Key, item); pending.Remove(value);
            }
        }
        foreach (var value in definition.Items)
            foreach (var dependency in value.DependsOn)
                items[value.Key.Trim()].AddDependency(scope, actor, items[dependency.Trim()], items.Values.ToArray(), now);
        var flows = new List<WorkflowDefinition>();
        foreach (var value in definition.Workflows)
        {
            var flow = WorkflowDefinition.Create(scope, project, value.Key, TemplateDefinition.Render(value.Name, parameters)!);
            foreach (var t in value.Transitions)
                flow.ConfigureTransition(scope, t.Key, roles.Single(x => TemplateDefinition.SameKey(x.Key, t.FromRole)), Named<WorkItemStatus>(t.FromStatus),
                    t.ToRole is null ? null : roles.Single(x => TemplateDefinition.SameKey(x.Key, t.ToRole)), Named<WorkItemStatus>(t.ToStatus),
                    new(t.RequireCommit, t.RequirePassingTests, t.RequireApproval, TemplateDefinition.Render(t.RequiredArtifact, parameters)));
            flow.Update(scope, flow.Name, false); // Explicit operator activation; never dispatch from a template.
            flows.Add(flow);
        }
        return new(project, roles, items.Values.ToArray(), flows);
    }
    private static T Named<T>(string value) where T : struct, Enum
        => Enum.GetNames<T>().Any(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase)) && Enum.TryParse<T>(value, true, out var parsed)
            ? parsed : throw new ArgumentException("Unknown template enum name.");
    private static ITemplateSession Session(IWorkItemSession session) => session as ITemplateSession ?? throw new InvalidOperationException("Template persistence is unavailable.");
    private static async Task<ProjectTemplate> Find(ITemplateSession s, Guid id, CancellationToken ct)
        => (await s.GetTemplatesAsync(ct)).SingleOrDefault(x => x.Id == id) ?? throw new KeyNotFoundException("Template not found.");
    private static ProjectTemplateView View(ProjectTemplate x) => new(x.Id,x.Key,x.Name,x.Revision,x.DefinitionJson,x.ContentHash,x.CreatedAtUtc);
    private static TemplateInstantiationView View(TemplateInstantiation x) => new(x.Id,x.TemplateId,x.ProjectId,x.RequestId,x.ParametersJson,x.MappingJson,x.CreatedAtUtc);
    private sealed record Plan(Project Project, IReadOnlyList<RoleProfile> Roles, IReadOnlyList<WorkItem> Items, IReadOnlyList<WorkflowDefinition> Flows);
}
