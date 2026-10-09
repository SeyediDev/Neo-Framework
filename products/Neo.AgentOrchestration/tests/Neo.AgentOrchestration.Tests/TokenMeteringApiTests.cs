using System.Net;
using System.Net.Http.Json;
using Neo.AgentOrchestration.Application.Work;
using Neo.AgentOrchestration.Application.Runs;
using Neo.AgentOrchestration.Contracts;
using Neo.AgentOrchestration.Domain.Work;
using Xunit;

namespace Neo.AgentOrchestration.Tests;
public sealed class TokenMeteringApiTests
{
    [Fact] public async Task Http_metering_requires_grants_scope_owner_and_consistent_idempotency()
    {
        using var f=new WorkFixture(); await using var api=new ApiFixture(f.Store,f.Clock);
        using var client=api.Client(f.Scope); var ct=f.Ct; var root=ApiFixture.Root(f.Scope);
        var create=new CreateWorkItemRequest(f.Project.Id,"api-meter","Meter","metering",RequestId:Guid.NewGuid(),EstimatedTokens:1000);
        var item=await ApiFixture.Post<WorkItemDetails>(client,root+"/items",create,HttpStatusCode.Created);
        Assert.Equal(1000,item.Item.EstimatedTokens);
        Assert.Equal(item.Item.Id,(await ApiFixture.Post<WorkItemDetails>(client,root+"/items",create,HttpStatusCode.Created)).Item.Id);
        Assert.Equal(HttpStatusCode.Conflict,(await client.PostAsJsonAsync(root+"/items",create with {EstimatedTokens=2000},ct)).StatusCode);
        var url=root+$"/items/{item.Item.Id}";
        using var reader=api.Client(f.Scope,["read"]);
        Assert.Equal(HttpStatusCode.Forbidden,(await reader.PutAsJsonAsync(url+"/token-estimate",new SetTokenEstimateRequest(item.Item.Version,2000),ct)).StatusCode);
        item=await ApiFixture.Post<WorkItemDetails>(client,url+"/status",new ChangeStatusRequest(item.Item.Version,"Ready"));
        item=await ApiFixture.Post<WorkItemDetails>(client,url+"/claim",new ClaimWorkItemRequest(item.Item.Version,f.Role.Id));
        var usage=new RecordWorkTokenUsageRequest(item.Item.Version,Guid.NewGuid(),"fixture-provider","fixture-reference",InputTokens:10,OutputTokens:0);
        Assert.Equal(HttpStatusCode.Forbidden,(await reader.PostAsJsonAsync(url+"/token-usage",usage,ct)).StatusCode);
        using var other=api.Client(f.Scope,subject:"other");
        Assert.Equal(HttpStatusCode.Conflict,(await other.PostAsJsonAsync(url+"/token-usage",usage,ct)).StatusCode);
        var measured=await ApiFixture.Post<WorkItemDetails>(client,url+"/token-usage",usage);
        Assert.Equal(10,measured.Item.TokenMeter!.TotalTokens);
        var retry=await ApiFixture.Post<WorkItemDetails>(client,url+"/token-usage",usage);
        Assert.Equal(measured.Item.Version,retry.Item.Version); Assert.Single(retry.TokenUsage!);
        Assert.Equal(HttpStatusCode.Conflict,(await client.PostAsJsonAsync(url+"/token-usage",usage with {InputTokens=20},ct)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,(await client.PostAsJsonAsync(url+"/token-usage",usage with {ExpectedVersion=measured.Item.Version,RequestId=Guid.NewGuid(),InputTokens=-1},ct)).StatusCode);
        var brief=(await client.GetFromJsonAsync<WorkContextView>(url+"/context",ct))!;
        Assert.Equal(10,brief.Item.TokenMeter!.TotalTokens); Assert.Contains("tokenUsageReports",brief.Omitted);
    }

    [Fact] public async Task Sql_metering_survives_reload_and_duplicate_retries()
    {
        var f=await SqlPersistenceTests.Fixture.Create(); var item=await f.CreateItem("sql-meter");
        await f.Change(item.Id,new StatusChange(WorkItemStatus.Ready)); await f.Claim(item.Id);
        var estimate=await f.Change(item.Id,new TokenEstimateChange(400));
        var usage=new RecordWorkTokenUsageRequest(estimate.Item.Version,Guid.NewGuid(),"fixture","fixture-receipt",InputTokens:100,OutputTokens:25);
        var written=await f.Change(item.Id,new TokenUsageChange(usage));
        var read=await f.Handlers.Handle(new GetWorkItem(f.Scope,item.Id),TestContext.Current.CancellationToken);
        Assert.Equal(400,read.Item.EstimatedTokens); Assert.Equal(125,read.Item.TokenMeter!.TotalTokens); Assert.Single(read.TokenUsage!);
        var retry=await f.Handlers.Handle(new UpdateWorkItem(f.Scope,item.Id,f.Actor,estimate.Item.Version,new TokenUsageChange(usage)),TestContext.Current.CancellationToken);
        Assert.Equal(written.Item.Version,retry.Item.Version);
    }

    [Fact] public async Task Sql_run_usage_retries_are_immutable_and_estimates_are_not_actuals()
    {
        var ct=TestContext.Current.CancellationToken;
        var f=await SqlPersistenceTests.Fixture.Create();
        var setup=await SqlRunTests.Configure(f); var item=await SqlRunTests.Ready(f);
        var started=await SqlRunTests.Start(f,setup,item); var runs=SqlRunTests.Runs(f);
        var body=new RecordTokenUsageRequest(Guid.NewGuid(),"fake",null,100,20,80,10);
        var request=new RecordAgentRunUsage(f.Scope,started.Run.Id,body);
        var first=await runs.Handle(request,ct); Assert.Single(first.TokenUsage!);
        f.Clock.Advance(10);
        Assert.Single((await runs.Handle(request,ct)).TokenUsage!);
        await Assert.ThrowsAsync<WorkItemConflictException>(()=>runs.Handle(request with {Body=body with {OutputTokens=30}},ct));
        await Assert.ThrowsAsync<ArgumentException>(()=>runs.Handle(request with {Body=body with {RequestId=Guid.NewGuid(),CachedInputTokens=101}},ct));
        await runs.Handle(request with {Body=body with {RequestId=Guid.NewGuid(),Source="estimated",InputTokens=900}},ct);
        var actual=await f.Handlers.Handle(new GetWorkItem(f.Scope,item.Id),ct);
        Assert.Equal(120,actual.Item.TokenMeter!.TotalTokens);
        Assert.Equal(1,actual.Item.TokenMeter.ReportCount);
        Assert.Equal(80,actual.Item.TokenMeter.CachedInputTokens);
        Assert.Equal(10,actual.Item.TokenMeter.ReasoningTokens);
    }
}
