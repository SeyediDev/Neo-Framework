using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fanasa.AccessManagement.Web.Persistence;
using Fanasa.ContractPolicy;
using Fanasa.HrReadCandidate;
using Fanasa.HrReadPdp;

namespace Fanasa.HrReadHost;

public sealed record Hr3PolicyPublication(ContractPolicyBinding Binding, bool Active);
public sealed record Hr3RoleAssignment(string Subject, Guid RoleId, bool Active,
    DateTimeOffset ValidFrom, DateTimeOffset ValidTo);
/// <summary>Complete explicit input. Zero expected revisions mean absent records, never a default grant.</summary>
public sealed record Hr3PublicationRequest(ContractAccessProjection Projection,
    Hr3PolicyPublication Policy, Hr3RoleAssignment Assignment,
    long ExpectedContractVersion, long ExpectedAllocationConfigRevision,
    long ExpectedPolicyRevision, long ExpectedAssignmentRevision, string Actor, string Reason);
public sealed record Hr3PublicationResult(ProjectionState Projection,
    HrReadPdpPublishedPolicy Policy, HrReadPdpProductRoleAssignment Assignment, string PublicationSha256);

/// <summary>
/// A trusted adapter's CURRENT source-derived proof. PublicationSha256 binds the exact detached
/// request; SourceRevision identifies independently read contract/allocation/publication authority.
/// This record is not accepted from HTTP JSON. The adapter must authenticate the administrative
/// actor, independently authorize publication of the supplied source contract, policy, allocation
/// and assignment, and read the actual existing registry/access records from the same database.
/// No boolean in Hr3PublicationRequest can stand in for that authorization.
/// </summary>
public sealed record Hr3PublicationAuthoritySnapshot(string ActorSubjectId, string PublicationSha256,
    string SourceRevision, HrReadRegistration Registration, TenantAttributes Tenant,
    MembershipAttributes Membership, HrReadPdpHostRoleSnapshot Role, GrantAttributes Grant,
    DateTimeOffset ValidUntil);
public interface IHr3PublicationAuthority
{
    /// <summary>Synchronous under the same existing Fabric transaction; no write, nested transaction,
    /// cached Permit or sync-over-async. Return null on missing, changed or unavailable authority.</summary>
    Hr3PublicationAuthoritySnapshot? ReadCurrent(FabricDatabase database, Hr3PublicationRequest request,
        CancellationToken cancellationToken);
}
public sealed class UnavailableHr3PublicationAuthority : IHr3PublicationAuthority
{
    public Hr3PublicationAuthoritySnapshot? ReadCurrent(FabricDatabase database,
        Hr3PublicationRequest request, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); return null; }
}

