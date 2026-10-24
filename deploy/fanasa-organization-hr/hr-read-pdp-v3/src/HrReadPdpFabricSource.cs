using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Fanasa.AccessManagement.Web.Persistence;
using Fanasa.ContractPolicy;
using Fanasa.HrReadCandidate;

namespace Fanasa.HrReadPdp;

public sealed record HrReadPdpHostRoleSnapshot(Guid Id, string Key, IReadOnlyList<string> Permissions);

/// <summary>
/// Detached, independently authenticated CURRENT host records, not request assertions.
/// The host owns existing registry/access/client/introspection/delegation/native permission
/// and legacy-veto adapters. Returning this shape never creates any of those authorities.
/// SubjectAuthenticated includes validation of the proof's subject token; UniqueConsumerMapping
/// requires exactly one authenticated consumer mapping, not a tenant claim selected from an array.
/// </summary>
public sealed record HrReadPdpHostRecordSnapshot(
    string CallerSubjectId, string CallerClientId, SubjectAttributes Subject, ResourceAttributes Resource,
    HrReadRegistration Registration, TenantAttributes Tenant, MembershipAttributes Membership,
    HrReadPdpHostRoleSnapshot Role, GrantAttributes Grant, DateTimeOffset ValidUntil,
    bool CallerDelegationAllowed, bool SubjectAuthenticated, bool UniqueConsumerMapping,
    bool CurrentClientAuthorized, bool NativePermissionAllowed, bool LegacyVetoClear);

public interface IHrReadPdpHostRecords
{
    /// <summary>
    /// Synchronous read under the passed SAME query-only Fabric snapshot. Verify current
    /// caller delegation and retained proof, subject's unique authenticated mapping, current
    /// product client, tenant/membership/role/native permission/direct grant and legacy veto.
    /// Read the actual central registry, never request.Binding as a registry replacement.
    /// No await, sync-over-async, additional transaction, cached Permit, write or debit.
    /// A request-scoped adapter may retain previously completed cryptographic/introspection
    /// work, but must check its current revocation/expiry and record versions on EACH call.
    /// Repeated reads must return the same record content when authority is unchanged;
    /// ValidUntil may shorten. Throw or return null if authoritative reads are unavailable.
    /// </summary>
    HrReadPdpHostRecordSnapshot? ReadCurrent(FabricDatabase database, TrustedAccessSnapshotQuery query,
        AuthenticatedHrReadPdpCaller caller, CancellationToken cancellationToken);
}

public sealed class UnavailableHrReadPdpHostRecords : IHrReadPdpHostRecords
{
    public HrReadPdpHostRecordSnapshot? ReadCurrent(FabricDatabase database, TrustedAccessSnapshotQuery query,
        AuthenticatedHrReadPdpCaller caller, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); return null; }
}

// Read shapes pinned to existing ContractPolicyStore.cs; these do not provide a publisher.
public sealed record HrReadPdpPublishedPolicy(Guid ConsumerTenantId, ContractPolicyBinding Binding,
    bool Active, long Revision, string Actor, string Reason, DateTimeOffset RecordedAt);
public sealed record HrReadPdpProductRoleAssignment(Guid TenantId, string Subject, Guid RoleId,
    bool Active, DateTimeOffset ValidFrom, DateTimeOffset ValidTo, long Revision, string Actor, string Reason);

/// <summary>
/// Read-only HR3 PIP over the host's existing FabricDatabase instance. No database/path,
/// registry, ledger, nonce or fulfillment state is created. Each call opens a NEW query-only
/// snapshot, including a final call after serialization. No previous read is a fallback.
/// Profile-aware policy/assignment records must be provisioned by an authorized future
/// publisher in the existing buckets; legacy consumer-only aliases are never consulted.
/// </summary>
public sealed class HrReadPdpFabricSource : IAccessAttributeSnapshotSource
{
    public const string ProjectionBucket = "contract-policy.projection";
    public const string AllocationBucket = "contract-policy.allocation";
    public const string PolicyBucket = "contract-policy.policy";
    public const string PolicyVersionBucket = "contract-policy.policy-version";
    public const string RoleAssignmentBucket = "contract-policy.role-assignment";
    public const int MaximumSourceDocumentBytes = 1024 * 1024;
    public const int MaximumProjectionDocuments = 10000;
    private const long MaximumSafeInteger = 9007199254740991;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions StoredJson = new()
    {
        PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true, MaxDepth = 32
    };
    private readonly FabricDatabase database;
    private readonly ContractProjectionValidator validator;
    private readonly HrReadPdpRequest request;
    private readonly TrustedAccessSnapshotQuery expectedQuery;
    private readonly AuthenticatedHrReadPdpCaller caller;
    private readonly IHrReadPdpHostRecords records;
    private readonly TimeProvider time;

