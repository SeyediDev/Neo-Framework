using System.Text.Json;

namespace Fanasa.HrReadCandidate;

/// <summary>Candidate shape, not a grant. Only a host authority adapter resolves this from verified request evidence and current central records.</summary>
public abstract class HrReadAdmission
{
    protected HrReadAdmission(HrReadTarget target, int maximumResponseBytes, DateTimeOffset validUntil)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumResponseBytes);
        if (maximumResponseBytes > HrReadDesign.MaximumResponseBytes) throw new ArgumentOutOfRangeException(nameof(maximumResponseBytes));
        Target = target; MaximumResponseBytes = maximumResponseBytes; ValidUntil = validUntil;
    }
    public HrReadTarget Target { get; }
    public int MaximumResponseBytes { get; }
    /// <summary>Exclusive minimum of witness/token/decision/period/freshness deadlines.</summary>
    public DateTimeOffset ValidUntil { get; }
}

public interface IHrReadAuthority
{
    /// <summary>
    /// Request-scoped adapter: reauthenticate OrganizationHrBearer; verify exact wire bytes,
    /// native direct TLS validity/EKU and the same peer's keyId/tier pin; resolve the profile
    /// and authenticated unique introspection consumer. Match all evidence to this target.
    /// Complete asynchronous authentication/certificate work BEFORE any Fabric entry.
    /// </summary>
    ValueTask<HrReadAdmission?> AuthenticateAndResolveAsync(HrReadTarget target, CancellationToken cancellationToken);
}

public sealed class UnavailableHrReadAuthority : IHrReadAuthority
{
    public ValueTask<HrReadAdmission?> AuthenticateAndResolveAsync(HrReadTarget target, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult<HrReadAdmission?>(null); }
}

/// <summary>Detached JSON only; no deferred query/entity/enumerable/DB reader crosses this boundary.</summary>
public sealed class MaterializedHrRead
{
    private MaterializedHrRead(bool found, JsonElement value) { Found = found; Value = value; }
    public bool Found { get; }
    public JsonElement Value { get; }
    // Clone inside the adapter snapshot, before disposing its backing document.
    public static MaterializedHrRead Existing(JsonElement value) => value.ValueKind == JsonValueKind.Undefined
        ? throw new ArgumentException("A materialized JSON value is required.", nameof(value)) : new(true, value.Clone());
    public static MaterializedHrRead NotFound() => new(false, default);
}

/// <summary>
/// Seam to the EXISTING Fabric, with no replacement store. Calls return only after
/// leaving their independent transaction/snapshot. No await, sync-over-async or
/// transactional ReadAsync provider is allowed inside these synchronous calls.
/// </summary>
public interface IHrReadFabricAdapter
{
    /// <summary>In one independent write transaction: assert current authority; claim existing bucket contract-policy.gateway-context.replay.v1 with tier:requestId; COMMIT before returning true. Never refund a late veto.</summary>
    bool TryClaimNonceInTransaction(HrReadAdmission admission, CancellationToken cancellationToken);
    /// <summary>New query-only ReadSnapshot: guard authority before AND after reading; match result tenant/revision to admission.Target; bound materialization, then detach chart/history.</summary>
    MaterializedHrRead ReadInSnapshot(HrReadAdmission admission, CancellationToken cancellationToken);
    /// <summary>NEW snapshot after previous snapshot AND serialization have ended: recheck current registry/profile/policy/revocation/witness/allocation and original request/principal/native TLS certificate validity/EKU/pin. Async TLS retrieval must already have finished. Two checks in one snapshot are insufficient.</summary>
    bool RevalidateInNewSnapshot(HrReadAdmission admission, CancellationToken cancellationToken);
}

public interface IHrReadResponseWriter
{
    /// <summary>Immediately set status, application/json for 200, Cache-Control: no-store; send complete bytes. No deferred serialization/query. Host maps Denied to a no-store denial.</summary>
    ValueTask SendAsync(int statusCode, ReadOnlyMemory<byte> bytes, string cacheControl, CancellationToken cancellationToken);
}
public enum HrReadOutcome { Denied, Sent }

