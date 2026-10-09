using System.Security.Cryptography;
using System.Text;

namespace Neo.AgentOrchestration.Domain.ExternalExecution;

public enum GatewayPhase
{
    Reserved, Preparing, Prepared, Submitting, Running, AwaitingApproval,
    StopRequested, ReconciliationRequired, AwaitingEvidence, CallbackReady, Delivered
}

// An execution journal, NOT a second work item or workflow owner. Native writes
// have a durable intent BEFORE the network call. Expiring a processing lease
// never releases the sandbox/capacity reservation or authorizes replay.
public sealed class GatewayRun
{
    private GatewayRun() { }
    public Guid RunId { get; private set; }
    public Guid OrganizationId { get; private set; }
    public Guid WorkspaceId { get; private set; }
    public Guid ProjectId { get; private set; }
    public string BindingKey { get; private set; } = "";
    public string BindingFingerprint { get; private set; } = "";
    public string PayloadHash { get; private set; } = "";
    public string Payload { get; private set; } = "";
    public string SandboxId { get; private set; } = "";
    public GatewayPhase Phase { get; private set; }
    public string? PreparedHandle { get; private set; }
    public string? ApprovalId { get; private set; }
    public bool ApprovalPending { get; private set; }
    public bool StopPending { get; private set; }
    public bool CapacityHeld { get; private set; }
    public string? ReasonCode { get; private set; }
    public string? FrozenResult { get; private set; }
    public Guid? CallbackReceiptId { get; private set; }
    public Guid Version { get; private set; }
    public Guid? LeaseId { get; private set; }
    public DateTimeOffset? LeaseUntilUtc { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public static GatewayRun Reserve(Guid runId, Guid organizationId, Guid workspaceId,
        Guid projectId, string bindingKey, string fingerprint, string sandboxId,
        string payload, DateTimeOffset now)
    {
        if (new[] { runId, organizationId, workspaceId, projectId }.Any(x => x == Guid.Empty) ||
            string.IsNullOrWhiteSpace(bindingKey) || bindingKey.Length > 40 ||
            !IsHash(fingerprint) || string.IsNullOrWhiteSpace(sandboxId) || sandboxId.Length > 100 ||
            string.IsNullOrWhiteSpace(payload) || Encoding.UTF8.GetByteCount(payload) > 256 * 1024)
            throw new GatewayConflictException("gateway-reservation-invalid");
        return new() { RunId = runId, OrganizationId = organizationId, WorkspaceId = workspaceId,
            ProjectId = projectId, BindingKey = bindingKey, BindingFingerprint = fingerprint,
            SandboxId = sandboxId, Payload = payload, PayloadHash = Hash(payload),
            Phase = GatewayPhase.Reserved, CreatedAtUtc = now, UpdatedAtUtc = now, Version = Guid.NewGuid() };
    }

    public void RequireDuplicate(GatewayRun candidate)
    {
        if (RunId != candidate.RunId || OrganizationId != candidate.OrganizationId ||
            WorkspaceId != candidate.WorkspaceId || ProjectId != candidate.ProjectId ||
            BindingKey != candidate.BindingKey || BindingFingerprint != candidate.BindingFingerprint ||
            SandboxId != candidate.SandboxId || PayloadHash != candidate.PayloadHash)
            throw new GatewayConflictException("gateway-duplicate-conflict");
    }

    public void Acquire(Guid lease, DateTimeOffset now, TimeSpan duration)
    {
        if (lease == Guid.Empty || duration <= TimeSpan.Zero || duration > TimeSpan.FromMinutes(5) ||
            Phase == GatewayPhase.Delivered || LeaseUntilUtc > now)
            throw new GatewayConflictException("gateway-lease-unavailable");
        // A dead worker may have performed the intent even when its response was
        // lost. Never replay Preparing/Submitting after restart.
        if (Phase is GatewayPhase.Preparing or GatewayPhase.Submitting)
        { Phase = GatewayPhase.ReconciliationRequired; ReasonCode = "gateway-write-interrupted"; }
        LeaseId = lease; LeaseUntilUtc = now + duration;
        if (Phase != GatewayPhase.CallbackReady) CapacityHeld = true;
        Touch(now);
    }

    public void RequireLease(Guid lease, DateTimeOffset now)
    {
        if (lease != LeaseId || LeaseUntilUtc <= now || LeaseUntilUtc is null)
            throw new GatewayConflictException("gateway-lease-lost");
    }

    public void BeginPrepare(Guid lease, DateTimeOffset now)
    { RequireLease(lease, now); RequirePhase(GatewayPhase.Reserved); Phase = GatewayPhase.Preparing; Touch(now); }
    public void SavePreparation(Guid lease, string handle, DateTimeOffset now)
    { RequireLease(lease, now); RequirePhase(GatewayPhase.Preparing); PreparedHandle = Text(handle, 4096); Phase = GatewayPhase.Prepared; Touch(now); }
    public void BeginSubmit(Guid lease, DateTimeOffset now)
    { RequireLease(lease, now); RequirePhase(GatewayPhase.Prepared); Phase = GatewayPhase.Submitting; Touch(now); }
    public void SaveSubmission(Guid lease, string handle, DateTimeOffset now)
    { RequireLease(lease, now); RequirePhase(GatewayPhase.Submitting); PreparedHandle = Text(handle, 4096); Phase = GatewayPhase.Running; Touch(now); }

    public void HoldApproval(Guid lease, string? requestId, DateTimeOffset now)
    {
        RequireLease(lease, now); RequireObservable(); ApprovalPending = true;
        if (requestId is not null)
        {
            if (ApprovalId is not null && ApprovalId != requestId)
                throw new GatewayConflictException("gateway-approval-reconciliation");
            ApprovalId = Text(requestId, 100);
        }
        Phase = GatewayPhase.AwaitingApproval; Touch(now);
    }
    // No approval reply API is provided: resolving this latch requires a future
    // authorized, request-matched human decision AND native acknowledgement.
    public void Observe(Guid lease, bool terminal, DateTimeOffset now)
    {
        RequireLease(lease, now); RequireObservable();
        if (ApprovalPending) Phase = GatewayPhase.AwaitingApproval;
        else if (terminal) Phase = GatewayPhase.AwaitingEvidence;
        else if (StopPending) Phase = GatewayPhase.StopRequested;
        // In particular, Unknown cannot clear a reconciliation hold.
        Touch(now);
    }
    public void RequestStop(Guid lease, DateTimeOffset now)
    {
        RequireLease(lease, now); RequireObservable(); StopPending = true;
        Phase = GatewayPhase.StopRequested; Touch(now);
    }
    public void Reconcile(Guid lease, string code, DateTimeOffset now)
    {
        RequireLease(lease, now);
        if (Phase is GatewayPhase.CallbackReady or GatewayPhase.Delivered) throw new GatewayConflictException("gateway-phase-conflict");
        ReasonCode = Text(code, 100); Phase = ApprovalPending ? GatewayPhase.AwaitingApproval : GatewayPhase.ReconciliationRequired; Touch(now);
    }
    public void FreezeResult(Guid lease, string result, bool executorExited, DateTimeOffset now)
    {
        RequireLease(lease, now); RequirePhase(GatewayPhase.AwaitingEvidence);
        if (!executorExited || ApprovalPending) throw new GatewayConflictException("gateway-executor-not-settled");
        FrozenResult = Text(result, 256 * 1024); Phase = GatewayPhase.CallbackReady;
        // This is the first point capacity can be released. Durable callback
        // delivery can continue without an active executor.
        CapacityHeld = false; Touch(now);
    }
    public void AcknowledgeCallback(Guid lease, Guid receipt, DateTimeOffset now)
    {
        RequireLease(lease, now); RequirePhase(GatewayPhase.CallbackReady);
        if (receipt == Guid.Empty) throw new GatewayConflictException("gateway-callback-invalid");
        CallbackReceiptId = receipt; Phase = GatewayPhase.Delivered; CapacityHeld = false; Touch(now);
    }
    public void ReleaseLease(Guid lease, DateTimeOffset now)
    { RequireLease(lease, now); LeaseId = null; LeaseUntilUtc = null; Touch(now); }
    private void RequirePhase(GatewayPhase expected)
    { if (Phase != expected) throw new GatewayConflictException("gateway-phase-conflict"); }
    private void RequireObservable()
    {
        if (PreparedHandle is null || Phase is GatewayPhase.Reserved or GatewayPhase.Preparing or
            GatewayPhase.Prepared or GatewayPhase.CallbackReady or GatewayPhase.Delivered)
            throw new GatewayConflictException("gateway-phase-conflict");
    }
    private void Touch(DateTimeOffset now) { UpdatedAtUtc = now; Version = Guid.NewGuid(); }
    private static string Text(string value, int max) => string.IsNullOrWhiteSpace(value) || Encoding.UTF8.GetByteCount(value) > max
        ? throw new GatewayConflictException("gateway-value-invalid") : value;
    private static bool IsHash(string value) => value.Length == 64 && value.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');
    public static string Hash(string body) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
}

public sealed class GatewayConflictException(string code) : InvalidOperationException("Gateway state could not be changed.")
{ public string Code { get; } = code; }
