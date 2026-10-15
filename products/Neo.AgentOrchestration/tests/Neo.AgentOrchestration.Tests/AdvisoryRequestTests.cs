using System.Text;
using Microsoft.Extensions.Configuration;
using Neo.AgentOrchestration.Domain.Advisory;
using Neo.AgentOrchestration.Domain.Work;
using Neo.AgentOrchestration.Infrastructure.Advisory;
using Neo.AgentOrchestration.Infrastructure.Delivery;
using Neo.AgentOrchestration.Application.Delivery;
using Xunit;

namespace Neo.AgentOrchestration.Tests;

public sealed class AdvisoryRequestTests
{
    private static (WorkFixture Fixture, WorkItem Item, AdvisoryRequest Request) Setup()
    {
        var f = new WorkFixture();
        var item = WorkItem.Create(f.Scope, f.Project, "private-plan", "Private plan", "advisory", f.Actor, f.Clock.Now);
        var request = AdvisoryRequest.Create(f.Scope, item, f.Actor, Guid.NewGuid(), item.Version,
            "Suggest acceptance checks for a sample counter.", true, 180, new string('A', 64), Guid.NewGuid(), f.Clock.Now);
        return (f, item, request);
    }
    [Fact]
    public void Submission_intent_recovery_holds_instead_of_resending_and_never_changes_the_task()
    {
        var (f, item, r) = Setup(); using var owned = f;
        var version = item.Version; var status = item.Status;
        Assert.True(r.Begin(f.Clock.Now, true));
        Assert.False(r.Begin(f.Clock.Now.AddSeconds(1), true));
        Assert.Equal(AdvisoryState.Held, r.State);
        Assert.False(r.Begin(f.Clock.Now.AddSeconds(2), true));
        Assert.Equal(version, item.Version); Assert.Equal(status, item.Status);
        Assert.Null(item.OwnerAgentId);
    }
    [Fact]
    public void Consent_scope_and_stale_snapshot_are_required()
    {
        var (f, item, r) = Setup(); using var owned = f;
        Assert.Throws<ArgumentException>(() => AdvisoryRequest.Create(f.Scope, item, f.Actor, Guid.NewGuid(), item.Version,
            "brief", false, 180, new string('A',64), Guid.NewGuid(), f.Clock.Now));
        Assert.Throws<InvalidOperationException>(() => AdvisoryRequest.Create(f.Scope, item, f.Actor, Guid.NewGuid(), Guid.NewGuid(),
            "brief", true, 180, new string('A',64), Guid.NewGuid(), f.Clock.Now));
        Assert.Throws<KeyNotFoundException>(() => r.RequireRequester(f.Scope, new WorkActor("other", "chat")));
        Assert.False(r.Begin(f.Clock.Now, false)); Assert.Equal(AdvisoryState.Rejected, r.State);
    }
    [Fact]
    public void Cancelled_native_submission_does_not_claim_stop_and_retains_actual_usage_without_text()
    {
        var (f, _, r) = Setup(); using var owned = f;
        Assert.True(r.Begin(f.Clock.Now, true));
        r.Cancel(r.Version, f.Clock.Now.AddSeconds(1));
        Assert.Equal(AdvisoryState.Submitting, r.State); Assert.True(r.CancelRequested);
        r.Complete(new("private text", "local-model", 17, 8, "native-run"), f.Clock.Now.AddSeconds(2));
        Assert.Equal(AdvisoryState.Cancelled, r.State); Assert.Null(r.Draft);
        Assert.Equal(17, r.InputTokens); Assert.Equal(8, r.OutputTokens);
        Assert.Throws<InvalidOperationException>(() => r.Complete(new("duplicate", null, 17, 8), f.Clock.Now));
    }
    [Fact]
    public void Late_output_is_not_a_draft_unknown_counters_are_not_zero()
    {
        var (f, _, r) = Setup(); using var owned = f;
        Assert.True(r.Begin(f.Clock.Now, true));
        r.Complete(new("too late", null, null, null), r.DeadlineUtc);
        Assert.Equal(AdvisoryState.Expired, r.State); Assert.Null(r.Draft); Assert.Null(r.InputTokens);
    }
    [Fact]
    public void Final_delivery_failure_holds_uncertain_submission_without_claiming_native_completion()
    {
        var (f, _, r) = Setup(); using var owned = f;
        Assert.True(r.Begin(f.Clock.Now, true));
        r.DeliveryExhausted(f.Clock.Now.AddMinutes(6));
        Assert.Equal(AdvisoryState.Held, r.State); Assert.False(r.Begin(f.Clock.Now.AddMinutes(7), true));
        Assert.Null(r.Draft); Assert.Null(r.InputTokens);
    }
    [Fact]
    public void Bounded_response_must_be_completed_and_match_the_private_session()
    {
        var bytes = Encoding.UTF8.GetBytes("""{"status":"COMPLETED","session_id":"private","run_id":"r1","content":"<script>draft</script>","model":"local","metrics":{"input_tokens":21,"output_tokens":7}}""");
        var draft = AdvisoryTransport.Parse(bytes, "private");
        Assert.Equal(21, draft.InputTokens); Assert.Equal("r1", draft.NativeRunId);
        Assert.Throws<InvalidOperationException>(() => AdvisoryTransport.Parse(bytes, "someone-else"));
        var unknown = AdvisoryTransport.Parse(Encoding.UTF8.GetBytes("""{"status":"COMPLETED","session_id":"private","run_id":"r2","content":"draft","metrics":{}}"""), "private");
        Assert.Null(unknown.InputTokens); Assert.Null(unknown.OutputTokens);
    }
    [Theory]
    [InlineData("https://external.example/")]
    [InlineData("http://127.0.0.1:17840/not-a-base")]
    [InlineData("http://secret@127.0.0.1/")]
    public void Destination_cannot_be_external_or_caller_selected(string url)
    {
        var c = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["AgnoAdvisory:BaseUrl"] = url }).Build();
        Assert.Throws<InvalidOperationException>(() => AdvisoryTransport.Endpoint(c));
    }
    [Fact]
    public void Advisory_only_selection_excludes_every_imported_coding_kind_before_leasing()
    {
        var project = Guid.NewGuid();
        var c = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> {
            ["AgnoAdvisory:Enabled"]="true", ["AgnoAdvisory:Bindings:pilot:ProjectId"]=project.ToString() }).Build();
        var rows = Enum.GetValues<WorkDeliveryKind>().Select(k => new DeliveryRecord { Kind=k, ProjectId=project }).ToArray();
        Assert.Equal(WorkDeliveryKind.SendAdvisoryRequest, Assert.Single(EnabledLaneDispatcher.Enabled(rows.AsQueryable(), c)).Kind);
        Assert.Empty(EnabledLaneDispatcher.Enabled(rows.AsQueryable(), new ConfigurationBuilder().Build()));
    }
}
