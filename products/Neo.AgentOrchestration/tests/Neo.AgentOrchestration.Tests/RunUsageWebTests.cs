using System.Net;
using Neo.AgentOrchestration.Domain.Runs;
using Neo.AgentOrchestration.Web.Pages;
using Xunit;

namespace Neo.AgentOrchestration.Tests;

public sealed class RunUsageWebTests
{
    [Fact]
    public async Task Run_shows_unknown_until_reported_and_distinguishes_run_usage_from_task_budget()
    {
        var ct = TestContext.Current.CancellationToken;
        var f = await SqlPersistenceTests.Fixture.Create();
        var setup = await SqlRunTests.Configure(f); var item = await SqlRunTests.Ready(f);
        var run = await SqlRunTests.Start(f, setup, item);
        await using var web = await WebFixture.Create(f);
        var url = web.Root + "/runs/" + run.Run.Id;
        var empty = WebUtility.HtmlDecode(await web.Html(url));
        Assert.Contains("مصرف نامعلوم است، نه صفر", empty);
        Assert.Contains("زمان و برآورد کل کار", empty);
        await using (var db = f.Factory.CreateDbContext())
        {
            // Render-only fixture: explicitly synthetic report, not evidence
            // that the fake engine produced native provider counters.
            db.TokenUsageReports.Add(TokenUsageReport.Create(f.Scope, Guid.NewGuid(), run.Run.Id, item.Id,
                "fake", "render-fixture", 12, 0, null, null, "reported-harness", "render-only", f.Clock.GetUtcNow()));
            await db.SaveChangesAsync(ct);
        }
        var html = WebUtility.HtmlDecode(await web.Html(url));
        Assert.Contains("گزارش خودکار هارنس", html); Assert.Contains("render-fixture", html);
        Assert.Contains("<td>0</td>", html); Assert.Contains("<td>نامعلوم</td>", html);
        Assert.DoesNotContain("هنوز گزارش مصرفی", html); Assert.Contains("دوباره دستی ثبت نکنید", html);
        await web.Export("run-usage", html);
    }

    [Theory]
    [InlineData("reported-harness", "گزارش خودکار هارنس")]
    [InlineData("reported-manual", "گزارش دستی")]
    [InlineData("estimated", "تخمین؛ خارج از مصرف")]
    public void Sources_are_not_mislabeled_as_billing(string source, string label)
        => Assert.Equal(label, WorkPageModel.UsageSource(source));
}
