using Neo.AgentOrchestration.Application.Work;
using Neo.AgentOrchestration.Application.Workspace;
using Neo.AgentOrchestration.Contracts;
using Neo.AgentOrchestration.Domain.Projects;
using Neo.AgentOrchestration.Domain.Runs;
using Neo.AgentOrchestration.Domain.Work;
using Neo.AgentOrchestration.Web.Pages;
using Xunit;

namespace Neo.AgentOrchestration.Tests;

public sealed class TokenMeteringTests
{
    private static WorkTokenUsageView Row(long? input, long? output, string source="reported", long? cached=null, long? reasoning=null)
        => new(Guid.NewGuid(), null, "provider", "model", source, "receipt", input, output, cached, reasoning, DateTimeOffset.UtcNow);

    [Fact] public void Unknown_zero_partial_and_subsets_are_distinct()
    {
        Assert.Null(TokenMetering.Summarize([]).TotalTokens);
        Assert.Equal(0, TokenMetering.Summarize([Row(0,0)]).TotalTokens);
        var partial=TokenMetering.Summarize([Row(100,null),Row(10,20)]);
        Assert.Null(partial.TotalTokens); Assert.Equal(130,partial.KnownTotalTokens);
        Assert.Equal(1,partial.CompleteReportCount); Assert.Equal(2,partial.ReportCount);
        Assert.Equal(120, TokenMetering.Summarize([Row(100,20,cached:80,reasoning:10)]).TotalTokens);
        Assert.Null(TokenMetering.Summarize([Row(100,20,"estimated")]).KnownTotalTokens);
        Assert.Equal(15, TokenMetering.Summarize([Row(10,5,"imported"),Row(900,900,"estimated")]).TotalTokens);
    }

    [Fact] public async Task Estimates_usage_retry_owner_scope_and_stale_version_are_checked()
    {
        using var f=new WorkFixture(); var item=await f.Create("meter");
        Assert.Null(item.Item.EstimatedTokens); Assert.Null(item.Item.TokenMeter!.TotalTokens);
        item=await f.Change(item.Item.Id,new TokenEstimateChange(1000));
        Assert.Equal(1000,item.Item.EstimatedTokens); Assert.Null(item.Item.TokenMeter!.TotalTokens);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(()=>f.Change(item.Item.Id,new TokenEstimateChange(0)));
        await f.Change(item.Item.Id,new StatusChange(WorkItemStatus.Ready)); item=await f.Claim(item.Item.Id);
        var request=new RecordWorkTokenUsageRequest(item.Item.Version,Guid.NewGuid(),"provider","receipt-1",InputTokens:100,OutputTokens:20,CachedInputTokens:80,ReasoningTokens:10);
        var written=await f.Change(item.Item.Id,new TokenUsageChange(request));
        Assert.Equal(120,written.Item.TokenMeter!.TotalTokens); Assert.Single(written.TokenUsage!);
        var duplicate=await f.Change(item.Item.Id,new TokenUsageChange(request),version:item.Item.Version);
        Assert.Equal(written.Item.Version,duplicate.Item.Version); Assert.Single(duplicate.TokenUsage!);
        await Assert.ThrowsAsync<WorkItemConflictException>(()=>f.Change(item.Item.Id,new TokenUsageChange(request with {OutputTokens=21})));
        await Assert.ThrowsAsync<WorkItemConflictException>(()=>f.Change(item.Item.Id,new TokenUsageChange(request),actor:new WorkActor("other","chat")));
        await Assert.ThrowsAsync<WorkItemConflictException>(()=>f.Change(item.Item.Id,new TokenUsageChange(request with {RequestId=Guid.NewGuid()}),version:item.Item.Version));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Change(item.Item.Id,new TokenUsageChange(request with {RequestId=Guid.NewGuid()}),actor:new WorkActor("other","chat")));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>f.Change(item.Item.Id,new TokenEstimateChange(2000),actor:new WorkActor("other","chat")));
        await Assert.ThrowsAsync<ArgumentException>(()=>f.Change(item.Item.Id,new TokenUsageChange(request with {RequestId=Guid.NewGuid(),CachedInputTokens=101})));
        await Assert.ThrowsAsync<ArgumentException>(()=>f.Change(item.Item.Id,new TokenUsageChange(request with {RequestId=Guid.NewGuid(),InputTokens=null,OutputTokens=null,CachedInputTokens=null,ReasoningTokens=null})));
        await Assert.ThrowsAsync<KeyNotFoundException>(()=>f.Sender.Send(new GetWorkItem(new WorkspaceScope(Guid.NewGuid(),Guid.NewGuid()),item.Item.Id),f.Ct));
        var cleared=await f.Change(item.Item.Id,new TokenEstimateChange(null)); Assert.Null(cleared.Item.EstimatedTokens);
    }

    [Fact] public async Task Board_aggregates_full_filter_not_page_and_does_not_roll_children_into_parent()
    {
        using var f=new WorkFixture(); var parent=await f.Create("parent",estimate:600); var child=await f.Create("child",parent.Item.Id);
        await f.Change(parent.Item.Id,new TokenEstimateChange(1000));
        await f.Change(child.Item.Id,new TokenEstimateChange(200));
        f.Store.Usage.Add(TokenUsageReport.Create(f.Scope,Guid.NewGuid(),Guid.NewGuid(),child.Item.Id,"provider",null,10,5,5,2,"reported","usage",f.Clock.Now));
        f.Store.Usage.Add(TokenUsageReport.Create(f.Scope,Guid.NewGuid(),Guid.NewGuid(),child.Item.Id,"provider",null,100,100,null,null,"estimated","estimate",f.Clock.Now));
        var board=await f.Sender.Send(new GetWorkBoard(f.Scope,f.Project.Id,Take:1),f.Ct);
        Assert.Single(board.Items); Assert.Equal(2,board.Total); Assert.Equal(1200,board.Metrics.EstimatedTokens);
        Assert.Equal(2,board.Metrics.TokenEstimatedCount); Assert.Equal(1,board.Metrics.TimeEstimatedCount);
        Assert.Equal(15,board.Metrics.TokenMeter!.TotalTokens); Assert.Equal(1,board.Metrics.TokenReportedItemCount);
        var details=await f.Sender.Send(new GetWorkItem(f.Scope,parent.Item.Id),f.Ct);
        Assert.Null(details.Item.TokenMeter!.TotalTokens); Assert.Equal(15,Assert.Single(details.Children).TokenMeter!.TotalTokens);
        var empty=await f.Sender.Send(new GetWorkBoard(f.Scope,f.Project.Id,Domain:"other"),f.Ct);
        Assert.Null(empty.Metrics.TokenMeter!.TotalTokens); Assert.Null(empty.Metrics.EstimatedTokens);
    }

    [Theory]
    [InlineData(1,"hours",3600)] [InlineData(1.5,"minutes",90)] [InlineData(45,"seconds",45)]
    public void Human_time_estimates_convert_without_truncation(double value,string unit,long seconds)
        => Assert.Equal(seconds,WorkPageModel.EstimateSeconds((decimal)value,unit));
    [Fact] public void Time_estimate_null_and_invalid_are_safe()
    {
        Assert.Null(WorkPageModel.EstimateSeconds(null,"minutes"));
        Assert.Throws<ArgumentException>(()=>WorkPageModel.EstimateSeconds(-1,"hours"));
        Assert.Throws<ArgumentException>(()=>WorkPageModel.EstimateSeconds(1,"days"));
        Assert.Equal("نامعلوم",WorkPageModel.Tokens(null));
    }
}