/// <summary>
/// Opt-in HR3 publisher over the host's existing FabricDatabase. All projection, current allocation,
/// immutable allocation revision, immutable policy version, scoped policy/assignment and audit writes
/// commit together. It never creates tenants, products, memberships, roles, direct grants, contracts,
/// allocations, fulfillment, quota debits or nonce records from defaults. The complete projection and
/// allocation must already be authorized by IHr3PublicationAuthority's independent source adapter.
/// Legacy consumer-only policy/assignment aliases and v1/v2 projection records are never overwritten.
/// </summary>
public sealed class Hr3PublicationStore(FabricDatabase database, ContractProjectionValidator validator,
    IHr3PublicationAuthority? authority = null, TimeProvider? time = null)
{
    private const long Max = 9007199254740991;
    private const int MaxBytes = 1024 * 1024;
    private readonly IHr3PublicationAuthority authority = authority ?? new UnavailableHr3PublicationAuthority();
    private readonly TimeProvider time = time ?? TimeProvider.System;
    private static readonly JsonSerializerOptions StoredJson = new()
    {
        PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true, MaxDepth = 32
    };

    public static string Fingerprint(Hr3PublicationRequest request) =>
        Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request, ContractJson.Options)));
    public static string AllocationVersionKey(string allocationId, long configRevision)
    {
        if (!Id(allocationId) || configRevision is < 1 or > Max) throw new ArgumentException("Invalid allocation revision identity.");
        return allocationId + ":" + configRevision.ToString(CultureInfo.InvariantCulture);
    }

    public Hr3PublicationResult Publish(Hr3PublicationRequest input, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database); ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(input); cancellationToken.ThrowIfCancellationRequested();
        if (database.IsInTransaction) throw new InvalidOperationException("HR3 publication requires its own committed Fabric transaction.");
        var encoded = JsonSerializer.SerializeToUtf8Bytes(input, ContractJson.Options);
        if (encoded.Length > MaxBytes) throw new ArgumentException("Publication exceeds the technical input bound.");
        var request = JsonSerializer.Deserialize<Hr3PublicationRequest>(encoded, ContractJson.Options)
            ?? throw new ArgumentException("Missing publication.");
        Validate(request);
        var fingerprint = Fingerprint(request);
        return database.Transaction(() => PublishAtomic(request, fingerprint, cancellationToken));
    }

    private Hr3PublicationResult PublishAtomic(Hr3PublicationRequest r, string fingerprint, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var first = ReadAuthority(r, fingerprint, ct);
        var selector = new ProjectionSelector(r.Projection.ConsumerTenantId, r.Projection.ProfileId,
            r.Projection.ProductId, r.Projection.ContractId);
        var policyKey = HrReadPdpFabricSource.PolicyKey(selector);
        var assignmentKey = HrReadPdpFabricSource.RoleAssignmentKey(selector, r.Assignment.Subject);
        var previous = Read<ProjectionState>(HrReadPdpFabricSource.ProjectionBucket, r.Projection.ContractId);
        if (previous is not null) ValidatePrevious(previous, r.Projection.ContractId);
        var replay = TryReplay(r, fingerprint, previous, policyKey, assignmentKey, first, ct);
        if (replay is not null) return replay;
        Revision(previous?.Projection.ContractVersion ?? 0, r.ExpectedContractVersion);
        if (previous is not null) ProjectionProgression(previous.Projection, r.Projection);

        var allocation = Read<AllocationLimits>(HrReadPdpFabricSource.AllocationBucket, r.Projection.Limits.AllocationId);
        if (allocation is not null) ValidateAllocation(allocation, r.Projection.Limits.AllocationId);
        Revision(allocation?.ConfigRevision ?? 0, r.ExpectedAllocationConfigRevision);
        if (allocation is not null)
        {
            if (allocation.CanonicalScope != r.Projection.Limits.CanonicalScope ||
                allocation.Scope.ConsumerTenantId != r.Projection.ConsumerTenantId)
                throw new InvalidOperationException("Allocation identity or scope cannot be rebound.");
            if (r.Projection.Limits.ConfigRevision < allocation.ConfigRevision ||
                r.Projection.Limits.ConfigRevision == allocation.ConfigRevision && !Equal(allocation, r.Projection.Limits))
                throw new InvalidOperationException("Allocation revision is stale or identifies different content.");
        }
        var immutableAllocationKey = AllocationVersionKey(r.Projection.Limits.AllocationId, r.Projection.Limits.ConfigRevision!.Value);
        var immutableAllocation = Read<AllocationLimits>(HrReadPdpFabricSource.AllocationBucket, immutableAllocationKey);
        if (immutableAllocation is not null && !Equal(immutableAllocation, r.Projection.Limits))
            throw new InvalidOperationException("Immutable allocation revision conflict.");

        var policy = Read<HrReadPdpPublishedPolicy>(HrReadPdpFabricSource.PolicyBucket, policyKey);
        if (policy is not null) ValidatePreviousPolicy(policy, r.Projection.ConsumerTenantId);
        Revision(policy?.Revision ?? 0, r.ExpectedPolicyRevision);
        if (policy is not null && (policy.Binding.PolicyId != r.Policy.Binding.PolicyId ||
            r.Policy.Binding.PolicyVersion < policy.Binding.PolicyVersion ||
            r.Policy.Binding.PolicyVersion == policy.Binding.PolicyVersion && !Equal(policy.Binding, r.Policy.Binding)))
            throw new InvalidOperationException("Current policy identity/version conflict.");
        var immutablePolicyKey = HrReadPdpFabricSource.PolicyVersionKey(r.Policy.Binding.PolicyId, r.Policy.Binding.PolicyVersion);
        var immutablePolicy = Read<ContractPolicyBinding>(HrReadPdpFabricSource.PolicyVersionBucket, immutablePolicyKey);
        if (immutablePolicy is not null && !Equal(immutablePolicy, r.Policy.Binding))
            throw new InvalidOperationException("Immutable policy ID/version conflict.");

        var assignment = Read<HrReadPdpProductRoleAssignment>(HrReadPdpFabricSource.RoleAssignmentBucket, assignmentKey);
        if (assignment is not null) ValidatePreviousAssignment(assignment, r.Projection.ConsumerTenantId, r.Assignment.Subject);
        Revision(assignment?.Revision ?? 0, r.ExpectedAssignmentRevision);
        if (!r.Assignment.Active && (assignment is null || assignment.RoleId != r.Assignment.RoleId ||
            assignment.ValidFrom != r.Assignment.ValidFrom || assignment.ValidTo != r.Assignment.ValidTo))
            throw new InvalidOperationException("Revocation must preserve an existing assignment binding and window.");

        var now = time.GetUtcNow().ToUniversalTime();
        var published = new HrReadPdpPublishedPolicy(Guid.Parse(r.Projection.ConsumerTenantId),
            ContractJson.Copy(r.Policy.Binding), r.Policy.Active, checked(r.ExpectedPolicyRevision + 1), r.Actor, r.Reason, now);
        var assigned = new HrReadPdpProductRoleAssignment(Guid.Parse(r.Projection.ConsumerTenantId),
            r.Assignment.Subject, r.Assignment.RoleId, r.Assignment.Active, r.Assignment.ValidFrom, r.Assignment.ValidTo,
            checked(r.ExpectedAssignmentRevision + 1), r.Actor, r.Reason);
        var receiptId = "hr3-publication-" + fingerprint[..32];
        var receipts = previous?.Receipts.ToList() ?? [];
        if (receipts.Any(x => x.EventId == receiptId && x.Fingerprint != fingerprint))
            throw new InvalidOperationException("Publication receipt collision.");
        if (!receipts.Any(x => x.EventId == receiptId)) receipts.Add(new(receiptId, fingerprint));
        if (receipts.Count > 10000) throw new InvalidOperationException("Projection receipt bound exceeded.");
        var state = new ProjectionState(ContractJson.Copy(r.Projection), receipts.ToArray());
        // Preserve any previously current allocation as immutable before advancing it.
        if (allocation is not null)
        {
            var oldKey = AllocationVersionKey(allocation.AllocationId, allocation.ConfigRevision!.Value);
            var oldImmutable = Read<AllocationLimits>(HrReadPdpFabricSource.AllocationBucket, oldKey);
            if (oldImmutable is not null && !Equal(oldImmutable, allocation))
                throw new InvalidOperationException("Previously current allocation conflicts with immutable history.");
            if (oldImmutable is null) database.Put(HrReadPdpFabricSource.AllocationBucket, oldKey, allocation);
        }
        if (immutableAllocation is null) database.Put(HrReadPdpFabricSource.AllocationBucket, immutableAllocationKey, ContractJson.Copy(r.Projection.Limits));
        if (immutablePolicy is null) database.Put(HrReadPdpFabricSource.PolicyVersionBucket, immutablePolicyKey, ContractJson.Copy(r.Policy.Binding));
        database.Put(HrReadPdpFabricSource.AllocationBucket, r.Projection.Limits.AllocationId, ContractJson.Copy(r.Projection.Limits));
        database.Put(HrReadPdpFabricSource.ProjectionBucket, r.Projection.ContractId, state);
        database.Put(HrReadPdpFabricSource.PolicyBucket, policyKey, published);
        database.Put(HrReadPdpFabricSource.RoleAssignmentBucket, assignmentKey, assigned);
        database.Emit("hr3-publication:" + Guid.NewGuid().ToString("N"), "Hr3PublicationCommitted", new
        {
            TenantId = Guid.Parse(r.Projection.ConsumerTenantId), Selector = selector, PublicationSha256 = fingerprint,
            r.Projection.ContractVersion, r.Projection.Limits.ConfigRevision,
            PolicyRevision = published.Revision, AssignmentRevision = assigned.Revision,
            r.Policy.Binding.PolicyVersion, r.Actor, r.Reason, AuthoritySourceRevision = first.SourceRevision
        }, now);
        var last = ReadAuthority(r, fingerprint, ct);
        if (last.ValidUntil > first.ValidUntil || !Equal(first with { ValidUntil = last.ValidUntil }, last))
            throw new UnauthorizedAccessException("Publication authority changed before commit.");
        ct.ThrowIfCancellationRequested();
        return ContractJson.Copy(new Hr3PublicationResult(state, published, assigned, fingerprint));
    }

    private Hr3PublicationResult? TryReplay(Hr3PublicationRequest r, string fingerprint, ProjectionState? state,
        string policyKey, string assignmentKey, Hr3PublicationAuthoritySnapshot first, CancellationToken ct)
    {
        if (state is null || !state.Receipts.Any(x => x.EventId == "hr3-publication-" + fingerprint[..32] && x.Fingerprint == fingerprint))
            return null;
        var policy = Read<HrReadPdpPublishedPolicy>(HrReadPdpFabricSource.PolicyBucket, policyKey);
        var assignment = Read<HrReadPdpProductRoleAssignment>(HrReadPdpFabricSource.RoleAssignmentBucket, assignmentKey);
        var allocation = Read<AllocationLimits>(HrReadPdpFabricSource.AllocationBucket, r.Projection.Limits.AllocationId);
        if (policy is null || assignment is null || allocation is null)
            throw new InvalidDataException("A publication receipt has missing committed records.");
        ValidatePreviousPolicy(policy, r.Projection.ConsumerTenantId);
        ValidatePreviousAssignment(assignment, r.Projection.ConsumerTenantId, r.Assignment.Subject);
        ValidateAllocation(allocation, r.Projection.Limits.AllocationId);
        if (!Equal(state.Projection, r.Projection) || !Equal(allocation, r.Projection.Limits) ||
            !Equal(policy.Binding, r.Policy.Binding) || policy.Active != r.Policy.Active ||
            policy.Revision != r.ExpectedPolicyRevision + 1 || policy.Actor != r.Actor || policy.Reason != r.Reason ||
            assignment.Revision != r.ExpectedAssignmentRevision + 1 || assignment.Subject != r.Assignment.Subject ||
            assignment.RoleId != r.Assignment.RoleId || assignment.Active != r.Assignment.Active ||
            assignment.ValidFrom != r.Assignment.ValidFrom || assignment.ValidTo != r.Assignment.ValidTo ||
            assignment.Actor != r.Actor || assignment.Reason != r.Reason ||
            !Equal(Read<AllocationLimits>(HrReadPdpFabricSource.AllocationBucket,
                AllocationVersionKey(allocation.AllocationId, allocation.ConfigRevision!.Value)), allocation))
            throw new InvalidOperationException("Previously committed publication has advanced or differs; replay cannot overwrite it.");
        var last = ReadAuthority(r, fingerprint, ct);
        if (last.ValidUntil > first.ValidUntil || !Equal(first with { ValidUntil = last.ValidUntil }, last))
            throw new UnauthorizedAccessException("Publication authority changed before replay confirmation.");
        ct.ThrowIfCancellationRequested();
        return ContractJson.Copy(new Hr3PublicationResult(state, policy, assignment, fingerprint));
    }

    private Hr3PublicationAuthoritySnapshot ReadAuthority(Hr3PublicationRequest r, string fingerprint, CancellationToken ct)
    {
        var value = authority.ReadCurrent(database, ContractJson.Copy(r), ct);
        ct.ThrowIfCancellationRequested();
        var p = r.Projection; var a = r.Assignment; var now = time.GetUtcNow();
        if (value is null || value.ActorSubjectId != r.Actor || value.PublicationSha256 != fingerprint ||
            !Label(value.SourceRevision, 200) || value.ValidUntil.Offset != TimeSpan.Zero || value.ValidUntil <= now ||
            value.Registration is not { } registration || registration.Id.ToString("D") != p.Ownership!.RegisteredProductId ||
            registration.Key != p.ProductId || registration.Audience != HrReadProfile.Audience ||
            (p.Ownership.Kind == "Platform" ? registration.Owner != "platform" || registration.TenantId is not null :
                registration.Owner != p.Ownership.Owner || registration.TenantId?.ToString("D") != p.OwnerTenantId) ||
            value.Tenant is not { } tenant || tenant.TenantId != p.ConsumerTenantId ||
            value.Membership is not { } member || member.SubjectId != a.Subject || member.TenantId != p.ConsumerTenantId ||
            value.Role is not { } role || role.Id != a.RoleId || role.Key != HrReadProfile.Role ||
            value.Grant is not { } grant || !Id(grant.GrantId) || grant.SubjectId != a.Subject ||
            grant.ConsumerTenantId != p.ConsumerTenantId || grant.ProductId != p.ProductId || grant.Role != HrReadProfile.Role)
            throw new UnauthorizedAccessException("Current authoritative publication/registry/access proof is unavailable or mismatched.");
        if (p.Status == "Active" && r.Policy.Active && a.Active &&
            (!tenant.Active || !member.Active || !Current(member.ValidFrom, member.ValidTo, now) ||
             role.Permissions is null || !role.Permissions.Contains(HrReadDesign.Permission, StringComparer.Ordinal) ||
             !grant.Active || !Current(grant.ValidFrom, grant.ValidTo, now) || grant.Actions is null ||
             p.FeatureEntitlements.Any(x => !grant.Actions.Contains(x, StringComparer.Ordinal)) ||
             a.ValidTo > Instant(member.ValidTo) || a.ValidTo > Instant(grant.ValidTo) || a.ValidTo > Instant(p.ValidTo)))
            throw new UnauthorizedAccessException("An active publication requires existing current membership, native read role and direct grant for its complete validity.");
        return ContractJson.Copy(value);
    }

    private void Validate(Hr3PublicationRequest r)
    {
        var p = r.Projection;
        if (p is null || validator.Validate(p).Count != 0 || p.SchemaVersion != HrReadProfile.SchemaVersion ||
            p.ProfileId != HrReadProfile.ProfileId || p.ProductId != HrReadProfile.ProductId ||
            !Uuid(p.ConsumerTenantId) || r.Policy is null || !Equal(p.Policy, r.Policy.Binding) ||
            r.Assignment is not { } a || !Uuid(a.Subject) || a.RoleId == Guid.Empty ||
            a.ValidFrom.Offset != TimeSpan.Zero || a.ValidTo.Offset != TimeSpan.Zero || a.ValidFrom >= a.ValidTo ||
            !Uuid(r.Actor) || !Label(r.Reason, 1000)) throw new ArgumentException("A complete valid explicit HR3 publication is required.");
        foreach (var expected in new[] { r.ExpectedContractVersion, r.ExpectedAllocationConfigRevision, r.ExpectedPolicyRevision, r.ExpectedAssignmentRevision })
            if (expected is < 0 or >= Max) throw new ArgumentOutOfRangeException(nameof(r), "Expected revisions must be exact nonnegative safe integers.");
    }
    private void ValidatePrevious(ProjectionState value, string key)
    {
        if (value.Projection is null || value.Projection.ContractId != key ||
            value.Projection.SchemaVersion != HrReadProfile.SchemaVersion || value.Projection.ProfileId != HrReadProfile.ProfileId ||
            value.Projection.ProductId != HrReadProfile.ProductId || validator.Validate(value.Projection).Count != 0 ||
            value.Receipts is null || value.Receipts.Any(x => x is null || !Id(x.EventId) || !Hash(x.Fingerprint)) ||
            value.Receipts.Select(x => x.EventId).Distinct(StringComparer.Ordinal).Count() != value.Receipts.Count)
            throw new InvalidDataException("Stored projection is invalid or requires explicit migration from another profile.");
    }
    private static void ProjectionProgression(ContractAccessProjection old, ContractAccessProjection next)
    {
        if (old.ContractId != next.ContractId || old.SchemaVersion != next.SchemaVersion || old.ProfileId != next.ProfileId ||
            old.OwnerTenantId != next.OwnerTenantId || old.Ownership != next.Ownership || old.ConsumerTenantId != next.ConsumerTenantId ||
            old.ProductId != next.ProductId || old.OfferId != next.OfferId || old.SubscriptionId != next.SubscriptionId ||
            old.Limits.AllocationId != next.Limits.AllocationId || old.Limits.CanonicalScope != next.Limits.CanonicalScope)
            throw new InvalidOperationException("Contract aggregate identity cannot change.");
        if (next.ContractVersion < old.ContractVersion || next.ContractVersion == old.ContractVersion && !Equal(old, next) ||
            old.Status == "Ended" && next.Status != "Ended")
            throw new InvalidOperationException("Contract version is stale, conflicts, or resumes an ended contract.");
    }
    private void ValidateAllocation(AllocationLimits a, string key)
    {
        if (a.AllocationId != key || a.ConfigRevision is not >= 1 ||
            validator.Schemas.Validate("limits.v2.schema.json", ContractJson.ToNode(a)).Count != 0 ||
            a.CanonicalScope != ContractProjectionValidator.CanonicalScope(a.Scope))
            throw new InvalidDataException("Stored allocation is invalid.");
    }
    private void ValidatePreviousPolicy(HrReadPdpPublishedPolicy p, string tenant)
    {
        if (p.ConsumerTenantId.ToString("D") != tenant || p.Revision is < 1 or > Max ||
            !Uuid(p.Actor) || !Label(p.Reason, 1000) || p.RecordedAt.Offset != TimeSpan.Zero ||
            p.RecordedAt > time.GetUtcNow() || p.Binding is null || p.Binding.TemplateId != HrReadProfile.TemplateId ||
            !Equal(Read<ContractPolicyBinding>(HrReadPdpFabricSource.PolicyVersionBucket,
                HrReadPdpFabricSource.PolicyVersionKey(p.Binding.PolicyId, p.Binding.PolicyVersion)), p.Binding))
            throw new InvalidDataException("Stored policy publication or immutable version is invalid.");
    }
    private static void ValidatePreviousAssignment(HrReadPdpProductRoleAssignment a, string tenant, string subject)
    {
        if (a.TenantId.ToString("D") != tenant || a.Subject != subject || a.RoleId == Guid.Empty ||
            a.Revision is < 1 or > Max || !Uuid(a.Actor) || !Label(a.Reason, 1000) ||
            a.ValidFrom.Offset != TimeSpan.Zero || a.ValidTo.Offset != TimeSpan.Zero || a.ValidFrom >= a.ValidTo)
            throw new InvalidDataException("Stored role assignment is invalid.");
    }
    private T? Read<T>(string bucket, string key) where T : class
    {
        var document = database.ReadDocument(bucket, key);
        if (document is null) return null;
        if (document.Json.Length is 0 or > MaxBytes || Encoding.UTF8.GetByteCount(document.Json) > MaxBytes)
            throw new InvalidDataException("Stored publication document exceeds its parsing bound.");
        using var json = JsonDocument.Parse(document.Json, new JsonDocumentOptions { MaxDepth = 32 });
        NoDuplicates(json.RootElement);
        return json.RootElement.Deserialize<T>(StoredJson) ?? throw new InvalidDataException("Missing persisted record.");
    }
    private static void NoDuplicates(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in node.EnumerateObject())
            { if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate persisted property."); NoDuplicates(property.Value); }
        }
        else if (node.ValueKind == JsonValueKind.Array) foreach (var item in node.EnumerateArray()) NoDuplicates(item);
    }
    private static void Revision(long current, long expected)
    { if (current != expected) throw new InvalidOperationException("Publication state changed; reload exact revisions."); }
    private static bool Equal<T>(T left, T right) => JsonSerializer.Serialize(left, ContractJson.Options) == JsonSerializer.Serialize(right, ContractJson.Options);
    private static bool Uuid(string? value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty && id.ToString("D") == value;
    private static bool Id(string? value) => value is { Length: > 0 and <= 64 } && value[0] is >= 'a' and <= 'z' && value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-');
    private static bool Hash(string? value) => value is { Length: 64 } && value.All(c => c is >= 'a' and <= 'f' or >= '0' and <= '9');
    private static bool Label(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max && !value.Any(char.IsControl);
    private static DateTimeOffset Instant(string value) => DateTimeOffset.ParseExact(value, "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    private static bool Current(string from, string to, DateTimeOffset now) => ContractJson.IsCanonicalUtc(from) && ContractJson.IsCanonicalUtc(to) && Instant(from) <= now && now < Instant(to);
}
