using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Neo.AgentOrchestration.Application.ExternalAgents;
using Neo.AgentOrchestration.Application.ExternalExecution;
using Neo.AgentOrchestration.Contracts;
using Neo.AgentOrchestration.Domain.ExternalExecution;
using Neo.AgentOrchestration.Infrastructure.ExternalExecution;
using Xunit;

namespace Neo.AgentOrchestration.Tests;

[Collection("Gateway SQL")]
public sealed class SqlGatewayJournalTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Concurrent_duplicates_survive_fresh_context_and_changed_payload_conflicts()
    {
        var f = await Fixture.Create(); var body = f.Body();
        var saved = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => f.Execution().ReserveAsync("coding", body, Ct)));
        Assert.All(saved, r => Assert.Equal(saved[0].Version, r.Version));
        Assert.Equal(GatewayPhase.Reserved, (await f.Journal.ReadAsync(f.Scope, Ct)).Phase);
        await Assert.ThrowsAsync<GatewayConflictException>(() => f.Execution().ReserveAsync("coding", body + " ", Ct));
        await Assert.ThrowsAsync<GatewayConflictException>(() => f.Journal.ReadAsync(f.Scope with { ProjectId = Guid.NewGuid() }, Ct));
    }
    [Fact]
    public async Task Restart_uses_stored_handle_one_submission_and_exact_callback_retry()
    {
        var f = await Fixture.Create(); await f.Execution().ReserveAsync("coding", f.Body(), Ct);
        var receipt = await f.Execution().AdvanceAsync(f.Scope, Ct);
        Assert.Equal(1, f.Adapter.Prepared); Assert.Equal(1, f.Adapter.Submitted);
        var running = await f.Journal.ReadAsync(f.Scope, Ct); Assert.NotNull(running.PreparedHandle);
        Assert.Equal(running.Version, receipt.Version); Assert.Null(receipt.LeaseId);
        Assert.Null(running.LeaseId); Assert.True(running.CapacityHeld);
        f.Adapter.State = ExternalAgentState.Completed;
        f.Sandbox.Exited = false; await f.Execution().AdvanceAsync(f.Scope, Ct);
        Assert.Equal(GatewayPhase.AwaitingEvidence, (await f.Journal.ReadAsync(f.Scope, Ct)).Phase);
        Assert.True((await f.Journal.ReadAsync(f.Scope, Ct)).CapacityHeld);
        f.Sandbox.Exited = true; await f.Execution().AdvanceAsync(f.Scope, Ct);
        var ready = await f.Journal.ReadAsync(f.Scope, Ct); Assert.Equal(GatewayPhase.CallbackReady, ready.Phase);
        Assert.False(ready.CapacityHeld);
        f.Delivery.LoseNext = true; await f.Execution().AdvanceAsync(f.Scope, Ct);
        Assert.Equal(GatewayPhase.CallbackReady, (await f.Journal.ReadAsync(f.Scope, Ct)).Phase);
        await f.Execution().AdvanceAsync(f.Scope, Ct);
        Assert.Equal(GatewayPhase.Delivered, (await f.Journal.ReadAsync(f.Scope, Ct)).Phase);
        Assert.Equal(2, f.Delivery.Bodies.Count); Assert.All(f.Delivery.Bodies, x => Assert.Equal(ready.FrozenResult, x));
        Assert.Equal(1, f.Adapter.Submitted);
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Restart_during_native_write_never_blindly_prepares_or_submits_again(bool submitting)
    {
        var f = await Fixture.Create(); await f.Execution().ReserveAsync("coding", f.Body(), Ct);
        var lease = Guid.NewGuid(); var run = await f.Journal.AcquireAsync(f.Scope, lease, f.Clock.GetUtcNow(), Ct);
        run = await f.Journal.ChangeAsync(f.Scope, run.Version, x => x.BeginPrepare(lease, f.Clock.GetUtcNow()), Ct);
        if (submitting)
        {
            run = await f.Journal.ChangeAsync(f.Scope, run.Version, x => x.SavePreparation(lease,
                JsonSerializer.Serialize(f.Adapter.Handle(f.Scope), Json), f.Clock.GetUtcNow()), Ct);
            await f.Journal.ChangeAsync(f.Scope, run.Version, x => x.BeginSubmit(lease, f.Clock.GetUtcNow()), Ct);
        }
        f.Clock.Advance(180); await f.Execution().AdvanceAsync(f.Scope, Ct);
        run = await f.Journal.ReadAsync(f.Scope, Ct);
        Assert.Equal(GatewayPhase.ReconciliationRequired, run.Phase);
        Assert.True(run.CapacityHeld); Assert.Equal(0, f.Adapter.Prepared); Assert.Equal(0, f.Adapter.Submitted);
    }
    [Fact]
    public async Task Lost_submission_response_is_held_and_not_retried()
    {
        var f = await Fixture.Create(); await f.Execution().ReserveAsync("coding", f.Body(), Ct);
        f.Adapter.LoseSubmit = true; await f.Execution().AdvanceAsync(f.Scope, Ct);
        var run = await f.Journal.ReadAsync(f.Scope, Ct); Assert.Equal(GatewayPhase.ReconciliationRequired, run.Phase);
        await f.Execution().AdvanceAsync(f.Scope, Ct); Assert.Equal(1, f.Adapter.Submitted); Assert.True(run.CapacityHeld);
    }
    [Fact]
    public async Task Approval_survives_terminal_poll_without_automatic_grant_or_callback()
    {
        var f = await Fixture.Create(); await f.Execution().ReserveAsync("coding", f.Body(), Ct);
        await f.Execution().AdvanceAsync(f.Scope, Ct);
        f.Adapter.State = ExternalAgentState.AwaitingApproval; await f.Execution().AdvanceAsync(f.Scope, Ct);
        f.Adapter.State = ExternalAgentState.Completed; f.Sandbox.Exited = true;
        await f.Execution().AdvanceAsync(f.Scope, Ct); var run = await f.Journal.ReadAsync(f.Scope, Ct);
        Assert.True(run.ApprovalPending); Assert.True(run.CapacityHeld); Assert.Null(run.FrozenResult);
        Assert.Equal(GatewayPhase.AwaitingApproval, run.Phase); Assert.Empty(f.Delivery.Bodies);
    }
    [Fact]
    public async Task Expired_executor_reservation_blocks_another_tenant_and_stale_lease()
    {
        var f = await Fixture.Create(); await f.Execution().ReserveAsync("coding", f.Body(), Ct);
        var lease = Guid.NewGuid(); var run = await f.Journal.AcquireAsync(f.Scope, lease, f.Clock.GetUtcNow(), Ct);
        f.Clock.Advance(180);
        var other = f.Scope with { OrganizationId = Guid.NewGuid(), WorkspaceId = Guid.NewGuid(), ProjectId = Guid.NewGuid(), RunId = Guid.NewGuid() };
        await f.Journal.ReserveAsync(GatewayRun.Reserve(other.RunId, other.OrganizationId, other.WorkspaceId, other.ProjectId,
            "coding", new('A', 64), "other-sandbox", "other-body", f.Clock.GetUtcNow()), Ct);
        await Assert.ThrowsAsync<GatewayConflictException>(() => f.Journal.AcquireAsync(other, Guid.NewGuid(), f.Clock.GetUtcNow(), Ct));
        await Assert.ThrowsAsync<GatewayConflictException>(() => f.Journal.ChangeAsync(f.Scope, run.Version, x => x.BeginPrepare(lease, f.Clock.GetUtcNow()), Ct));
        // Verification cleanup: exact test run was never started. Complete only
        // through the state machine; no force-release SQL/lifecycle bypass.
        var next = Guid.NewGuid(); run = await f.Journal.AcquireAsync(f.Scope, next, f.Clock.GetUtcNow(), Ct);
        run = await f.Journal.ChangeAsync(f.Scope, run.Version, x => x.BeginPrepare(next, f.Clock.GetUtcNow()), Ct);
        run = await f.Journal.ChangeAsync(f.Scope, run.Version, x => x.SavePreparation(next, "test-only", f.Clock.GetUtcNow()), Ct);
        run = await f.Journal.ChangeAsync(f.Scope, run.Version, x => x.BeginSubmit(next, f.Clock.GetUtcNow()), Ct);
        run = await f.Journal.ChangeAsync(f.Scope, run.Version, x => x.SaveSubmission(next, "test-only", f.Clock.GetUtcNow()), Ct);
        run = await f.Journal.ChangeAsync(f.Scope, run.Version, x => x.Observe(next, true, f.Clock.GetUtcNow()), Ct);
        await f.Journal.ChangeAsync(f.Scope, run.Version, x => x.FreezeResult(next, "test-only", true, f.Clock.GetUtcNow()), Ct);
    }
    [Fact]
    public async Task Stable_usage_deduplicates_conflicts_roll_back_and_scope_fk_is_enforced()
    {
        var f = await Fixture.Create(); await f.Execution().ReserveAsync("coding", f.Body(), Ct);
        var lease = Guid.NewGuid(); await f.Journal.AcquireAsync(f.Scope, lease, f.Clock.GetUtcNow(), Ct);
        var report = new ExternalAgentUsage("opencode:message1", "test-provider", "test-model", 10, 5, null, null);
        await f.Journal.SaveUsageAsync(f.Scope, lease, f.Clock.GetUtcNow(), [report, report], Ct);
        await f.Journal.SaveUsageAsync(f.Scope, lease, f.Clock.GetUtcNow(), [report], Ct);
        await Assert.ThrowsAsync<GatewayConflictException>(() => f.Journal.SaveUsageAsync(f.Scope, lease, f.Clock.GetUtcNow(),
            [report with { ReportId = "opencode:new" }, report with { InputTokens = 11 }], Ct));
        await using var db = f.Factory.CreateDbContext();
        Assert.Equal(1, await db.Usage.CountAsync(x => x.RunId == f.Scope.RunId, Ct));
        db.Usage.Add(new() { RunId = f.Scope.RunId, OrganizationId = Guid.NewGuid(), WorkspaceId = f.Scope.WorkspaceId,
            ProjectId = f.Scope.ProjectId, ReportId = "foreign", Body = "{}", BodyHash = new('A', 64) });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
    }
    [Fact]
    public async Task Disabled_sandbox_and_changed_binding_do_not_start_or_hold_capacity()
    {
        var f = await Fixture.Create(); await f.Execution().ReserveAsync("coding", f.Body(), Ct);
        f.Sandbox.Ready = false;
        await Assert.ThrowsAsync<ExternalAgentException>(() => f.Execution().StopAsync(f.Scope, Ct));
        await Assert.ThrowsAsync<ExternalAgentException>(() => f.Execution().AdvanceAsync(f.Scope, Ct));
        Assert.False((await f.Journal.ReadAsync(f.Scope, Ct)).CapacityHeld); Assert.Equal(0, f.Adapter.Prepared);
        f.Sandbox.Ready = true; f.Bindings.Fingerprint = new('B', 64);
        await Assert.ThrowsAsync<ExternalAgentException>(() => f.Execution().AdvanceAsync(f.Scope, Ct));
        Assert.Equal(0, f.Adapter.Submitted);
    }
    [Fact]
    public async Task Stop_waits_for_executor_exit_and_cannot_report_success()
    {
        var f = await Fixture.Create(); await f.Execution().ReserveAsync("coding", f.Body(), Ct);
        await f.Execution().AdvanceAsync(f.Scope, Ct);
        var stopped = await f.Execution().StopAsync(f.Scope, Ct);
        Assert.True(stopped.StopPending); Assert.True(stopped.CapacityHeld); Assert.Null(stopped.LeaseId);
        Assert.Equal(1, f.Adapter.Stopped); Assert.Null(stopped.FrozenResult);
        f.Adapter.State = ExternalAgentState.Cancelled; f.Sandbox.Exited = true;
        await f.Execution().AdvanceAsync(f.Scope, Ct);
        var held = await f.Journal.ReadAsync(f.Scope, Ct); Assert.Equal(GatewayPhase.ReconciliationRequired, held.Phase);
        Assert.True(held.CapacityHeld); Assert.Empty(f.Delivery.Bodies);
        f.Sandbox.Outcome = "NeedsInput"; await f.Execution().AdvanceAsync(f.Scope, Ct);
        Assert.Equal(GatewayPhase.CallbackReady, (await f.Journal.ReadAsync(f.Scope, Ct)).Phase);
        Assert.False((await f.Journal.ReadAsync(f.Scope, Ct)).CapacityHeld);
    }
    [Fact]
    public async Task Malformed_or_unknown_protocol_is_rejected_before_reservation()
    {
        var f = await Fixture.Create();
        foreach (var body in new[] { "not json", "{}", f.Body().Replace("neo-harness/v2", "unknown/v3"), new string('x', 256 * 1024 + 1) })
            await Assert.ThrowsAsync<ExternalAgentException>(() => f.Execution().ReserveAsync("coding", body, Ct));
        await using var db = f.Factory.CreateDbContext(); Assert.Empty(await db.Runs.ToArrayAsync(Ct));
    }

    private sealed class Fixture
    {
        public required Factory Factory { get; init; }
        public required SqlGatewayJournal Journal { get; init; }
        public ExternalAgentScope Scope { get; } = new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        public ManualClock Clock { get; } = new();
        public TestAdapter Adapter { get; } = new();
        public TestSandbox Sandbox { get; } = new();
        public TestDelivery Delivery { get; } = new();
        public TestBindings Bindings { get; private set; } = null!;
        public GatewayExecution Execution() => new(Journal, Bindings, Sandbox, Delivery, Clock);
        public static async Task<Fixture> Create()
        {
            var connection = Environment.GetEnvironmentVariable("FANASA_GATEWAY_TEST_SQL");
            Assert.SkipUnless(!string.IsNullOrWhiteSpace(connection), "Set a dedicated FanasaAgentGateway_Verification SQL connection.");
            var c = new SqlConnectionStringBuilder(connection!);
            if (c.InitialCatalog != "FanasaAgentGateway_Verification") throw new InvalidOperationException("Unexpected test catalog.");
            var factory = new Factory(c.ConnectionString);
            await GatewayProvisioner.MigrateAsync(c.ConnectionString, c.InitialCatalog, Ct);
            await using var db = factory.CreateDbContext();
            // The catalog is an explicit disposable integration fixture, not
            // production/board data. Clear only its own journal tables per case.
            await db.Usage.ExecuteDeleteAsync(Ct); await db.Runs.ExecuteDeleteAsync(Ct);
            var f = new Fixture { Factory = factory, Journal = new(factory) }; f.Bindings = new(f.Adapter); return f;
        }
        public string Body()
        {
            var item = new WorkItemView(Guid.NewGuid(), Scope.ProjectId, null, "test", "Test only", "harness", "context",
                "InProgress", "Normal", null, "test", "test", "develop", false, 0, null, null, true, Guid.NewGuid(), Clock.GetUtcNow());
            return JsonSerializer.Serialize(new HarnessCompactRequest("neo-harness/v2", Scope.RunId, Scope.OrganizationId, Scope.WorkspaceId,
                Guid.NewGuid(), Bindings.Profile, "ignored-model", null, null, "ignored-branch", "https://callback.invalid/result",
                new(item, [], [], [], [], 0, 0, 0, 0, false, [])), Json);
        }
    }
    private sealed class Factory(string connection) : IDbContextFactory<GatewayDbContext>
    { public GatewayDbContext CreateDbContext() => new(new DbContextOptionsBuilder<GatewayDbContext>().UseSqlServer(connection).Options); }
    private sealed class TestBindings(TestAdapter adapter) : IGatewayBindings
    {
        public Guid Profile { get; } = Guid.NewGuid(); public string Fingerprint { get; set; } = new('A', 64);
        public GatewayBinding Resolve(string key, ExternalAgentScope scope, Guid profile, string callback)
            => profile == Profile ? new(key, Fingerprint, "test-sandbox", scope, profile, new(callback)) : throw new ExternalAgentException("test-profile");
        public IExternalAgentAdapter Adapter(GatewayBinding binding) => adapter;
    }
    private sealed class TestAdapter : IExternalAgentAdapter
    {
        public ExternalAgentEngine Engine => ExternalAgentEngine.OpenCode;
        public string BindingFingerprint => new('C', 64);
        public int Prepared { get; private set; } public int Submitted { get; private set; }
        public int Stopped { get; private set; }
        public bool LoseSubmit { get; set; } public ExternalAgentState State { get; set; } = ExternalAgentState.Unknown;
        public ExternalAgentHandle Handle(ExternalAgentScope scope) => new(scope, Engine, BindingFingerprint, "msg_" + scope.RunId.ToString("N"), "test-session");
        public Task<ExternalAgentHandle> PrepareAsync(ExternalAgentInput input, CancellationToken ct) { Prepared++; return Task.FromResult(Handle(input.Scope)); }
        public Task<ExternalAgentHandle> SubmitAsync(ExternalAgentInput input, ExternalAgentHandle prepared, CancellationToken ct)
        { Submitted++; return LoseSubmit ? throw new ExternalAgentException("test-lost-write", true) : Task.FromResult(prepared); }
        public Task<ExternalAgentObservation> ObserveAsync(ExternalAgentHandle handle, CancellationToken ct) => Task.FromResult(new ExternalAgentObservation(State, [], State == ExternalAgentState.AwaitingApproval ? "permission-1" : null));
        public Task RequestStopAsync(ExternalAgentHandle handle, CancellationToken ct) { Stopped++; return Task.CompletedTask; }
    }
    private sealed class TestSandbox : IGatewaySandbox
    {
        public bool Ready { get; set; } = true; public bool Exited { get; set; }
        public string Outcome { get; set; } = "Succeeded";
        public Task VerifyReadyAsync(GatewayBinding binding, CancellationToken ct) => Ready ? Task.CompletedTask : throw new ExternalAgentException("test-sandbox-disabled");
        public Task<GatewaySandboxEvidence> CollectAsync(GatewayBinding binding, ExternalAgentState state, CancellationToken ct) => Task.FromResult(new GatewaySandboxEvidence(Exited,
            new(Outcome, "Test collector only, not a model result", [new("Test", "fixture", "Passed", "SQL state test, not repository work")])));
    }
    private sealed class TestDelivery : IGatewayResultDelivery
    {
        private readonly Guid receipt = Guid.NewGuid(); public bool LoseNext { get; set; }
        public List<string> Bodies { get; } = [];
        public Task<Guid> DeliverAsync(GatewayBinding binding, string result, CancellationToken ct)
        { Bodies.Add(result); if (LoseNext) { LoseNext = false; throw new ExternalAgentException("test-lost-callback", true); } return Task.FromResult(receipt); }
    }
}

[CollectionDefinition("Gateway SQL", DisableParallelization = true)]
public sealed class GatewaySqlCollection;
