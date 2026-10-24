using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Fanasa.ContractPolicy;
using Fanasa.HrReadCandidate;

namespace Fanasa.HrReadPdp;

public interface IHrReadPdpSourceFactory
{
    /// <summary>Bind a source over the host's existing authoritative instance. This factory must not create authority records.</summary>
    IAccessAttributeSnapshotSource? Create(HrReadPdpRequest request, AuthenticatedHrReadPdpCaller caller,
        TrustedAccessSnapshotQuery query);
}

public sealed class UnavailableHrReadPdpSourceFactory : IHrReadPdpSourceFactory
{
    public IAccessAttributeSnapshotSource? Create(HrReadPdpRequest request, AuthenticatedHrReadPdpCaller caller,
        TrustedAccessSnapshotQuery query) => null;
}

public interface IHrReadPdpResponseWriter
{
    /// <summary>Send only the complete prepared JSON, 200 application/json, no-store. Do not defer any serialization.</summary>
    ValueTask SendAsync(byte[] bytes, DateTimeOffset validUntil, CancellationToken cancellationToken);
}

/// <summary>Read-only PDP transport. A Permit is a decision with unfulfilled obligations, never an admission receipt.</summary>
public sealed class HrReadPdpService
{
    public const int MaximumResponseBytes = 65536;
    private readonly ContractPolicyModule module;
    private readonly IHrReadPdpSourceFactory factory;
    private readonly TimeProvider time;
    public HrReadPdpService(ContractPolicyModule module, IHrReadPdpSourceFactory? factory = null, TimeProvider? time = null)
    {
        this.module = module ?? throw new ArgumentNullException(nameof(module));
        this.factory = factory ?? new UnavailableHrReadPdpSourceFactory(); this.time = time ?? TimeProvider.System;
    }

