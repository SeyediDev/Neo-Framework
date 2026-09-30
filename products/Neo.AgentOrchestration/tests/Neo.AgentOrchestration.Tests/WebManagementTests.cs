using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Neo.AgentOrchestration.Application.Work;
using Neo.AgentOrchestration.Contracts;
using Neo.AgentOrchestration.Domain.Work;
using Neo.AgentOrchestration.Web;
using Xunit;
using Fixture=Neo.AgentOrchestration.Tests.SqlPersistenceTests.Fixture;

namespace Neo.AgentOrchestration.Tests;
public sealed class WebManagementTests
{
    private static CancellationToken Ct=>TestContext.Current.CancellationToken;
    [Fact]
    public async Task Anonymous_pages_fail_closed_and_unconfigured_login_never_requests_a_token_in_html()
    {
        await using var web=new WebApplicationFactory<WebHost>().WithWebHostBuilder(b=>b.UseEnvironment("Testing"));
        using var client=web.CreateClient(new(){AllowAutoRedirect=false,BaseAddress=new("https://localhost")});
        var response=await client.GetAsync("/Workspace",Ct);
        Assert.Equal(HttpStatusCode.Redirect,response.StatusCode);Assert.Contains("/Login",response.Headers.Location!.ToString());
        var login=await client.GetAsync("/Login",Ct);Assert.Equal(HttpStatusCode.ServiceUnavailable,login.StatusCode);
        Assert.DoesNotContain("type=\"password\"",await login.Content.ReadAsStringAsync(Ct));
        Assert.Equal("no-store",login.Headers.CacheControl!.ToString());
        Assert.NotNull(web.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get(CookieAuthenticationDefaults.AuthenticationScheme).SessionStore);
    }
    [Fact]
    public async Task Oidc_challenge_uses_code_pkce_and_does_not_accept_external_return_urls()
    {
        await using var web=new WebApplicationFactory<WebHost>().WithWebHostBuilder(b=>
        {
            b.UseEnvironment("Testing");
            b.ConfigureAppConfiguration((_,c)=>c.AddInMemoryCollection(new Dictionary<string,string?>{
                ["WebAuthentication:Authority"]="https://identity.example.test",["WebAuthentication:ClientId"]="web-test"}));
            b.ConfigureServices(s=>s.PostConfigure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme,o=>
            {
                o.ConfigurationManager=new Microsoft.IdentityModel.Protocols.StaticConfigurationManager<OpenIdConnectConfiguration>(
                    new OpenIdConnectConfiguration{Issuer="https://identity.example.test",AuthorizationEndpoint="https://identity.example.test/authorize"});
            }));
        });
        using var client=web.CreateClient(new(){AllowAutoRedirect=false,BaseAddress=new("https://localhost")});
        var response=await client.GetAsync("/Login?returnUrl=https%3A%2F%2Fevil.example%2F",Ct);
        Assert.Equal(HttpStatusCode.Redirect,response.StatusCode);var location=response.Headers.Location!;
        Assert.Equal("identity.example.test",location.Host);
        var query=Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(location.Query);
        Assert.Equal("code",query["response_type"].ToString());Assert.False(string.IsNullOrWhiteSpace(query["code_challenge"]));
        var options=web.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(OpenIdConnectDefaults.AuthenticationScheme);
        Assert.Equal("/Workspace",options.StateDataFormat.Unprotect(query["state"].ToString())!.RedirectUri);
    }
    [Fact]
    public async Task Ticket_store_keeps_tokens_server_side_and_logout_removes_the_ticket()
    {
        using var cache=new MemoryCache(new MemoryCacheOptions());var store=new WebTicketStore(cache);
        var properties=new AuthenticationProperties{ExpiresUtc=DateTimeOffset.UtcNow.AddMinutes(1)};
        properties.StoreTokens([new(){Name="access_token",Value="private-test-token"}]);
        var ticket=new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new("sub","test")],"test")),properties,"test");
        var id=await store.StoreAsync(ticket);Assert.DoesNotContain("private-test-token",id);
        Assert.Equal("private-test-token",(await store.RetrieveAsync(id))!.Properties.GetTokenValue("access_token"));
        await store.RemoveAsync(id);Assert.Null(await store.RetrieveAsync(id));
    }
    [Theory]
    [InlineData("http://remote.example")]
    [InlineData("https://user:password@example.test")]
    [InlineData("https://example.test/?token=value")]
    public void Web_rejects_unsafe_api_destinations(string url)
        =>Assert.Throws<InvalidOperationException>(()=>WebIdentity.ApiAddress(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string,string?>{["OrchestrationApi:BaseUrl"]=url}).Build()));
    [Fact]
    public void Web_actor_survives_reauthentication_but_never_shares_ownership_between_subjects_or_issuers()
    {
        static ClaimsPrincipal User(string subject) => new(new ClaimsIdentity([new("sub",subject)],"oidc"));
        var first=WebIdentity.ChatId("https://identity.example.test",User("owner"));
        Assert.Equal(first,WebIdentity.ChatId("https://identity.example.test/",User("owner")));
        Assert.NotEqual(first,WebIdentity.ChatId("https://identity.example.test",User("other")));
        Assert.NotEqual(first,WebIdentity.ChatId("https://other.example.test",User("owner")));
        Assert.Throws<InvalidOperationException>(()=>WebIdentity.ChatId("https://identity.example.test",new ClaimsPrincipal()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sql_web_forms_manage_task_history_time_children_filters_and_archive_through_the_api(bool spa)
    {
        var f=await Fixture.Create();await using var web=await WebFixture.Create(f);
        if(spa) web.Client.DefaultRequestHeaders.Add("X-Neo-Navigation","1");
        var root=web.Root;var title="کار قابل پیگیری";var description="خط اول\nخط دوم <script>alert('x')</script>";
        var created=await web.Submit(root+"/new",null,new(){["ProjectId"]=f.Project.Id.ToString(),["Key"]="web-parent",["Title"]=title,["Domain"]="integration",["Description"]=description,["EstimatedSeconds"]="100"});
        Assert.Equal(HttpStatusCode.Redirect,created.StatusCode);var itemUrl=created.Headers.Location!.ToString();
        var id=Guid.Parse(itemUrl.Split('/').Last());
        var details=await web.ApiRead<WorkItemDetails>($"items/{id}");
        Assert.Equal(description,details.Item.Description);
        var child=await web.Submit(root+$"/new?ProjectId={f.Project.Id}&ParentWorkItemId={id}",null,new(){["ProjectId"]=f.Project.Id.ToString(),["ParentWorkItemId"]=id.ToString(),["Key"]="web-child",["Title"]="زیرتسک نمایان",["Domain"]="child-domain"});
        Assert.Equal(HttpStatusCode.Redirect,child.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect,(await web.Submit(itemUrl,"Status",new(){["NextStatus"]="Ready"})).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect,(await web.Submit(itemUrl,"Claim",new(){["RoleId"]=f.Role.Id.ToString(),["Branch"]="web-test"})).StatusCode);
        f.Clock.Advance(25);
        Assert.Equal(HttpStatusCode.Redirect,(await web.Submit(itemUrl,"StopTime",[])).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect,(await web.Submit(itemUrl,"Log",new(){["Message"]="سابقه قابل مشاهده"})).StatusCode);
        var html=await web.Html(itemUrl);
        Assert.Contains("زیرتسک نمایان",WebUtility.HtmlDecode(html));Assert.Contains("سابقه قابل مشاهده",WebUtility.HtmlDecode(html));
        Assert.DoesNotContain("<script>alert",html);Assert.Contains("&lt;script&gt;",html);
        Assert.Contains("خط دوم",WebUtility.HtmlDecode(html));Assert.DoesNotContain(web.Token,html);
        details=await web.ApiRead<WorkItemDetails>($"items/{id}");Assert.Equal(25,details.Item.ElapsedSeconds);Assert.Equal(25m,details.Item.BudgetUsedPercent);
        var board=await web.Html(root+$"?ProjectId={f.Project.Id}&Domain=integration&RoleId={f.Role.Id}");
        Assert.Contains($"data-item-id=\"{id}\"",board);Assert.DoesNotContain("زیرتسک نمایان",WebUtility.HtmlDecode(board));
        Assert.DoesNotContain($"data-item-id=\"{id}\"",await web.Html(root+"?Domain=missing"));
        Assert.DoesNotContain($"data-item-id=\"{id}\"",await web.Html(root+"?State=Done"));
        var stale=details.Item.Version;
        await web.Submit(itemUrl,"Log",new(){["Message"]="new version"});
        Assert.Equal(HttpStatusCode.Conflict,(await web.Submit(itemUrl,"Log",new(){["Version"]=stale.ToString(),["Message"]="stale write"})).StatusCode);
        // Parent cannot complete with unfinished children; Blocked is archivable.
        Assert.Equal(HttpStatusCode.Redirect,(await web.Submit(itemUrl,"Status",new(){["NextStatus"]="Blocked"})).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect,(await web.Submit(itemUrl,"Archive",[])).StatusCode);
        Assert.DoesNotContain($"data-item-id=\"{id}\"",await web.Html(root));
        Assert.Contains($"data-item-id=\"{id}\"",await web.Html(root+"?IncludeArchived=true"));
        Assert.Equal(HttpStatusCode.Redirect,(await web.Submit(itemUrl,"Restore",[])).StatusCode);
        await web.Export("board",await web.Html(root));await web.Export("item",await web.Html(itemUrl));
    }
    [Fact]
    public async Task Sql_web_settings_configure_roles_agents_workflows_and_show_a_real_simulation_run()
    {
        var f=await Fixture.Create();await using var web=await WebFixture.Create(f);
        var manage=web.Root+"/manage";
        Assert.Equal(HttpStatusCode.Redirect,(await web.Submit(manage,"Role",new(){["Key"]="reviewer",["Name"]="بازبین",["ScopeDescription"]="تأیید مستقل"})).StatusCode);
        var catalog=await web.ApiRead<WorkspaceCatalog>("catalog");var role=catalog.Roles.Single(x=>x.Key=="REVIEWER");
        Assert.Equal(HttpStatusCode.Redirect,(await web.Submit(manage,"Agent",new(){["RoleId"]=f.Role.Id.ToString(),["Key"]="dev",["Name"]="توسعه‌دهنده",["Provider"]="fake",["Instructions"]="فقط شبیه‌سازی"})).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect,(await web.Submit(manage,"Agent",new(){["RoleId"]=role.Id.ToString(),["Key"]="review",["Name"]="ایجنت بازبین",["Provider"]="fake"})).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect,(await web.Submit(manage,"Workflow",new(){["ProjectId"]=f.Project.Id.ToString(),["Key"]="flow",["Name"]="فلو وب"})).StatusCode);
        catalog=await web.ApiRead<WorkspaceCatalog>("catalog");var flow=catalog.Workflows.Single();
        Assert.Equal(HttpStatusCode.Redirect,(await web.Submit(manage,"Transition",new(){["Key"]="review",["FromRoleId"]=f.Role.Id.ToString(),["FromStatus"]="Review",["ToRoleId"]=role.Id.ToString(),["ToStatus"]="Ready",["Enabled"]="true"})).StatusCode);
        catalog=await web.ApiRead<WorkspaceCatalog>("catalog");flow=catalog.Workflows.Single();
        Assert.Equal(HttpStatusCode.Redirect,(await web.Submit(manage,"Transition",new(){["Key"]="done",["FromRoleId"]=role.Id.ToString(),["FromStatus"]="Review",["ToRoleId"]="",["ToStatus"]="Done",["Enabled"]="true"})).StatusCode);
        var item=await f.CreateItem("simulated");await f.Change(item.Id,new StatusChange(WorkItemStatus.Ready));
        var started=await web.Submit(web.Root+$"/items/{item.Id}","Run",new(){["RoleId"]=f.Role.Id.ToString(),["SimulationOutcome"]="Succeeded"});
        Assert.Equal(HttpStatusCode.Redirect,started.StatusCode);var runUrl=started.Headers.Location!.ToString();
        Assert.Contains("Queued",await web.Html(runUrl));
        await SqlRunTests.Drain(f,item.Id);
        var runs=await web.ApiRead<AgentRunDetails[]>($"items/{item.Id}/runs");Assert.Equal(2,runs.Length);Assert.Equal("Completed",runs[1].Run.Decision);
        var finishedUrl=web.Root+$"/runs/{runs[1].Run.Id}";
        Assert.Contains("Processed",await web.Html(finishedUrl));
        Assert.Equal(HttpStatusCode.Redirect,(await web.Submit(finishedUrl,"Return",[])).StatusCode);
        await web.Export("manage",await web.Html(manage));await web.Export("run",await web.Html(finishedUrl));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Web_writes_require_antiforgery_and_api_grants_and_cannot_cross_workspaces(bool spa)
    {
        var f=await Fixture.Create();await using var web=await WebFixture.Create(f,["read"]);
        if(spa) web.Client.DefaultRequestHeaders.Add("X-Neo-Navigation","1");
        var response=await web.Client.PostAsync(web.Root+"/new",new FormUrlEncodedContent(new Dictionary<string,string>{["Title"]="forged"}),Ct);
        Assert.Equal(HttpStatusCode.BadRequest,response.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await web.Submit(web.Root+"/new",null,new(){["ProjectId"]=f.Project.Id.ToString(),["Key"]="forbidden",["Title"]="No permission",["Domain"]="test"})).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden,(await web.Client.GetAsync($"/work/{f.Scope.OrganizationId}/{Guid.NewGuid()}",Ct)).StatusCode);
        Assert.Empty((await web.ApiRead<WorkBoard>("items")).Items);
    }
}

internal sealed class WebFixture : IAsyncDisposable
{
    public required ApiFixture Api {get;init;}
    public required HttpClient ApiClient {get;init;}
    public required WebApplicationFactory<WebHost> Host {get;init;}
    public required HttpClient Client {get;init;}
    public required Fixture Sql {get;init;}
    public required string Token {get;init;}
    public string Root=>$"/work/{Sql.Scope.OrganizationId}/{Sql.Scope.WorkspaceId}";
    private static CancellationToken Ct=>TestContext.Current.CancellationToken;
    public static Task<WebFixture> Create(Fixture f,string[]? grants=null)
    {
        var api=new ApiFixture(clock:f.Clock,sql:f.Connection,simulation:true);
        var apiClient=api.Client(f.Scope,grants??["read","write","configure","approve","execute"]);
        var token=apiClient.DefaultRequestHeaders.Authorization!.Parameter!;
        var host=new WebApplicationFactory<WebHost>().WithWebHostBuilder(b=>
        {
            b.UseEnvironment("Testing");
            b.ConfigureServices(s=>
            {
                s.AddSingleton(new WebTestSession(token));
                s.AddAuthentication(o=>{o.DefaultAuthenticateScheme="WebTest";o.DefaultChallengeScheme="WebTest";})
                    .AddScheme<AuthenticationSchemeOptions,WebTestAuthentication>("WebTest",_=>{});
                s.AddScoped(sp=>new OrchestrationClient(new HttpClient(api.Factory.Server.CreateHandler()){BaseAddress=new Uri("http://localhost/")},sp.GetRequiredService<IHttpContextAccessor>()));
            });
        });
        var client=host.CreateClient(new(){AllowAutoRedirect=false,BaseAddress=new("https://localhost")});
        return Task.FromResult(new WebFixture{Api=api,ApiClient=apiClient,Host=host,Client=client,Sql=f,Token=token});
    }
    public async Task<T> ApiRead<T>(string resource)=>(await ApiClient.GetFromJsonAsync<T>(ApiFixture.Root(Sql.Scope)+"/"+resource,Ct))!;
    public async Task<string> Html(string path)
    {
        var response=await Client.GetAsync(path,Ct);Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        return await response.Content.ReadAsStringAsync(Ct);
    }
    public async Task<HttpResponseMessage> Submit(string path,string? handler,Dictionary<string,string> values)
    {
        var html=await Html(path);
        var forms=Regex.Matches(html,@"<form\b.*?</form>",RegexOptions.Singleline|RegexOptions.IgnoreCase).Select(x=>x.Value);
        var form=forms.First(x=>handler is null ? !x.Contains("handler=") && x.Contains("__RequestVerificationToken") &&
            (Attr(x[..(x.IndexOf('>')+1)],"action").Length==0 || Attr(x[..(x.IndexOf('>')+1)],"action").Split('?')[0]==path.Split('?')[0]) : x.Contains("handler="+handler+"\""));
        var fields=new Dictionary<string,string>();
        foreach(Match input in Regex.Matches(form,@"<input\b[^>]*>",RegexOptions.IgnoreCase))
        {
            if(Attr(input.Value,"type")!="hidden")continue;
            var name=Attr(input.Value,"name");if(name.Length>0)fields[name]=Attr(input.Value,"value");
        }
        foreach(var field in values)fields[field.Key]=field.Value;
        var action=Attr(form[..(form.IndexOf('>')+1)],"action");
        if (action.Length==0) action=path;
        var response=await Client.PostAsync(action,new FormUrlEncodedContent(fields),Ct);
        if((int)response.StatusCode>=500)Assert.Fail("Web POST failed: "+await response.Content.ReadAsStringAsync(Ct));
        return response;
    }
    private static string Attr(string tag,string name)=>WebUtility.HtmlDecode(Regex.Match(tag,"\\b"+name+"=\"([^\"]*)\"").Groups[1].Value);
    public async Task Export(string name,string html)
    {
        // Optional non-secret rendered-fixture artifacts for visual review.
        var folder=Environment.GetEnvironmentVariable("NEO_WEB_PREVIEW_DIR");if(string.IsNullOrWhiteSpace(folder))return;
        Assert.DoesNotContain(Token,html);Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder,name+".html"),html,Ct);
        var css=await Client.GetStringAsync("/site.css",Ct);await File.WriteAllTextAsync(Path.Combine(folder,"site.css"),css,Ct);
    }
    public async ValueTask DisposeAsync(){Client.Dispose();ApiClient.Dispose();await Host.DisposeAsync();await Api.DisposeAsync();}
}
internal sealed record WebTestSession(string Token);
// Test assembly only. No header, test sign-in endpoint or bypass exists in Web.
internal sealed class WebTestAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options,ILoggerFactory logger,UrlEncoder encoder,WebTestSession session)
    :AuthenticationHandler<AuthenticationSchemeOptions>(options,logger,encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var properties=new AuthenticationProperties();properties.Items[WebIdentity.ChatKey]="web:test";
        properties.StoreTokens([new(){Name="access_token",Value=session.Token}]);
        var principal=new ClaimsPrincipal(new ClaimsIdentity([new("sub","agent-a"),new(ClaimTypes.NameIdentifier,"agent-a")],Scheme.Name));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal,properties,Scheme.Name)));
    }
}
