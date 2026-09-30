using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Neo.AgentOrchestration.Application.Templates;
using Neo.AgentOrchestration.Application.Work;
using Neo.AgentOrchestration.Domain.Templates;
using Neo.AgentOrchestration.Contracts;
using Neo.AgentOrchestration.Web.Pages;
using Xunit;

namespace Neo.AgentOrchestration.Tests;

public sealed class ProjectTemplateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Theory]
    [InlineData("""{"parameters":[],"roles":[],"items":[],"workflows":[]}""")]
    [InlineData("""{"parameters":[],"roles":[],"items":[null],"workflows":[]}""")]
    [InlineData("""{"parameters":["x","x"],"roles":[],"items":[{"key":"a","title":"A","domain":"d","type":"Task","dependsOn":[]}],"workflows":[]}""")]
    [InlineData("""{"parameters":[],"roles":[],"items":[{"key":"a","title":"A","domain":"d","type":"Task","dependsOn":["missing"]}],"workflows":[]}""")]
    public void Invalid_definition_is_rejected(string json)
        => Assert.Throws<ArgumentException>(() => TemplateDefinition.Parse(json));

    [Fact]
    public async Task Sql_templates_preview_without_writes_instantiate_atomically_and_keep_immutable_provenance()
    {
        var f = await SqlPersistenceTests.Fixture.Create();
        var handler = new TemplateHandlers(f.Store, f.Clock);
        var published = await handler.Handle(new PublishProjectTemplate(f.Scope, new("delivery", "Delivery", 0, TemplatesModel.Example), f.Actor), Ct);
        var request = new InstantiateProjectTemplateRequest(Guid.NewGuid(), "generated", "Generated project", new() { ["feature"]="A feature" });
        var preview = await handler.Handle(new PreviewProjectTemplate(f.Scope,published.Id,request,f.Actor), Ct);
        Assert.Equal(3, preview.Items.Count);
        Assert.Contains(preview.Items, x => x.Title == "A feature" && x.Type == "UserStory");
        await using (var before = f.Factory.CreateDbContext())
        {
            Assert.Equal(1, await before.Projects.CountAsync(x => x.WorkspaceId == f.Scope.WorkspaceId, Ct));
            Assert.False(await before.WorkItems.AnyAsync(x => x.WorkspaceId == f.Scope.WorkspaceId, Ct));
        }
        var results = await Task.WhenAll(Enumerable.Range(0,3).Select(_ => handler.Handle(new InstantiateProjectTemplate(f.Scope,published.Id,request,f.Actor), Ct)));
        Assert.Single(results.Select(x => x.Id).Distinct());
        var receipt = results[0];
        await Assert.ThrowsAsync<WorkItemConflictException>(() => handler.Handle(new InstantiateProjectTemplate(f.Scope,published.Id,request with { ProjectName="Changed" },f.Actor),Ct));
        await Assert.ThrowsAsync<WorkItemConflictException>(() => handler.Handle(new InstantiateProjectTemplate(f.Scope,published.Id,request,new("other","other")),Ct));
        var v2 = await handler.Handle(new PublishProjectTemplate(f.Scope,new("delivery","Delivery next",1,TemplatesModel.Example.Replace("Outcome is verified", "New acceptance",StringComparison.Ordinal)),f.Actor),Ct);
        Assert.Equal(2,v2.Revision);
        Assert.NotEqual(published.Id,v2.Id);
        await Assert.ThrowsAsync<WorkItemConflictException>(() => handler.Handle(new PublishProjectTemplate(f.Scope,new("delivery","Stale",1,TemplatesModel.Example),f.Actor),Ct));
        await using var db = f.Factory.CreateDbContext();
        var items=await db.WorkItems.Where(x=>x.ProjectId==receipt.ProjectId).Include(x=>x.Dependencies).Include(x=>x.Logs).ToArrayAsync(Ct);
        Assert.Equal(3,items.Length);
        Assert.All(items,x=>Assert.Null(x.OwnerRoleId));
        Assert.Equal(2,items.Count(x=>x.ParentWorkItemId.HasValue));
        Assert.Single(items.Single(x=>x.Key=="TEST-1").Dependencies);
        Assert.Contains("Outcome is verified",items.Single(x=>x.Key=="STORY-1").AcceptanceCriteria!);
        Assert.All(items,x=>Assert.Contains(x.Logs,l=>l.Message.Contains(published.ContentHash)));
        Assert.False((await db.Workflows.SingleAsync(x=>x.ProjectId==receipt.ProjectId,Ct)).IsEnabled);
        Assert.False(await db.AgentRuns.AnyAsync(x=>x.ProjectId==receipt.ProjectId,Ct));
        Assert.Equal(published.Id,(await db.Set<TemplateInstantiation>().SingleAsync(x=>x.ProjectId==receipt.ProjectId,Ct)).TemplateId);
    }

    [Fact]
    public async Task Sql_invalid_graph_and_parameters_roll_back_without_partial_project_or_roles()
    {
        var f=await SqlPersistenceTests.Fixture.Create();
        var handler=new TemplateHandlers(f.Store,f.Clock);
        var cycle=TemplatesModel.Example.Replace(@"""dependsOn"":[""TASK-1""]",@"""dependsOn"":[""TEST-1""]",StringComparison.Ordinal);
        await Assert.ThrowsAnyAsync<Exception>(()=>handler.Handle(new PublishProjectTemplate(f.Scope,new("invalid","Invalid",0,cycle),f.Actor),Ct));
        var published=await handler.Handle(new PublishProjectTemplate(f.Scope,new("valid","Valid",0,TemplatesModel.Example),f.Actor),Ct);
        var bad=new InstantiateProjectTemplateRequest(Guid.NewGuid(),"must-not-exist","Invalid",new(){["feature"]=new string('x',300)});
        await Assert.ThrowsAnyAsync<ArgumentException>(()=>handler.Handle(new InstantiateProjectTemplate(f.Scope,published.Id,bad,f.Actor),Ct));
        await using var db=f.Factory.CreateDbContext();
        Assert.Equal(1,await db.Projects.CountAsync(x=>x.WorkspaceId==f.Scope.WorkspaceId,Ct));
        Assert.Equal(1,await db.Roles.CountAsync(x=>x.WorkspaceId==f.Scope.WorkspaceId,Ct));
        Assert.Equal(1,await db.Set<ProjectTemplate>().CountAsync(x=>x.WorkspaceId==f.Scope.WorkspaceId,Ct));
        Assert.False(await db.Set<TemplateInstantiation>().AnyAsync(x=>x.WorkspaceId==f.Scope.WorkspaceId,Ct));
    }

    [Fact]
    public async Task Http_and_web_templates_enforce_scope_permissions_and_display_preview_and_provenance()
    {
        var f=await SqlPersistenceTests.Fixture.Create();
        await using var web=await WebFixture.Create(f);
        var path=web.Root+"/templates";
        Assert.Equal(HttpStatusCode.Redirect,(await web.Submit(path,"Publish",new(){
            ["Key"]="web-template",["Name"]="Web template",["ExpectedLatestRevision"]="0",["DefinitionJson"]=TemplatesModel.Example})).StatusCode);
        var catalog=await web.ApiRead<ProjectTemplateCatalog>("templates");
        var template=Assert.Single(catalog.Templates);
        var requestId=Guid.NewGuid().ToString();
        var fields=new Dictionary<string,string>{["TemplateId"]=template.Id.ToString(),["RequestId"]=requestId,["ProjectKey"]="web-generated",["ProjectName"]="Web generated",["ParametersJson"]="{\"feature\":\"Visible feature\"}"};
        var preview=await web.Submit(path,"Preview",fields);
        Assert.Equal(HttpStatusCode.OK,preview.StatusCode);
        Assert.Contains("Visible feature",await preview.Content.ReadAsStringAsync(Ct));
        // Use the actual preview form's anti-forgery token and pinned parameters.
        var html=await preview.Content.ReadAsStringAsync(Ct);
        var form=System.Text.RegularExpressions.Regex.Matches(html,@"<form\b.*?</form>",System.Text.RegularExpressions.RegexOptions.Singleline).Select(x=>x.Value).Single(x=>x.Contains("handler=Instantiate"));
        var token=WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Match(form,"name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        Assert.NotEmpty(token);
        fields["__RequestVerificationToken"]=token;
        var created=await web.Client.PostAsync(path+"?handler=Instantiate",new FormUrlEncodedContent(fields),Ct);
        Assert.Equal(HttpStatusCode.Redirect,created.StatusCode);
        Assert.Contains("ProjectId",created.Headers.Location!.OriginalString);
        Assert.Contains("web-generated".ToUpperInvariant(),(await web.ApiRead<WorkspaceCatalog>("catalog")).Projects.Select(x=>x.Key));
        var after=await web.Html(path);
        Assert.Contains("Web generated",after);
        await using var reader=await WebFixture.Create(f,["read"]);
        Assert.Equal(HttpStatusCode.Forbidden,(await reader.Submit(reader.Root+"/templates","Publish",new(){
            ["Key"]="forbidden",["Name"]="Forbidden",["ExpectedLatestRevision"]="0",["DefinitionJson"]=TemplatesModel.Example})).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await web.ApiClient.GetAsync(ApiFixture.Root(new(f.Scope.OrganizationId,Guid.NewGuid()))+"/templates",Ct)).StatusCode);
    }
}
