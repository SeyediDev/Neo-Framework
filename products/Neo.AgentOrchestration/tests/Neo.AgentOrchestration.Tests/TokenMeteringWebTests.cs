using System.Net;
using Neo.AgentOrchestration.Contracts;
using Xunit;

namespace Neo.AgentOrchestration.Tests;
public sealed class TokenMeteringWebTests
{
    [Fact] public async Task Web_forms_estimate_measure_and_render_cards_without_sql_access()
    {
        using var f=new WorkFixture(); await using var web=await WebFixture.Create(f);
        web.Client.DefaultRequestHeaders.Add("X-Neo-Navigation","1");
        var created=await web.Submit(web.Root+"/new",null,new(){["ProjectId"]=f.Project.Id.ToString(),["Key"]="web-meter",["Title"]="Meter",["Domain"]="metering",["EstimateValue"]="1.5",["EstimateUnit"]="hours",["EstimatedTokens"]="1000"});
        Assert.Equal(HttpStatusCode.Redirect,created.StatusCode); var url=created.Headers.Location!.ToString();
        var id=Guid.Parse(url.Split('/').Last()); var item=await web.ApiRead<WorkItemDetails>($"items/{id}");
        Assert.Equal(5400,item.Item.EstimatedSeconds); Assert.Equal(1000,item.Item.EstimatedTokens);
        Assert.Contains("نامعلوم",WebUtility.HtmlDecode(await web.Html(url)));
        await web.Submit(url,"Status",new(){["NextStatus"]="Ready"});
        await web.Submit(url,"Claim",new(){["RoleId"]=f.Role.Id.ToString()});
        Assert.Equal(HttpStatusCode.Redirect,(await web.Submit(url,"Estimate",new(){["EstimateValue"]="2",["EstimateUnit"]="minutes"})).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect,(await web.Submit(url,"TokenEstimate",new(){["EstimatedTokens"]="2000"})).StatusCode);
        var report=new Dictionary<string,string>{["UsageRequestId"]=Guid.NewGuid().ToString(),["UsageProvider"]="fixture",["UsageReference"]="fixture-reference",["InputTokens"]="100",["OutputTokens"]="20",["CachedInputTokens"]="80",["ReasoningTokens"]="10"};
        Assert.Equal(HttpStatusCode.Redirect,(await web.Submit(url,"TokenUsage",report)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect,(await web.Submit(url,"TokenUsage",report)).StatusCode);
        item=await web.ApiRead<WorkItemDetails>($"items/{id}"); Assert.Single(item.TokenUsage!); Assert.Equal(120,item.Item.TokenMeter!.TotalTokens);
        var html=await web.Html(web.Root+"?Domain=metering");
        Assert.Contains("data-time-meter",html); Assert.Contains("data-tracking=\"true\"",html);
        Assert.Contains("120",html); Assert.Contains("2,000",html); Assert.DoesNotContain(web.Token,html);
        Assert.Contains("data-time-meter",await web.Html(web.Root+"/roles"));
        var invalid=await web.Client.PostAsync(url+"?handler=TokenUsage",new FormUrlEncodedContent(report),f.Ct);
        Assert.Equal(HttpStatusCode.BadRequest,invalid.StatusCode);
        await web.Submit(url,"StopTime",[]);
        Assert.Contains("data-tracking=\"false\"",await web.Html(url));
    }
}