    public HrReadPdpFabricSource(FabricDatabase existingDatabase, ContractProjectionValidator validator,
        HrReadPdpRequest expectedRequest, TrustedAccessSnapshotQuery expectedQuery,
        AuthenticatedHrReadPdpCaller authenticatedCaller, IHrReadPdpHostRecords? records = null, TimeProvider? time = null)
    {
        database = existingDatabase ?? throw new ArgumentNullException(nameof(existingDatabase));
        this.validator = validator ?? throw new ArgumentNullException(nameof(validator));
        request = expectedRequest ?? throw new ArgumentNullException(nameof(expectedRequest));
        this.expectedQuery = ContractJson.Copy(expectedQuery ?? throw new ArgumentNullException(nameof(expectedQuery)));
        caller = authenticatedCaller ?? throw new ArgumentNullException(nameof(authenticatedCaller));
        this.records = records ?? new UnavailableHrReadPdpHostRecords(); this.time = time ?? TimeProvider.System;
    }

    public static string PolicyKey(ProjectionSelector selector)
    {
        ArgumentNullException.ThrowIfNull(selector);
        if (!CanonicalGuid(selector.ConsumerTenantId) || selector.ProfileId != HrReadProfile.ProfileId
            || selector.ProductId != HrReadProfile.ProductId || !Id(selector.ContractId))
            throw new ArgumentException("A complete canonical HR3 selector is required.", nameof(selector));
        return string.Join('|', selector.ConsumerTenantId, selector.ProfileId, selector.ProductId, selector.ContractId);
    }
    public static string RoleAssignmentKey(ProjectionSelector selector, string subject)
    {
        if (!CanonicalGuid(subject)) throw new ArgumentException("A canonical authenticated subject is required.", nameof(subject));
        return PolicyKey(selector) + "|" + subject;
    }
    public static string PolicyVersionKey(string policyId, long version)
    {
        if (!Id(policyId) || version is < 1 or > MaximumSafeInteger) throw new ArgumentException("Invalid immutable policy identity.");
        return policyId + ":" + version.ToString(CultureInfo.InvariantCulture);
    }

    public ValueTask<AccessAttributeSnapshotData?> ReadAsync(TrustedAccessSnapshotQuery query, CancellationToken cancellationToken = default)
        => ValueTask.FromResult(RevalidateCurrent(query, cancellationToken));

