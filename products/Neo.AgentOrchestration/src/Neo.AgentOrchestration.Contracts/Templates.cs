namespace Neo.AgentOrchestration.Contracts;

public sealed record PublishProjectTemplateRequest(string Key, string Name, int ExpectedLatestRevision, string DefinitionJson);
public sealed record InstantiateProjectTemplateRequest(Guid RequestId, string ProjectKey, string ProjectName, Dictionary<string,string>? Parameters = null);
public sealed record ProjectTemplateView(Guid Id, string Key, string Name, int Revision, string DefinitionJson, string ContentHash, DateTimeOffset CreatedAtUtc);
public sealed record TemplateInstantiationView(Guid Id, Guid TemplateId, Guid ProjectId, Guid RequestId, string ParametersJson, string MappingJson, DateTimeOffset CreatedAtUtc);
public sealed record ProjectTemplateCatalog(IReadOnlyList<ProjectTemplateView> Templates, IReadOnlyList<TemplateInstantiationView> Instantiations);
public sealed record ProjectTemplatePreview(Guid TemplateId, int Revision, string ContentHash, string ProjectKey, string ProjectName,
    IReadOnlyList<TemplatePreviewItem> Items, IReadOnlyList<string> RoleKeys, IReadOnlyList<string> WorkflowKeys);
public sealed record TemplatePreviewItem(string Key, string Title, string Type, string Domain, string? Description,
    string? AcceptanceCriteria, string? ParentKey, IReadOnlyList<string> DependsOn);
