using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Neo.AgentOrchestration.Contracts;

namespace Neo.AgentOrchestration.Web.Pages;

public sealed class TemplatesModel(OrchestrationClient client) : WorkPageModel(client)
{
    [BindProperty] public string Key { get; set; } = "";
    [BindProperty] public string Name { get; set; } = "";
    [BindProperty] public int ExpectedLatestRevision { get; set; }
    [BindProperty] public string DefinitionJson { get; set; } = Example;
    [BindProperty] public Guid TemplateId { get; set; }
    [BindProperty] public Guid RequestId { get; set; }
    [BindProperty] public string ProjectKey { get; set; } = "";
    [BindProperty] public string ProjectName { get; set; } = "";
    [BindProperty] public string ParametersJson { get; set; } = "{}";
    public ProjectTemplateCatalog? Templates { get; private set; }
    public ProjectTemplatePreview? Preview { get; private set; }
    public Task OnGetAsync(CancellationToken ct)
    { RequestId = Guid.NewGuid(); return Attempt(() => Load(ct)); }
    private async Task Load(CancellationToken ct)
    { await LoadCatalog(ct); Templates = await Get<ProjectTemplateCatalog>("templates", ct); }
    private InstantiateProjectTemplateRequest InstanceRequest()
    {
        if (string.IsNullOrWhiteSpace(ParametersJson) || ParametersJson.Length > 40000 || RequestId == Guid.Empty) throw new WebApiException(400);
        try
        {
            var values = JsonSerializer.Deserialize<Dictionary<string,string>>(ParametersJson) ?? throw new WebApiException(400);
            return new(RequestId, ProjectKey, ProjectName, values);
        }
        catch (JsonException) { throw new WebApiException(400); }
    }
    public async Task<IActionResult> OnPostPublishAsync(CancellationToken ct)
    {
        if (await Attempt(async () => _ = await Send<ProjectTemplateView>("templates", new PublishProjectTemplateRequest(Key,Name,ExpectedLatestRevision,DefinitionJson), ct)))
            return RedirectToPage(new { OrganizationId, WorkspaceId });
        await Attempt(() => Load(ct)); return Page();
    }
    public async Task<IActionResult> OnPostPreviewAsync(CancellationToken ct)
    {
        await Attempt(async () => Preview = await Send<ProjectTemplatePreview>($"templates/{TemplateId}/preview", InstanceRequest(), ct));
        await Attempt(() => Load(ct)); return Page();
    }
    public async Task<IActionResult> OnPostInstantiateAsync(CancellationToken ct)
    {
        TemplateInstantiationView? result = null;
        if (await Attempt(async () => result = await Send<TemplateInstantiationView>($"templates/{TemplateId}/instantiate", InstanceRequest(), ct)))
            return RedirectToPage("/Board", new { OrganizationId, WorkspaceId, ProjectId = result!.ProjectId });
        await Attempt(() => Load(ct)); return Page();
    }
    public const string Example = """
        {
          "parameters": ["feature"],
          "roles": [
            {"key":"template-developer","name":"Template Developer"},
            {"key":"template-reviewer","name":"Template Reviewer"}
          ],
          "items": [
            {"key":"STORY-1","title":"${feature}","domain":"product","type":"UserStory","dependsOn":[],"acceptanceCriteria":"Outcome is verified for ${project.name}"},
            {"key":"TASK-1","title":"Implement ${feature}","domain":"implementation","type":"Task","dependsOn":[],"parentKey":"STORY-1"},
            {"key":"TEST-1","title":"Verify ${feature}","domain":"quality","type":"Task","dependsOn":["TASK-1"],"parentKey":"STORY-1"}
          ],
          "workflows": [{"key":"delivery","name":"Delivery","transitions":[
            {"key":"review","fromRole":"template-developer","fromStatus":"Review","toRole":"template-reviewer","toStatus":"Ready","requireCommit":true,"requirePassingTests":true},
            {"key":"complete","fromRole":"template-reviewer","fromStatus":"Review","toRole":null,"toStatus":"Done","requirePassingTests":true}
          ]}]
        }
        """;
}