    /// <summary>A fresh source read, never the previous snapshot or a stored Permit.</summary>
    public AccessAttributeSnapshotData? RevalidateCurrent(TrustedAccessSnapshotQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (database.IsInTransaction || query is null || !Equivalent(query, expectedQuery)
                || !RequestMatches(query, time.GetUtcNow())) return null;
            // Own the graph handed to synchronous host records; no retained caller list can
            // mutate the expected request or the provider's retained query.
            var ownedQuery = ContractJson.Copy(query);
            return database.ReadSnapshot(() => ReadSnapshot(ownedQuery, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { cancellationToken.ThrowIfCancellationRequested(); return null; }
    }

    private AccessAttributeSnapshotData? ReadSnapshot(TrustedAccessSnapshotQuery query, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!RequestMatches(query, time.GetUtcNow())) return null;
        var first = Capture(query, cancellationToken);
        if (first is null || !HostMatches(first, query, time.GetUtcNow())) return null;

        var selected = ContractProjectionSelector.FindSingle(ReadProjections(cancellationToken), query.Selector!);
        if (selected is null || !ProjectionMatches(selected, first.Registration, time.GetUtcNow())) return null;
        var allocation = Read<AllocationLimits>(AllocationBucket, selected.Limits.AllocationId);
        if (allocation is null || !Equivalent(allocation, selected.Limits)) return null;
        var published = Read<HrReadPdpPublishedPolicy>(PolicyBucket, PolicyKey(query.Selector!));
        if (published is null || !published.Active || published.ConsumerTenantId.ToString("D") != query.Resource.ConsumerTenantId
            || published.Revision is < 1 or > MaximumSafeInteger || !Labels(published.Actor, published.Reason)
            || published.RecordedAt.Offset != TimeSpan.Zero || published.RecordedAt > time.GetUtcNow()
            || !Equivalent(published.Binding, selected.Policy)) return null;
        var immutable = Read<ContractPolicyBinding>(PolicyVersionBucket,
            PolicyVersionKey(selected.Policy.PolicyId, selected.Policy.PolicyVersion));
        if (immutable is null || !Equivalent(immutable, published.Binding)) return null;
        var assignment = Read<HrReadPdpProductRoleAssignment>(RoleAssignmentBucket,
            RoleAssignmentKey(query.Selector!, query.AuthenticatedSubject.SubjectId));
        if (assignment is null || !AssignmentMatches(assignment, first, query, time.GetUtcNow())) return null;

        // External central records can have another persistence boundary. Read them again
        // AFTER Fabric correlation, compare every stable field, and shorten to both leases.
        var last = Capture(query, cancellationToken);
        if (last is null || !HostMatches(last, query, time.GetUtcNow())
            || !Equivalent(first with { ValidUntil = last.ValidUntil }, last)
            || !ProjectionMatches(selected, last.Registration, time.GetUtcNow())
            || !AssignmentMatches(assignment, last, query, time.GetUtcNow())) return null;
        cancellationToken.ThrowIfCancellationRequested();
        var observedAt = time.GetUtcNow().ToUniversalTime();
        if (!RequestMatches(query, observedAt)) return null;
        var validUntil = new[] { request.ValidUntil, caller.ValidUntil, first.ValidUntil, last.ValidUntil,
            Instant(query.AuthenticationValidUntil), assignment.ValidTo, Instant(last.Membership.ValidTo),
            Instant(last.Grant.ValidTo), Instant(selected.ValidTo), observedAt.AddSeconds(HrReadDesign.MaximumWitnessSeconds) }.Min();
        if (validUntil <= observedAt) return null;
        // Provider also owns/correlates its copy and caps profile age. Return a detached
        // snapshot so neither the SQLite context nor host-owned lists escape this read.
        return ContractJson.Copy(new AccessAttributeSnapshotData(selected, last.Tenant, last.Membership,
            last.Grant, new EvidenceAttributes(Format(validUntil), selected.ContractVersion,
                selected.Policy.PolicyVersion), observedAt));
    }

    private HrReadPdpHostRecordSnapshot? Capture(TrustedAccessSnapshotQuery query, CancellationToken cancellationToken)
    {
        var value = records.ReadCurrent(database, ContractJson.Copy(query), caller, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        return value is null ? null : ContractJson.Copy(value);
    }

    private bool RequestMatches(TrustedAccessSnapshotQuery query, DateTimeOffset now)
    {
        var s = request.Binding;
        if (!s.Enabled || s.Validate().Count != 0 || now >= request.ValidUntil || now >= caller.ValidUntil
            || !caller.IsFor(request) || !caller.RevalidateCurrent() || caller.SubjectTokenSha256 != request.TokenSha256
            || caller.SubjectValidUntil.ToUnixTimeSeconds() != request.TokenExpiresAt
            || !ContractJson.IsCanonicalUtc(query.AuthenticationValidUntil)
            || Instant(query.AuthenticationValidUntil) > caller.ValidUntil || now >= Instant(query.AuthenticationValidUntil)
            || query.RequestId != request.RequestId || query.Action != request.Action || query.Zone != request.Zone
            || query.Selector is null || query.Selector != new ProjectionSelector(request.ConsumerTenantId,
                request.ProfileId, request.ProductId, request.ContractId)
            || query.Resource != new ResourceAttributes(request.ProductId, request.ConsumerTenantId, request.Environment, request.OperationGroup)
            || query.AuthenticatedSubject is not { AuthenticationValid: true, ClientAuthorized: true } subject
            || subject.SubjectId != request.SubjectId || subject.Audience != request.Audience
            || subject.Scopes is null || subject.Scopes.Count is 0 or > 64
            || subject.Scopes.Distinct(StringComparer.Ordinal).Count() != subject.Scopes.Count
            || !subject.Scopes.Contains(HrReadProfile.RequiredScope, StringComparer.Ordinal)) return false;
        var identities = caller.SubjectPrincipal.Identities.Where(x => x.IsAuthenticated).Take(2).ToArray();
        if (identities.Length != 1 || Single(identities[0], "sub") != request.SubjectId
            || Single(identities[0], "azp") != request.ClientId) return false;
        // Keep the exact raw scope claim in the authentication proof. The immutable
        // policy schema accepts only its identifier grammar, so the service derives
        // an ordered representable subset; required HR scope must still be present.
        return HrReadPdpService.AuthorizationScopes(caller).SequenceEqual(subject.Scopes, StringComparer.Ordinal);
    }

    private bool HostMatches(HrReadPdpHostRecordSnapshot host, TrustedAccessSnapshotQuery query, DateTimeOffset now)
    {
        var gateway = caller.CallerPrincipal.Identities.Where(x => x.IsAuthenticated).Take(2).ToArray();
        return gateway.Length == 1 && host.CallerSubjectId == Single(gateway[0], "sub")
            && host.CallerClientId == Single(gateway[0], "azp")
            && host.CallerDelegationAllowed && host.SubjectAuthenticated && host.UniqueConsumerMapping
            && host.CurrentClientAuthorized && host.NativePermissionAllowed && host.LegacyVetoClear
            && host.ValidUntil.Offset == TimeSpan.Zero && now < host.ValidUntil
            && Equivalent(host.Subject, query.AuthenticatedSubject) && host.Resource == query.Resource
            && request.Binding.MatchesCurrentRegistration(host.Registration)
            && host.Tenant is { Active: true } && host.Tenant.TenantId == request.ConsumerTenantId
            && host.Membership is { Active: true } && host.Membership.SubjectId == request.SubjectId
            && host.Membership.TenantId == request.ConsumerTenantId
            && CurrentWindow(host.Membership.ValidFrom, host.Membership.ValidTo, now)
            && host.Role is not null && host.Role.Id != Guid.Empty && host.Role.Key == HrReadProfile.Role
            && host.Role.Permissions is { Count: > 0 and <= 64 }
            && host.Role.Permissions.All(Id) && host.Role.Permissions.Distinct(StringComparer.Ordinal).Count() == host.Role.Permissions.Count
            && host.Role.Permissions.Contains(HrReadDesign.Permission, StringComparer.Ordinal)
            && host.Grant is { Active: true } && Id(host.Grant.GrantId)
            && host.Grant.SubjectId == request.SubjectId && host.Grant.ConsumerTenantId == request.ConsumerTenantId
            && host.Grant.ProductId == request.ProductId && host.Grant.Role == HrReadProfile.Role
            && host.Grant.Actions is { Count: > 0 and <= 2 } && host.Grant.Actions.All(HrReadProfile.IsAction)
            && host.Grant.Actions.Distinct(StringComparer.Ordinal).Count() == host.Grant.Actions.Count
            && host.Grant.Actions.Contains(query.Action, StringComparer.Ordinal)
            && CurrentWindow(host.Grant.ValidFrom, host.Grant.ValidTo, now);
    }

    private bool ProjectionMatches(ContractAccessProjection p, HrReadRegistration registration, DateTimeOffset now)
        => validator.Validate(p).Count == 0 && p.SchemaVersion == HrReadProfile.SchemaVersion
        && p.ProfileId == request.ProfileId && p.ContractId == request.ContractId && p.ContractVersion == request.ContractVersion
        && p.ConsumerTenantId == request.ConsumerTenantId && p.ProductId == request.ProductId
        && p.OwnerTenantId == request.OwnerTenantId && p.Ownership == request.Ownership
        && request.Binding.MatchesCurrentRegistration(registration)
        && p.Ownership?.RegisteredProductId == registration.Id.ToString("D")
        && p.Status == "Active" && CurrentWindow(p.ValidFrom, p.ValidTo, now) && Instant(p.EffectiveAt) <= now
        && p.FeatureEntitlements.Contains(request.Action, StringComparer.Ordinal)
        && p.EligibleRoles.Contains(HrReadProfile.Role, StringComparer.Ordinal)
        && p.ZoneConstraints.Contains(request.Zone, StringComparer.Ordinal)
        && p.Policy.PolicyId == request.PolicyId && p.Policy.PolicyVersion == request.PolicyVersion
        && p.Policy.TemplateId == HrReadProfile.TemplateId && p.Policy.CombiningAlgorithm == "deny-overrides"
        && p.Limits.AllocationId == request.AllocationId && p.Limits.ConfigRevision == request.ConfigRevision
        && p.Limits.CanonicalScope == request.AllocationScope
        && p.Limits.Scope.ConsumerTenantId == request.ConsumerTenantId
        && p.Limits.Scope.ProductIds.Contains(request.ProductId, StringComparer.Ordinal)
        && p.Limits.Scope.Environments.Contains(request.Environment, StringComparer.Ordinal)
        && p.Limits.Scope.OperationGroups.SequenceEqual(new[] { HrReadProfile.OperationGroup }, StringComparer.Ordinal);

    private static bool AssignmentMatches(HrReadPdpProductRoleAssignment a, HrReadPdpHostRecordSnapshot host,
        TrustedAccessSnapshotQuery query, DateTimeOffset now) => a.Active && a.TenantId.ToString("D") == query.Resource.ConsumerTenantId
        && a.Subject == query.AuthenticatedSubject.SubjectId && a.RoleId == host.Role.Id && a.Revision is >= 1 and <= MaximumSafeInteger
        && a.ValidFrom.Offset == TimeSpan.Zero && a.ValidTo.Offset == TimeSpan.Zero && a.ValidFrom <= now && now < a.ValidTo
        && Labels(a.Actor, a.Reason);

    private IEnumerable<ContractAccessProjection> ReadProjections(CancellationToken cancellationToken)
    {
        var result = new List<ContractAccessProjection>(); string? after = null;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = database.DocumentPage(ProjectionBucket, after, 100);
            if (page.Length == 0) break;
            if (result.Count > MaximumProjectionDocuments - page.Length) throw new InvalidDataException("Projection scan exceeds its technical bound.");
            foreach (var document in page)
            {
                var state = Parse<ProjectionState>(document);
                if (state.Projection is null || state.Projection.ContractId != document.Key || state.Receipts is null
                    || state.Receipts.Any(x => x is null || !Id(x.EventId) || !LowerHash(x.Fingerprint))
                    || state.Receipts.Select(x => x.EventId).Distinct(StringComparer.Ordinal).Count() != state.Receipts.Count)
                    throw new InvalidDataException("Invalid persisted projection state.");
                result.Add(state.Projection);
            }
            var next = page[^1].Key;
            if (after is not null && string.CompareOrdinal(next, after) <= 0) throw new InvalidDataException("Projection page did not advance.");
            after = next;
            if (page.Length < 100) break;
        }
        return result;
    }

    private T? Read<T>(string bucket, string key) where T : class
        => database.ReadDocument(bucket, key) is { } value ? Parse<T>(value) : null;
    private static T Parse<T>(FabricDocument document) where T : class
    {
        // Existing ReadDocument returns an allocated SQLite string. This bounds parsing
        // and detachment, not SQLite's initial allocation. No raw DB connection is opened.
        if (document.Json.Length is 0 or > MaximumSourceDocumentBytes || StrictUtf8.GetByteCount(document.Json) > MaximumSourceDocumentBytes)
            throw new InvalidDataException("Persisted source document exceeds its technical bound.");
        using var json = JsonDocument.Parse(document.Json, new JsonDocumentOptions { MaxDepth = 32 });
        RejectDuplicates(json.RootElement);
        return json.RootElement.Deserialize<T>(StoredJson) ?? throw new InvalidDataException("Missing source document.");
    }
    private static void RejectDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            { if (!names.Add(property.Name)) throw new InvalidDataException("Duplicate persisted source field."); RejectDuplicates(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) RejectDuplicates(item);
    }
    private static bool Equivalent<T>(T a, T b) => JsonSerializer.Serialize(a, ContractJson.Options) == JsonSerializer.Serialize(b, ContractJson.Options);
    private static string? Single(ClaimsIdentity identity, string type)
    { var items = identity.FindAll(type).Take(2).ToArray(); return items.Length == 1 ? items[0].Value : null; }
    private static bool CanonicalGuid(string? value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty && id.ToString("D") == value;
    private static bool Id(string? value) => value is { Length: > 0 and <= 64 } && value[0] is >= 'a' and <= 'z'
        && value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-');
    private static bool LowerHash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static bool Labels(string? actor, string? reason) => !string.IsNullOrWhiteSpace(actor) && actor.Length <= 200
        && !actor.Any(char.IsControl) && !string.IsNullOrWhiteSpace(reason) && reason.Length <= 1000;
    private static bool CurrentWindow(string from, string to, DateTimeOffset now) => ContractJson.IsCanonicalUtc(from)
        && ContractJson.IsCanonicalUtc(to) && Instant(from) <= now && now < Instant(to);
    private static DateTimeOffset Instant(string value) => DateTimeOffset.ParseExact(value, "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
