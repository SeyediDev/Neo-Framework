using System.Net;
using System.Net.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Neo.AgentOrchestration.Application.Delivery;
using Neo.AgentOrchestration.Application.Work;
using Neo.AgentOrchestration.Contracts;
using Neo.AgentOrchestration.Domain.Advisory;
using Neo.AgentOrchestration.Domain.Work;
using Neo.AgentOrchestration.Infrastructure.Advisory;
using Neo.AgentOrchestration.Infrastructure.Delivery;
using Neo.AgentOrchestration.Infrastructure.Persistence;
using Neo.Application.Features.Outbox;
using Neo.Domain.Entities.Common;
using Xunit;
using Fixture = Neo.AgentOrchestration.Tests.SqlPersistenceTests.Fixture;

namespace Neo.AgentOrchestration.Tests;

public sealed class SqlAdvisoryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    internal static IConfiguration Config(Fixture f, string? tokenFile = null) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string,string?> {
            ["AgnoAdvisory:Enabled"]="true", ["AgnoAdvisory:BaseUrl"]="http://127.0.0.1:17840/",
            ["AgnoAdvisory:TokenFile"]=tokenFile,
            ["AgnoAdvisory:Bindings:pilot:OrganizationId"]=f.Scope.OrganizationId.ToString(),
            ["AgnoAdvisory:Bindings:pilot:WorkspaceId"]=f.Scope.WorkspaceId.ToString(),
            ["AgnoAdvisory:Bindings:pilot:ProjectId"]=f.Project.Id.ToString() }).Build();

    [Fact]
    public async Task Private_intake_is_atomic_idempotent_scoped_and_does_not_change_work_or_usage()
    {
        var f = await Fixture.Create(); var item = await f.CreateItem("advisory-intake");
        var store = new SqlAdvisoryRequests(f.Factory, f.Clock, Config(f));
        var body = new CreateAdvisoryRequest(Guid.NewGuid(), item.Version, "Reviewed short brief", true);
        var first = await store.CreateAsync(f.Scope, item.Id, f.Actor, body, Ct);
        Assert.Equal(first.Id, (await store.CreateAsync(f.Scope, item.Id, f.Actor, body, Ct)).Id);
        await Assert.ThrowsAsync<DeliveryConflictException>(() => store.CreateAsync(f.Scope, item.Id, f.Actor, body with { Brief="changed" }, Ct));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.CreateAsync(f.Scope, item.Id, f.Actor, body with { RequestId=Guid.NewGuid() }, Ct));
        var other = new WorkActor("other-user", "other-chat");
        Assert.Empty((await store.ListAsync(f.Scope, item.Id, other, Ct)).Requests);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => store.GetAsync(f.Scope, first.Id, other, Ct));
        var current = await f.Handlers.Handle(new GetWorkItem(f.Scope,item.Id),Ct);
        Assert.Equal(item.Version,current.Item.Version); Assert.Equal(item.Status,current.Item.Status);
        Assert.Null(current.Item.OwnerAgentId); Assert.Empty(current.TimeEntries);
        await using var db = f.Factory.CreateDbContext();
        var delivery = await db.Deliveries.Include(x=>x.Outbox).SingleAsync(x=>x.WorkItemId==item.Id,Ct);
        Assert.DoesNotContain(body.Brief, delivery.Outbox.MessageContent);
        Assert.Null(delivery.AgentRunId); Assert.Empty(await db.AgentRuns.Where(x=>x.WorkItemId==item.Id).ToArrayAsync(Ct));
        Assert.Null(first.InputTokens); Assert.Null(first.OutputTokens);
    }

    [Fact]
    public async Task Uncertain_post_and_recovery_never_resend()
    {
        var f = await Fixture.Create(); var item = await f.CreateItem("advisory-recovery");
        var path = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(path,"fixture-token-not-a-real-credential",Ct);
            var config=Config(f,path); var store=new SqlAdvisoryRequests(f.Factory,f.Clock,config);
            var request=await store.CreateAsync(f.Scope,item.Id,f.Actor,new(Guid.NewGuid(),item.Version,"Synthetic acceptance brief",true),Ct);
            await using var db=f.Factory.CreateDbContext();
            var row=await db.Deliveries.SingleAsync(x=>x.WorkItemId==item.Id,Ct);
            var op=new WorkDeliveryExecution(row.Id,f.Scope,f.Project.Id,item.Id,item.Version,row.Kind);
            var wire=new Wire(_=>throw new HttpRequestException("raw-provider-error-must-not-leak"));
            var transport=new AdvisoryTransport(new HttpClient(wire),store,config,f.Clock);
            await transport.SendAsync(op,Ct); await transport.SendAsync(op,Ct);
            Assert.Equal(1,wire.Calls);
            var held=await store.GetAsync(f.Scope,request.Id,f.Actor,Ct);
            Assert.Equal("Held",held.State); Assert.Equal("submission-uncertain",held.Reason);
            Assert.Null(held.Draft);
            Assert.Equal(item.Version,(await f.Handlers.Handle(new GetWorkItem(f.Scope,item.Id),Ct)).Item.Version);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Advisory_only_dispatch_does_not_mutate_imported_coding_even_expired_final_leases()
    {
        var f=await Fixture.Create(); var item=await f.CreateItem("advisory-lane");
        var durable=new SqlDurableWorkStore(f.Factory,f.Clock);
        await durable.EnqueueAsync(f.Scope,new(item.Id,"coding-import","one","{}"),WorkDeliveryKind.DispatchAgent,(_,_)=>Task.CompletedTask,Ct);
        await using var db=f.Factory.CreateDbContext();
        var coding=await db.Deliveries.Include(x=>x.Outbox).SingleAsync(x=>x.WorkItemId==item.Id,Ct);
        coding.Outbox.OutboxState=OutboxState.Processing; coding.Outbox.ProcessTryCount=3;
        coding.Outbox.DeliveryLeaseId=Guid.NewGuid(); coding.Outbox.DeliveryLeaseUntilUtc=DateTime.UtcNow.AddMinutes(-1);
        await db.SaveChangesAsync(Ct); var lease=coding.Outbox.DeliveryLeaseId;
        var store=new SqlAdvisoryRequests(f.Factory,f.Clock,Config(f));
        await store.CreateAsync(f.Scope,item.Id,f.Actor,new(Guid.NewGuid(),item.Version,"Synthetic brief",true),Ct);
        var scheduler=new Scheduler();
        await new EnabledLaneDispatcher(db,Config(f),scheduler).Run();
        await db.Entry(coding.Outbox).ReloadAsync(Ct);
        Assert.Equal(OutboxState.Processing,coding.Outbox.OutboxState); Assert.Equal(lease,coding.Outbox.DeliveryLeaseId);
        Assert.Equal(3,coding.Outbox.ProcessTryCount);
        Assert.Single(scheduler.Rows); Assert.NotEqual(coding.OutboxId,scheduler.Rows[0]);
    }

    [Fact]
    public async Task Api_enforces_requester_privacy_read_and_execute()
    {
        var f=await Fixture.Create(); var item=await f.CreateItem("advisory-api");
        var settings=Config(f).AsEnumerable().ToDictionary(x=>x.Key,x=>x.Value);
        await using var api=new ApiFixture(sql:f.Connection,clock:f.Clock,settings:settings);
        using var client=api.Client(f.Scope,["read","execute"]);
        var root=ApiFixture.Root(f.Scope);
        var body=new CreateAdvisoryRequest(Guid.NewGuid(),item.Version,"Synthetic brief",true);
        var response=await client.PostAsJsonAsync(root+$"/items/{item.Id}/advisories",body,Ct);
        Assert.Equal(HttpStatusCode.Accepted,response.StatusCode);
        var request=(await response.Content.ReadFromJsonAsync<AdvisoryRequestView>(Ct))!;
        client.DefaultRequestHeaders.Remove("X-Orchestration-Chat");
        Assert.Equal(HttpStatusCode.OK,(await client.GetAsync(root+$"/advisories/{request.Id}",Ct)).StatusCode);
        using var other=api.Client(f.Scope,["read","execute"],subject:"other");
        Assert.Equal(HttpStatusCode.NotFound,(await other.GetAsync(root+$"/advisories/{request.Id}",Ct)).StatusCode);
        using var readOnly=api.Client(f.Scope,["read"]);
        Assert.Equal(HttpStatusCode.Forbidden,(await readOnly.PostAsJsonAsync(root+$"/items/{item.Id}/advisories",body,Ct)).StatusCode);
    }

    [Fact]
    public async Task Private_web_encodes_model_text_requires_antiforgery_and_can_discard_without_task_mutation()
    {
        var f=await Fixture.Create(); var item=await f.CreateItem("advisory-web");
        var actor=new WorkActor("agent-a","web:test");
        var store=new SqlAdvisoryRequests(f.Factory,f.Clock,Config(f));
        var request=await store.CreateAsync(f.Scope,item.Id,actor,new(Guid.NewGuid(),item.Version,"Reviewed synthetic brief",true),Ct);
        await using(var db=f.Factory.CreateDbContext())
        {
            var delivery=await db.Deliveries.SingleAsync(x=>x.WorkItemId==item.Id,Ct);
            await store.BeginAsync(new(delivery.Id,f.Scope,item.ProjectId,item.Id,item.Version,delivery.Kind),Ct);
        }
        await store.FinishAsync(f.Scope,request.Id,new("<script>alert('not executable')</script>","fixture-model",21,7),"",false,Ct);
        await using var web=await WebFixture.Create(f);
        var path=web.Root+$"/items/{item.Id}/advisories";
        var html=await web.Html(path);
        Assert.Contains("data-testid=\"advisory-draft\"",html);
        Assert.DoesNotContain("<script>alert(",html); Assert.Contains("&lt;script&gt;",html);
        Assert.Contains("no-store",(await web.Client.GetAsync(path,Ct)).Headers.CacheControl!.ToString());
        Assert.Equal(HttpStatusCode.BadRequest,(await web.Client.PostAsync(path+"?handler=Cancel",new FormUrlEncodedContent([]),Ct)).StatusCode);
        var current=await store.GetAsync(f.Scope,request.Id,actor,Ct);
        Assert.Equal(HttpStatusCode.Redirect,(await web.Submit(path,"Cancel",new() {
            ["AdvisoryId"]=current.Id.ToString(),["Version"]=current.Version.ToString() })).StatusCode);
        Assert.Equal("Cancelled",(await store.GetAsync(f.Scope,request.Id,actor,Ct)).State);
        Assert.Equal(item.Version,(await f.Handlers.Handle(new GetWorkItem(f.Scope,item.Id),Ct)).Item.Version);
    }

    private sealed class Wire(Func<HttpRequestMessage,HttpResponseMessage> reply) : HttpMessageHandler
    {
        public int Calls {get;private set;}
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)
        { Calls++; return Task.FromResult(reply(request)); }
    }
    private sealed class Scheduler : IOutboxJobScheduler
    {
        public List<long> Rows {get;}=[];
        public string? ScheduleOnline(object message)=>throw new NotSupportedException();
        public Task<string?> ScheduleOnlineAsync(object message,CancellationToken ct)=>throw new NotSupportedException();
        public string? ScheduleOutboxMessage(OutboxMessage row) { Rows.Add(row.Id); return "fixture-job"; }
        public Task<string?> ScheduleOutboxMessageAsync(OutboxMessage row,CancellationToken ct)=>Task.FromResult(ScheduleOutboxMessage(row));
    }
}
