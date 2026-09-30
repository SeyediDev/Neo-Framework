using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Neo.AgentOrchestration.Domain.Projects;
using Neo.AgentOrchestration.Domain.Work;
using Neo.Domain.Entities.Base;

namespace Neo.AgentOrchestration.Domain.Templates;

public sealed class ProjectTemplate : BaseEntity<Guid>
{
    public Guid OrganizationId { get; private set; }
    public Guid WorkspaceId { get; private set; }
    public string Key { get; private set; } = "";
    public string Name { get; private set; } = "";
    public int Revision { get; private set; }
    public string DefinitionJson { get; private set; } = "";
    public string ContentHash { get; private set; } = "";
    public string CreatedByAgent { get; private set; } = "";
    public string CreatedByChat { get; private set; } = "";
    public DateTimeOffset CreatedAtUtc { get; private set; }
    private ProjectTemplate() { }
    public static ProjectTemplate Publish(WorkspaceScope scope, string key, string name, int revision,
        string definitionJson, WorkActor actor, DateTimeOffset now)
    {
        if (revision < 1) throw new ArgumentException("Revision must be positive.");
        var definition = TemplateDefinition.Parse(definitionJson);
        var canonical = JsonSerializer.Serialize(definition, TemplateDefinition.Json);
        return new() { Id = Guid.NewGuid(), OrganizationId = scope.OrganizationId, WorkspaceId = scope.WorkspaceId,
            Key = ProjectRules.Key(key), Name = ProjectRules.Name(name), Revision = revision,
            DefinitionJson = canonical, ContentHash = Hash(canonical), CreatedByAgent = actor.AgentId,
            CreatedByChat = actor.ChatId, CreatedAtUtc = now.ToUniversalTime() };
    }
    public void RequireScope(WorkspaceScope scope) => scope.Require(OrganizationId, WorkspaceId);
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
}

public sealed record TemplateDefinition(string[] Parameters, TemplateRole[] Roles, TemplateItem[] Items, TemplateFlow[] Workflows)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, MaxDepth = 16 };
    public static TemplateDefinition Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 64000) throw new ArgumentException("Template definition must be 1-64000 characters.");
        TemplateDefinition value;
        try { value = JsonSerializer.Deserialize<TemplateDefinition>(json, Json) ?? throw new ArgumentException("Definition required."); }
        catch (JsonException) { throw new ArgumentException("Invalid template JSON schema."); }
        if (value.Parameters is null || value.Roles is null || value.Items is null || value.Workflows is null ||
            value.Parameters.Length > 16 || value.Roles.Length > 32 || value.Items.Length is < 1 or > 200 || value.Workflows.Length > 16 ||
            value.Roles.Any(x => x is null) || value.Items.Any(x => x is null) || value.Workflows.Any(x => x is null))
            throw new ArgumentException("Invalid template collections or limits.");
        if (value.Parameters.Any(x => x is null || !Regex.IsMatch(x, "^[a-zA-Z][a-zA-Z0-9_]{0,39}$", RegexOptions.CultureInvariant)) ||
            value.Parameters.Distinct(StringComparer.Ordinal).Count() != value.Parameters.Length)
            throw new ArgumentException("Parameter names must be unique simple identifiers.");
        Keys(value.Roles.Select(x => x.Key)); Keys(value.Items.Select(x => x.Key)); Keys(value.Workflows.Select(x => x.Key));
        foreach (var item in value.Items)
        {
            if (item.DependsOn is null || item.DependsOn.Length > 200) throw new ArgumentException("Dependencies required as an array.");
            if (item.ParentKey is not null && !value.Items.Any(x => SameKey(x.Key, item.ParentKey))) throw new ArgumentException("Unknown parent key.");
            if (item.DependsOn.Any(key => !value.Items.Any(x => SameKey(x.Key, key)))) throw new ArgumentException("Unknown dependency key.");
        }
        foreach (var flow in value.Workflows)
        {
            if (flow.Transitions is null || flow.Transitions.Length > 64 || flow.Transitions.Any(x => x is null)) throw new ArgumentException("Invalid transitions.");
            Keys(flow.Transitions.Select(x => x.Key));
            foreach (var transition in flow.Transitions)
                if (!value.Roles.Any(x => SameKey(x.Key, transition.FromRole)) ||
                    transition.ToRole is not null && !value.Roles.Any(x => SameKey(x.Key, transition.ToRole)))
                    throw new ArgumentException("Workflow refers to an undeclared role.");
        }
        return value;
    }
    private static void Keys(IEnumerable<string> keys)
    {
        var values = keys.Select(ProjectRules.Key).ToArray();
        if (values.Distinct(StringComparer.Ordinal).Count() != values.Length) throw new ArgumentException("Duplicate template keys.");
    }
    public static bool SameKey(string a, string b) => string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);
    public Dictionary<string,string> Bind(IReadOnlyDictionary<string,string>? supplied, string projectName)
    {
        supplied ??= new Dictionary<string,string>();
        if (supplied.Count != Parameters.Length || Parameters.Any(x => !supplied.ContainsKey(x)) ||
            supplied.Any(x => !Parameters.Contains(x.Key, StringComparer.Ordinal) || x.Value is null || x.Value.Length > 2000))
            throw new ArgumentException("Supply exactly the declared parameters, at most 2000 characters each.");
        var result = new Dictionary<string,string>(supplied, StringComparer.Ordinal) { ["project.name"] = projectName };
        return result;
    }
    public static string? Render(string? text, IReadOnlyDictionary<string,string> parameters)
        => text is null ? null : Regex.Replace(text, @"\$\{([^}]+)\}", match => parameters.TryGetValue(match.Groups[1].Value, out var value)
            ? value : throw new ArgumentException("Undeclared template parameter."), RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
}
public sealed record TemplateRole(string Key, string Name, string? ScopeDescription = null);
public sealed record TemplateItem(string Key, string Title, string Domain, string Type, string[] DependsOn,
    string? Description = null, string? AcceptanceCriteria = null, string? ParentKey = null, long? EstimatedSeconds = null, string Priority = "Normal");
