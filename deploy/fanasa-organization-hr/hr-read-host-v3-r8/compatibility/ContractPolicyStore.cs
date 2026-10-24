using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Persistence;
using Fanasa.AccessManagement.Web.Platform;
using Fanasa.ContractPolicy;

namespace Fanasa.AccessManagement.Web.ContractPolicies;

public sealed record PublishedContractPolicy(Guid ConsumerTenantId, ContractPolicyBinding Binding,
    bool Active, long Revision, string Actor, string Reason, DateTimeOffset RecordedAt);

public sealed record ProductRoleAssignment(Guid TenantId, string Subject, Guid RoleId, bool Active,
    DateTimeOffset ValidFrom, DateTimeOffset ValidTo, long Revision, string Actor, string Reason);

/// <summary>
/// Host integration over the existing Fabric database and central registry.
/// Administrative writes are invoked only by the host's authorized boundary;
/// policy publication and role assignment never create a direct grant.
/// </summary>
public sealed partial class ContractPolicyStore(FabricDatabase database, PersistentAccessManagement access,
    PlatformRegistry registry, ContractPolicyModule module, TimeProvider time)
{
    public const string ProductKey = "organization-fabric";
    public const string ProductAudience = "fanasa-organization-fabric-api";
    public const string Operation = "organization.unit.save";
    private const string ProjectionKind = "contract-policy.projection";
    private const string AllocationKind = "contract-policy.allocation";
    private const string PolicyKind = "contract-policy.policy";
    private const string PolicyVersionKind = "contract-policy.policy-version";
    private const string AssignmentKind = "contract-policy.role-assignment";
    private const long MaximumSafeInteger = 9_007_199_254_740_991;
    private static readonly Regex Id = new("\\A[a-z][a-z0-9.-]{0,63}\\z", RegexOptions.CultureInvariant);
    private static readonly Regex Identity = new(
        "\\A(?:[a-z][a-z0-9.-]{0,63}|[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})\\z",
        RegexOptions.CultureInvariant);
    private static readonly Regex Hash = new("\\A[0-9a-f]{64}\\z", RegexOptions.CultureInvariant);

    public ProjectionResult Apply(string? payload, ClaimsPrincipal principal,
        ContractProducerBinding binding, Func<bool> authorize)
    {
        ArgumentNullException.ThrowIfNull(authorize);
        if (!authorize()) return Rejected(null, "producer-not-authorized");
        var adapter = new ContractEventAdapter(module.Projector, binding);
        // Preflight authenticates before parsing, without reading or writing state.
        // It also applies the native payload limit and complete event validation.
        var preflight = adapter.Handle(null, payload, principal);
        if (preflight.Status != "applied" || preflight.State is null) return preflight;
        var incoming = preflight.State.Projection;
        if (incoming.SchemaVersion != "2.0.0" || incoming.ProductId != ProductKey)
            return Rejected(null, "unsupported-host-profile");
        var acceptedEvent = ContractJson.Parse(payload!)!.Deserialize<ContractProjectionEvent>(ContractJson.Options)!;

        try
        {
            return database.Transaction(() =>
            {
                var previous = Read(incoming.ContractId);
                if (!authorize()) return Rejected(previous, "producer-not-authorized");
                var result = adapter.Handle(previous, payload, principal);
                if (result.Status is "rejected" or "conflict" || result.State is null) return result;
                if (!KnownTenantsAndProduct(incoming))
                    return Rejected(previous, "unknown-product-or-tenant-binding");

                if (result.Status == "applied")
                {
                    var allocation = ReadAllocation(incoming.Limits.AllocationId);
                    if (allocation is not null)
                    {
                        if (allocation.CanonicalScope != incoming.Limits.CanonicalScope
                            || allocation.Scope.ConsumerTenantId != incoming.ConsumerTenantId)
                            return Rejected(previous, "allocation-scope-conflict");
                        if (incoming.Limits.ConfigRevision < allocation.ConfigRevision)
                            return Rejected(previous, "stale-allocation-config-revision");
                        if (incoming.Limits.ConfigRevision == allocation.ConfigRevision
                            && !Equivalent(allocation, incoming.Limits))
                            return Rejected(previous, "allocation-config-revision-conflict");
                    }
                    database.Put(AllocationKind, incoming.Limits.AllocationId, ContractJson.Copy(incoming.Limits));
                }

                // Duplicate snapshots with a fresh event ID and stale snapshots
                // still add receipts; the full current projection stays intact.
                database.Put(ProjectionKind, incoming.ContractId, ContractJson.Copy(result.State));
                // Capture the accepted input now; a delayed publisher must never
                // substitute the latest contract for an earlier outbox event.
                if (result.Status is "applied" or "duplicate"
                    && Equivalent(incoming, result.State.Projection))
                    CaptureGatewayPublication(acceptedEvent, binding.ClientId,
                        preflight.State.Receipts[0].Fingerprint);
                if (result.Status == "applied")
                {
                    var now = time.GetUtcNow().ToUniversalTime();
                    var eventId = preflight.State.Receipts[0].EventId;
                    var data = new
                    {
                        TenantId = Guid.Parse(incoming.ConsumerTenantId), incoming.ContractId,
                        incoming.ContractVersion, incoming.Status, incoming.OwnerTenantId,
                        incoming.Limits.AllocationId, incoming.Limits.ConfigRevision,
                        Producer = ContractEventAdapter.AuthorizedProducer, ClientId = binding.ClientId
                    };
                    database.Emit($"contract-policy:{incoming.ContractId}:{eventId}",
                        "ContractAccessProjectionApplied", data, now);
                    database.Emit($"contract-policy-audit:{incoming.ContractId}:{eventId}",
                        "ContractPolicyAudit", data, now);
                }
                // A late revocation must roll back projection, allocation, inbox
                // receipts and outbox together, including nested Fabric savepoints.
                if (!authorize()) throw new AuthorizationChanged(previous);
                return result;
            });
        }
        catch (AuthorizationChanged error)
        { return Rejected(error.Previous, "producer-not-authorized"); }
    }

    public ProjectionState? Read(string contractId)
    {
        RequireId(contractId, nameof(contractId));
        var value = database.Read<ProjectionState>(ProjectionKind, contractId);
        if (value is null) return null;
        ValidateStoredState(value);
        if (value.Projection.ContractId != contractId) throw new InvalidDataException("Projection key mismatch.");
        return ContractJson.Copy(value);
    }

    public ContractAccessProjection? Find(Guid consumer)
    {
        if (consumer == Guid.Empty) return null;
        var states = database.All<ProjectionState>(ProjectionKind);
        foreach (var state in states) ValidateStoredState(state, allowHrRead: true);
        var matches = states.Where(x => x.Projection.SchemaVersion == "2.0.0"
            && x.Projection.ProfileId == "fanasa.contract-policy/2"
            && Guid.TryParseExact(x.Projection.ConsumerTenantId, "D", out var id)
            && id == consumer).Take(2).ToArray();
        return matches.Length == 1 ? ContractJson.Copy(matches[0].Projection) : null;
    }

    public AllocationLimits? ReadAllocation(string allocationId)
    {
        RequireId(allocationId, nameof(allocationId));
        var limits = database.Read<AllocationLimits>(AllocationKind, allocationId);
        if (limits is null) return null;
        if (limits.AllocationId != allocationId || limits.ConfigRevision is null
            || module.Schemas.Validate("limits.v2.schema.json", ContractJson.ToNode(limits)).Count != 0
            || ContractProjectionValidator.CanonicalScope(limits.Scope) != limits.CanonicalScope)
            throw new InvalidDataException("Invalid stored allocation.");
        return ContractJson.Copy(limits);
    }

    public PublishedContractPolicy? ReadPolicy(Guid consumer)
    {
        try
        {
            var value = database.Read<PublishedContractPolicy>(PolicyKind, consumer.ToString("D"));
            if (value is null) return null;
            if (value.ConsumerTenantId != consumer || value.Revision is < 1 or > MaximumSafeInteger)
                throw new InvalidDataException("Published policy key or revision mismatch.");
            var binding = CopyBinding(value.Binding);
            Labels(value.Actor, value.Reason);
            var immutable = database.Read<ContractPolicyBinding>(PolicyVersionKind,
                $"{binding.PolicyId}:{binding.PolicyVersion}");
            if (immutable is null || !Equivalent(binding, CopyBinding(immutable)))
                throw new InvalidDataException("Published policy does not match its immutable version.");
            return ContractJson.Copy(value);
        }
        catch (Exception error) when (error is ArgumentException or JsonException or
                                      InvalidOperationException or NotSupportedException)
        { throw new InvalidDataException("Invalid stored published policy.", error); }
    }

    public PublishedContractPolicy PublishPolicy(Guid consumer, ContractPolicyBinding binding,
        long expectedRevision, string actor, string reason)
    {
        var owned = CopyBinding(binding);
        Labels(actor, reason);
        return database.Transaction(() =>
        {
            RequireConsumerAndProduct(consumer);
            var previous = ReadPolicy(consumer);
            Revision(previous?.Revision ?? 0, expectedRevision);
            if (previous is not null && (previous.Binding.PolicyId != owned.PolicyId
                || owned.PolicyVersion <= previous.Binding.PolicyVersion))
                throw new InvalidOperationException("Publication requires the same policy ID and a newer policy version.");
            var versionKey = $"{owned.PolicyId}:{owned.PolicyVersion}";
            var immutable = database.Read<ContractPolicyBinding>(PolicyVersionKind, versionKey);
            if (immutable is not null && !Equivalent(immutable, owned))
                throw new InvalidOperationException("A policy ID/version already identifies different content.");
            var value = new PublishedContractPolicy(consumer, owned, true, Next(expectedRevision),
                actor.Trim(), reason.Trim(), time.GetUtcNow().ToUniversalTime());
            database.Put(PolicyVersionKind, versionKey, owned);
            database.Put(PolicyKind, consumer.ToString("D"), value);
            Audit(consumer, "ContractPolicyPublished", value.Revision, value, value.RecordedAt);
            return ContractJson.Copy(value);
        });
    }

    public PublishedContractPolicy RetirePolicy(Guid consumer, long expectedRevision,
        string actor, string reason)
    {
        Labels(actor, reason);
        return database.Transaction(() =>
        {
            RequireConsumerAndProduct(consumer);
            var previous = ReadPolicy(consumer) ?? throw new KeyNotFoundException("Published policy not found.");
            Revision(previous.Revision, expectedRevision);
            if (!previous.Active) throw new InvalidOperationException("Policy is already retired.");
            var value = previous with { Active = false, Revision = Next(expectedRevision),
                Actor = actor.Trim(), Reason = reason.Trim(), RecordedAt = time.GetUtcNow().ToUniversalTime() };
            database.Put(PolicyKind, consumer.ToString("D"), value);
            Audit(consumer, "ContractPolicyRetired", value.Revision, value, value.RecordedAt);
            return ContractJson.Copy(value);
        });
    }

    public ProductRoleAssignment? ReadRoleAssignment(Guid tenant, string subject)
    {
        RequireIdentity(subject);
        try
        {
            var value = database.Read<ProductRoleAssignment>(AssignmentKind, AssignmentKey(tenant, subject));
            if (value is null) return null;
            if (value.TenantId != tenant || value.Subject != subject || value.RoleId == Guid.Empty
                || value.Revision is < 1 or > MaximumSafeInteger || value.ValidFrom >= value.ValidTo)
                throw new InvalidDataException("Role assignment key, version or validity mismatch.");
            Labels(value.Actor, value.Reason);
            return ContractJson.Copy(value);
        }
        catch (Exception error) when (error is ArgumentException or JsonException or
                                      InvalidOperationException or NotSupportedException)
        { throw new InvalidDataException("Invalid stored product role assignment.", error); }
    }

    public ProductRoleAssignment SaveRoleAssignment(Guid tenant, string subject, Guid roleId,
        bool active, DateTimeOffset validFrom, DateTimeOffset validTo, long expectedRevision,
        string actor, string reason)
    {
        RequireIdentity(subject);
        Labels(actor, reason);
        validFrom = validFrom.ToUniversalTime(); validTo = validTo.ToUniversalTime();
        return database.Transaction(() =>
        {
            var previous = ReadRoleAssignment(tenant, subject);
            Revision(previous?.Revision ?? 0, expectedRevision);
            if (active)
            {
                RequireConsumerAndProduct(tenant);
                if (validFrom >= validTo) throw new ArgumentException("Role assignment requires an increasing validity window.");
                if (access.GetUsers(tenant).Count(x => x.KeycloakSubject == subject && x.IsActive) != 1)
                    throw new ArgumentException("Role assignment requires one current active tenant member.");
                var role = access.GetRoles(tenant, ProductKey).SingleOrDefault(x => x.Id == roleId);
                if (role is null || role.Key != "ApiInvoker"
                    || !role.Permissions.Contains("organization.write", StringComparer.Ordinal))
                    throw new ArgumentException("An explicit organization product ApiInvoker role is required.");
            }
            else
            {
                // Revocation remains available after offboarding or removal of
                // the role definition. It preserves the existing binding/window.
                if (previous is null || previous.RoleId != roleId)
                    throw new InvalidOperationException("Revocation must identify an existing role assignment.");
                validFrom = previous.ValidFrom; validTo = previous.ValidTo;
            }
            var value = new ProductRoleAssignment(tenant, subject, roleId, active, validFrom, validTo,
                Next(expectedRevision), actor.Trim(), reason.Trim());
            database.Put(AssignmentKind, AssignmentKey(tenant, subject), value);
            Audit(tenant, "ProductRoleAssignmentChanged", value.Revision, value, time.GetUtcNow().ToUniversalTime());
            return ContractJson.Copy(value);
        });
    }

    private bool KnownTenantsAndProduct(ContractAccessProjection projection) =>
        Guid.TryParseExact(projection.OwnerTenantId, "D", out var owner) && owner != Guid.Empty
        && Guid.TryParseExact(projection.ConsumerTenantId, "D", out var consumer) && consumer != Guid.Empty
        && access.GetTenants().Count(x => x.Id == owner) == 1
        && access.GetTenants().Count(x => x.Id == consumer) == 1
        && registry.Products().Count(x => x.Key == ProductKey && x.Audience == ProductAudience
            && x.TenantId == owner) == 1;

    private void RequireConsumerAndProduct(Guid consumer)
    {
        if (consumer == Guid.Empty || access.GetTenants().Count(x => x.Id == consumer) != 1)
            throw new ArgumentException("Consumer tenant must exist.");
        var product = registry.Products().SingleOrDefault(x => x.Key == ProductKey && x.Audience == ProductAudience);
        if (product?.TenantId is not { } owner || access.GetTenants().Count(x => x.Id == owner) != 1)
            throw new ArgumentException("Registered organization product and owner tenant are required.");
    }

    private void ValidateStoredState(ProjectionState value, bool allowHrRead = false)
    {
        if (value.Projection is null || (value.Projection.SchemaVersion != "2.0.0"
                && !(allowHrRead && value.Projection.SchemaVersion == HrReadProfile.SchemaVersion
                    && value.Projection.ProfileId == HrReadProfile.ProfileId))
            || value.Projection.ProductId != ProductKey || module.Projections.Validate(value.Projection).Count != 0
            || value.Receipts is null || value.Receipts.Any(x => x is null || x.EventId is null
                || !Id.IsMatch(x.EventId) || x.Fingerprint is null || !Hash.IsMatch(x.Fingerprint))
            || value.Receipts.Select(x => x.EventId).Distinct(StringComparer.Ordinal).Count() != value.Receipts.Count)
            throw new InvalidDataException("Invalid stored contract projection state.");
    }

    private static ContractPolicyBinding CopyBinding(ContractPolicyBinding? binding)
    {
        if (binding is null || binding.PolicyId is null || !Id.IsMatch(binding.PolicyId)
            || binding.PolicyVersion is < 1 or > MaximumSafeInteger
            || binding.TemplateId != "organization-unit-save/1" || binding.CombiningAlgorithm != "deny-overrides"
            || binding.ExplicitDenyActions is null || binding.ExplicitDenyActions.Count > 64
            || binding.ExplicitDenyActions.Any(x => x != Operation)
            || binding.ExplicitDenyActions.Distinct(StringComparer.Ordinal).Count() != binding.ExplicitDenyActions.Count)
            throw new ArgumentException("Published policy must use the bounded organization-unit-save profile.", nameof(binding));
        return ContractJson.Copy(binding);
    }

    private void Audit(Guid tenant, string operation, long revision, object value, DateTimeOffset now) =>
        database.Emit("contract-policy-admin:" + Guid.NewGuid().ToString("N"), operation,
            new { TenantId = tenant, Revision = revision, Value = value }, now);

    private static string AssignmentKey(Guid tenant, string subject) => $"{tenant:D}:{subject}";
    private static bool Equivalent<T>(T left, T right) =>
        JsonSerializer.Serialize(left, ContractJson.Options) == JsonSerializer.Serialize(right, ContractJson.Options);
    private static T? CopyOrNull<T>(T? value) where T : class => value is null ? null : ContractJson.Copy(value);
    private static void RequireId(string? value, string parameter)
    { if (value is null || !Id.IsMatch(value)) throw new ArgumentException("A canonical identifier is required.", parameter); }
    private static void RequireIdentity(string? subject)
    { if (subject is null || !Identity.IsMatch(subject)) throw new ArgumentException("A canonical subject is required.", nameof(subject)); }
    private static void Labels(string? actor, string? reason)
    {
        if (string.IsNullOrWhiteSpace(actor) || actor.Length > 200 || actor.Any(char.IsControl)
            || string.IsNullOrWhiteSpace(reason) || reason.Length > 1000)
            throw new ArgumentException("An actor and bounded audit reason are required.");
    }
    private static void Revision(long actual, long expected)
    {
        if (expected < 0 || expected >= MaximumSafeInteger) throw new ArgumentOutOfRangeException(nameof(expected));
        if (actual != expected) throw new InvalidOperationException("Revision changed; reload before saving.");
    }
    private static long Next(long revision) => checked(revision + 1);
    private static ProjectionResult Rejected(ProjectionState? state, string error) =>
        new("rejected", CopyOrNull(state), Array.AsReadOnly(new[] { error }));
    private sealed class AuthorizationChanged(ProjectionState? previous) : Exception
    { public ProjectionState? Previous { get; } = previous; }
}