/// <summary>Execution candidate. No host, HTTP or authority adapters are registered by this library.</summary>
public sealed class HrReadCoordinator
{
    private readonly IHrReadAuthority authority;
    private readonly IHrReadFabricAdapter fabric;
    private readonly TimeProvider time;
    private readonly TimeSpan requestBudget;
    public HrReadCoordinator(IHrReadFabricAdapter fabric, IHrReadAuthority? authority = null, TimeProvider? time = null, TimeSpan? requestBudget = null)
    {
        this.fabric = fabric ?? throw new ArgumentNullException(nameof(fabric));
        this.authority = authority ?? new UnavailableHrReadAuthority(); this.time = time ?? TimeProvider.System;
        this.requestBudget = requestBudget ?? TimeSpan.FromSeconds(HrReadDesign.MaximumWitnessSeconds);
        if (this.requestBudget <= TimeSpan.Zero || this.requestBudget > TimeSpan.FromSeconds(HrReadDesign.MaximumWitnessSeconds))
            throw new ArgumentOutOfRangeException(nameof(requestBudget));
    }

    public async ValueTask<HrReadOutcome> ExecuteAsync(string method, string rawTarget, Stream body,
        IHrReadResponseWriter response, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body); ArgumentNullException.ThrowIfNull(response);
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = new CancellationTokenSource(requestBudget, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        cancellationToken = linked.Token;
        byte[] bytes; int status;
        try
        {
            if (!HrReadWire.TryParseTarget(method, rawTarget, out var target)
                || !await HrReadWire.IsActuallyEmptyAsync(body, cancellationToken)) return HrReadOutcome.Denied;
            var admission = await authority.AuthenticateAndResolveAsync(target!, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (admission is null || admission.Target != target || !IsCurrent(admission)) return HrReadOutcome.Denied;
            if (!fabric.TryClaimNonceInTransaction(admission, cancellationToken)) return HrReadOutcome.Denied;
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrent(admission)) return HrReadOutcome.Denied;
            var result = fabric.ReadInSnapshot(admission, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            // Snapshot CLOSED. Copy all serialized bytes BEFORE asking the fresh gate.
            bytes = Serialize(result, admission.MaximumResponseBytes, cancellationToken);
            status = result.Found ? 200 : 404;
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrent(admission) || !fabric.RevalidateInNewSnapshot(admission, cancellationToken)) return HrReadOutcome.Denied;
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrent(admission)) return HrReadOutcome.Denied;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return HrReadOutcome.Denied; } // Host logs sanitized categories, never bearer/witness/payload.
        // Send failure propagates; once transmission starts, do not relabel it as a safe denial.
        await response.SendAsync(status, bytes, "no-store", cancellationToken);
        return HrReadOutcome.Sent;
    }
    private bool IsCurrent(HrReadAdmission admission) => time.GetUtcNow() < admission.ValidUntil;
    private static byte[] Serialize(MaterializedHrRead result, int maximumBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Found) return [];
        using var output = new LimitedResponseStream(maximumBytes, cancellationToken);
        using (var json = new Utf8JsonWriter(output)) { result.Value.WriteTo(json); json.Flush(); }
        return output.ToArray();
    }
    private sealed class LimitedResponseStream(int maximumBytes, CancellationToken cancellationToken) : Stream
    {
        private readonly MemoryStream inner = new(Math.Min(maximumBytes, 4096));
        public byte[] ToArray() => inner.ToArray();
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        { cancellationToken.ThrowIfCancellationRequested(); if (buffer.Length > maximumBytes - inner.Length) throw new InvalidOperationException("Response exceeds configured byte limit."); inner.Write(buffer); }
        public override void Flush() => cancellationToken.ThrowIfCancellationRequested();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