    public async ValueTask<bool> ExecuteAsync(HrReadPdpRequest request, AuthenticatedHrReadPdpCaller caller,
        IHrReadPdpResponseWriter writer, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request); ArgumentNullException.ThrowIfNull(caller); ArgumentNullException.ThrowIfNull(writer);
        cancellationToken.ThrowIfCancellationRequested();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5), time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(budget.Token, cancellationToken);
        var ct = linked.Token;
        byte[] bytes; DateTimeOffset deadline;
        try
        {
            if (!Current(request, caller)) return false;
            var query = Query(request, caller);
            var source = factory.Create(request, caller, query);
            if (source is null) return false;
            var provider = new TrustedAccessSnapshotProvider(source, module.Projections, time, TimeSpan.FromSeconds(5));
            var initial = await provider.ReadAsync(query, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (!initial.Succeeded || !Matches(request, initial.Projection!) || !Current(request, caller)) return false;
            var decision = module.Evaluator.Evaluate(initial.Projection, initial.Context);
            if (!ValidDecision(request, decision)) return false;
            deadline = Deadline(request, caller, initial, decision);
            if (time.GetUtcNow() >= deadline) return false;
            // The first source read has finished. No lazy object reaches the writer.
            bytes = SerializeBounded(Response(request, initial.Projection!, decision, deadline), ct);
            // The host source MUST open a new snapshot for this independent call.
            var final = await provider.ReadAsync(query, ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            if (!final.Succeeded || !Matches(request, final.Projection!) || !Current(request, caller)) return false;
            var freshDecision = module.Evaluator.Evaluate(final.Projection, final.Context);
            if (!ValidDecision(request, freshDecision) || DecisionFingerprint(decision) != DecisionFingerprint(freshDecision)
                || Deadline(request, caller, final, freshDecision) < deadline) return false;
            // No asynchronous authentication, database or policy operation follows this final gate.
            ct.ThrowIfCancellationRequested();
            if (!caller.RevalidateCurrent() || time.GetUtcNow() >= deadline) return false;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return false; } // No token, body, certificate or policy details are logged here.
        await writer.SendAsync(bytes, deadline, ct).ConfigureAwait(false);
        return true; // A send failure propagates; it must never become a safe denial.
    }

    public static TrustedAccessSnapshotQuery Query(HrReadPdpRequest request, AuthenticatedHrReadPdpCaller caller) => new(
        request.RequestId, new(request.SubjectId, true, true, request.Audience,
            AuthorizationScopes(caller)),
        new(request.ProductId, request.ConsumerTenantId, request.Environment, request.OperationGroup),
        request.Action, request.Zone, Format(new[] { request.ValidUntil, caller.ValidUntil }.Min()),
        new(request.ConsumerTenantId, request.ProfileId, request.ProductId, request.ContractId));

    /// <summary>
    /// Preserve verified scope order while projecting only identifiers representable by
    /// the immutable authorization schema. The proof retains and rechecks ALL raw scopes;
    /// unrelated OIDC scopes such as offline_access never become authorization attributes.
    /// </summary>
    public static string[] AuthorizationScopes(AuthenticatedHrReadPdpCaller caller) =>
        (Claim(caller.SubjectPrincipal, "scope")?.Split(' ') ?? []).Where(x =>
            x is { Length: > 0 and <= 64 } && x[0] is >= 'a' and <= 'z'
            && x.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-')).ToArray();

    private bool Current(HrReadPdpRequest request, AuthenticatedHrReadPdpCaller caller) =>
        caller.IsFor(request) && request.Binding.Enabled && request.Binding.Validate().Count == 0 && caller.RevalidateCurrent()
        && caller.SubjectTokenSha256 == request.TokenSha256
        && caller.SubjectValidUntil.ToUnixTimeSeconds() == request.TokenExpiresAt
        && time.GetUtcNow() < request.ValidUntil && time.GetUtcNow() < caller.ValidUntil
        && Claim(caller.SubjectPrincipal, "sub") == request.SubjectId
        && Claim(caller.SubjectPrincipal, "azp") == request.ClientId;

    private static string? Claim(ClaimsPrincipal principal, string name)
    {
        var values = principal.Claims.Where(x => x.Type == name).Take(2).ToArray();
        return values.Length == 1 ? values[0].Value : null;
    }

    private static bool Matches(HrReadPdpRequest r, ContractAccessProjection p) =>
        p.SchemaVersion == HrReadDesign.SchemaVersion && p.ProfileId == HrReadDesign.ProfileId
        && p.ConsumerTenantId == r.ConsumerTenantId && p.ProductId == r.ProductId && p.OwnerTenantId == r.OwnerTenantId
        && p.Ownership == r.Ownership && p.ContractId == r.ContractId && p.ContractVersion == r.ContractVersion
        && p.Policy.PolicyId == r.PolicyId && p.Policy.PolicyVersion == r.PolicyVersion
        && p.Limits.AllocationId == r.AllocationId && p.Limits.CanonicalScope == r.AllocationScope
        && p.Limits.ConfigRevision == r.ConfigRevision;

    private bool ValidDecision(HrReadPdpRequest r, PolicyDecision d) =>
        d.SchemaVersion == HrReadDesign.SchemaVersion && d.ProfileId == HrReadDesign.ProfileId && d.RequestId == r.RequestId
        && d.Decision is "Permit" or "Deny" or "NotApplicable" or "Indeterminate"
        && module.Schemas.Validate("decision.v3.schema.json", ContractJson.ToNode(d)).Count == 0;

    private DateTimeOffset Deadline(HrReadPdpRequest r, AuthenticatedHrReadPdpCaller caller,
        TrustedAccessSnapshotResult snapshot, PolicyDecision decision)
    {
        var values = new List<DateTimeOffset> { r.ValidUntil, caller.ValidUntil, Instant(snapshot.Context!.Evidence.ValidUntil) };
        if (decision.ValidUntil is not null) values.Add(Instant(decision.ValidUntil));
        // The current allocation period also bounds every response, including non-Permit decisions.
        var now = time.GetUtcNow().ToUniversalTime();
        var month = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        values.Add(month.AddMonths(1));
        return values.Min();
    }

    private object Response(HrReadPdpRequest r, ContractAccessProjection p, PolicyDecision decision, DateTimeOffset deadline)
    {
        var now = time.GetUtcNow().ToUniversalTime();
        var period = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        return new
        {
            transportVersion = HrReadPdpWire.ResponseVersion, schemaVersion = HrReadDesign.SchemaVersion,
            profileId = HrReadDesign.ProfileId, r.RequestId, r.Method, r.RawTarget, r.SubjectId, r.ClientId,
            r.ConsumerTenantId, r.OwnerTenantId, r.Ownership, r.ProductId, r.Audience, r.Environment, r.Zone,
            r.Action, r.OperationGroup, r.TokenSha256, r.TokenExpiresAt, r.ContractId, r.ContractVersion,
            r.PolicyId, r.PolicyVersion, r.AllocationId, r.AllocationScope, r.ConfigRevision,
            allocationPeriodId = period.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            periodStartMs = period.ToUnixTimeMilliseconds(), periodEndMs = period.AddMonths(1).ToUnixTimeMilliseconds(),
            evaluatedAt = Epoch(Instant(decision.EvaluatedAt!)), validUntil = Epoch(deadline),
            admissionConfirmed = false, decision
        };
    }

    private static string DecisionFingerprint(PolicyDecision decision) => JsonSerializer.Serialize(new
    { decision.Decision, decision.ReasonCode, decision.PolicyReferences, decision.ProjectionReference,
        decision.AllocationReference, decision.Obligations }, ContractJson.Options);

    private static byte[] SerializeBounded(object value, CancellationToken ct)
    {
        using var stream = new BoundedStream(MaximumResponseBytes, ct);
        using (var writer = new Utf8JsonWriter(stream)) { JsonSerializer.Serialize(writer, value, ContractJson.Options); writer.Flush(); }
        return stream.ToArray();
    }
    private static DateTimeOffset Instant(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);
    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    private static decimal Epoch(DateTimeOffset value) => (value.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / (decimal)TimeSpan.TicksPerSecond;

    private sealed class BoundedStream(int maximum, CancellationToken ct) : Stream
    {
        private readonly MemoryStream inner = new();
        public byte[] ToArray() => inner.ToArray();
        public override bool CanRead => false; public override bool CanSeek => false; public override bool CanWrite => true;
        public override long Length => inner.Length; public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer)
        { ct.ThrowIfCancellationRequested(); if (inner.Length + buffer.Length > maximum) throw new InvalidDataException("PDP response bound exceeded."); inner.Write(buffer); }
        public override void Flush() => ct.ThrowIfCancellationRequested();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