public sealed record TemplateFlow(string Key, string Name, TemplateTransition[] Transitions);
public sealed record TemplateTransition(string Key, string FromRole, string FromStatus, string? ToRole, string ToStatus,
    bool RequireCommit = false, bool RequirePassingTests = false, bool RequireApproval = false, string? RequiredArtifact = null);

public sealed class TemplateInstantiation : BaseEntity<Guid>
{
    public Guid OrganizationId { get; private set; }
    public Guid WorkspaceId { get; private set; }
    public Guid TemplateId { get; private set; }
    public Guid ProjectId { get; private set; }
    public Guid RequestId { get; private set; }
    public string Fingerprint { get; private set; } = "";
    public string ParametersJson { get; private set; } = "";
    public string MappingJson { get; private set; } = "";
    public string CreatedByAgent { get; private set; } = "";
    public string CreatedByChat { get; private set; } = "";
    public DateTimeOffset CreatedAtUtc { get; private set; }
    private TemplateInstantiation() { }
    public static TemplateInstantiation Create(WorkspaceScope scope, ProjectTemplate template, Project project,
        Guid requestId, string fingerprint, string parameters, string mapping, WorkActor actor, DateTimeOffset now)
    {
        template.RequireScope(scope); project.RequireScope(scope);
        if (requestId == Guid.Empty || fingerprint.Length != 64 || parameters.Length > 40000 || mapping.Length > 40000)
            throw new ArgumentException("Invalid template instantiation receipt.");
        return new() { Id=Guid.NewGuid(), OrganizationId=scope.OrganizationId, WorkspaceId=scope.WorkspaceId,
            TemplateId=template.Id, ProjectId=project.Id, RequestId=requestId, Fingerprint=fingerprint,
            ParametersJson=parameters, MappingJson=mapping, CreatedByAgent=actor.AgentId, CreatedByChat=actor.ChatId, CreatedAtUtc=now.ToUniversalTime() };
    }
}
