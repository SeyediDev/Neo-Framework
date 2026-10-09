using Neo.AgentOrchestration.Domain.ExternalExecution;
using Xunit;

namespace Neo.AgentOrchestration.Tests;

public sealed class GatewayRunTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);
    private static GatewayRun Run() => GatewayRun.Reserve(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        "coding", new('A', 64), "sandbox-1", "immutable-body", Now);
    private static Guid Start(GatewayRun r)
    { var lease = Guid.NewGuid(); r.Acquire(lease, Now, TimeSpan.FromMinutes(2)); return lease; }
    private static void Running(GatewayRun r, Guid lease)
    { r.BeginPrepare(lease, Now); r.SavePreparation(lease, "prepared", Now); r.BeginSubmit(lease, Now); r.SaveSubmission(lease, "submitted", Now); }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Interrupted_native_write_is_held_not_replayed(bool submitting)
    {
        var r = Run(); var lease = Start(r); r.BeginPrepare(lease, Now);
        if (submitting) { r.SavePreparation(lease, "prepared", Now); r.BeginSubmit(lease, Now); }
        var later = Now.AddMinutes(3); var next = Guid.NewGuid();
        r.Acquire(next, later, TimeSpan.FromMinutes(2));
        Assert.Equal(GatewayPhase.ReconciliationRequired, r.Phase); Assert.True(r.CapacityHeld);
        Assert.Throws<GatewayConflictException>(() => r.BeginPrepare(next, later));
        Assert.Throws<GatewayConflictException>(() => r.SaveSubmission(lease, "stale", later));
    }
    [Fact]
    public void Expiring_worker_lease_does_not_release_executor_or_allow_stale_mutation()
    {
        var r = Run(); var lease = Start(r); Running(r, lease);
        var later = Now.AddMinutes(3); Assert.True(r.CapacityHeld);
        Assert.Throws<GatewayConflictException>(() => r.ReleaseLease(lease, later));
        var next = Guid.NewGuid(); r.Acquire(next, later, TimeSpan.FromMinutes(2));
        Assert.Throws<GatewayConflictException>(() => r.Observe(lease, true, later));
        Assert.True(r.CapacityHeld); Assert.Equal(GatewayPhase.Running, r.Phase);
    }
    [Fact]
    public void Approval_latch_survives_stale_terminal_poll_and_stop()
    {
        var r = Run(); var lease = Start(r); Running(r, lease);
        r.HoldApproval(lease, "permission-1", Now); r.Observe(lease, true, Now);
        Assert.Equal(GatewayPhase.AwaitingApproval, r.Phase);
        r.RequestStop(lease, Now); r.Observe(lease, true, Now);
        Assert.Equal(GatewayPhase.AwaitingApproval, r.Phase); Assert.True(r.ApprovalPending); Assert.True(r.CapacityHeld);
        Assert.Throws<GatewayConflictException>(() => r.FreezeResult(lease, "{}", true, Now));
    }
    [Fact]
    public void Stop_acknowledgement_is_not_a_terminal_result()
    {
        var r = Run(); var lease = Start(r); Running(r, lease); r.RequestStop(lease, Now);
        r.Observe(lease, false, Now); Assert.True(r.CapacityHeld); Assert.Null(r.FrozenResult);
        Assert.Equal(GatewayPhase.StopRequested, r.Phase);
    }
    [Fact]
    public void Native_completion_waits_for_actual_executor_exit_and_freezes_result()
    {
        var r = Run(); var lease = Start(r); Running(r, lease); r.Observe(lease, true, Now);
        Assert.Throws<GatewayConflictException>(() => r.FreezeResult(lease, "{}", false, Now));
        Assert.True(r.CapacityHeld);
        r.FreezeResult(lease, "result-v1", true, Now); Assert.False(r.CapacityHeld);
        Assert.Throws<GatewayConflictException>(() => r.FreezeResult(lease, "result-v2", true, Now));
        r.ReleaseLease(lease, Now); var retry = Guid.NewGuid(); r.Acquire(retry, Now, TimeSpan.FromMinutes(2));
        Assert.False(r.CapacityHeld); var receipt = Guid.NewGuid(); r.AcknowledgeCallback(retry, receipt, Now);
        Assert.Equal(GatewayPhase.Delivered, r.Phase); Assert.Equal(receipt, r.CallbackReceiptId);
    }
    [Fact]
    public void Duplicate_compares_scope_binding_sandbox_and_exact_body()
    {
        var r = Run();
        GatewayRun Duplicate(string payload, string sandbox, Guid project) => GatewayRun.Reserve(r.RunId, r.OrganizationId,
            r.WorkspaceId, project, r.BindingKey, r.BindingFingerprint, sandbox, payload, Now);
        r.RequireDuplicate(Duplicate(r.Payload, r.SandboxId, r.ProjectId));
        Assert.Throws<GatewayConflictException>(() => r.RequireDuplicate(Duplicate(r.Payload + " ", r.SandboxId, r.ProjectId)));
        Assert.Throws<GatewayConflictException>(() => r.RequireDuplicate(Duplicate(r.Payload, "sandbox-2", r.ProjectId)));
        Assert.Throws<GatewayConflictException>(() => r.RequireDuplicate(Duplicate(r.Payload, r.SandboxId, Guid.NewGuid())));
    }
}
