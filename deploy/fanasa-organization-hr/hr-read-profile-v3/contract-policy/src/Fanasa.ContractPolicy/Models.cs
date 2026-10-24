using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Fanasa.ContractPolicy;

public sealed record ContractAccessProjection
{
    public required string SchemaVersion { get; init; }
    public required string ProfileId { get; init; }
    public required string ContractId { get; init; }
    public required long ContractVersion { get; init; }
    public required string? OwnerTenantId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ProductOwnershipReference? Ownership { get; init; }
    public required string ConsumerTenantId { get; init; }
    public required string ProductId { get; init; }
    public required string OfferId { get; init; }
    public required string SubscriptionId { get; init; }
    public required string Status { get; init; }
    public required string ValidFrom { get; init; }
    public required string ValidTo { get; init; }
    public required string EffectiveAt { get; init; }
    public required IReadOnlyList<string> FeatureEntitlements { get; init; }
    public required IReadOnlyList<string> EligibleRoles { get; init; }
    public required SeatLimit Seats { get; init; }
    public required AllocationLimits Limits { get; init; }
    public required IReadOnlyList<string> ZoneConstraints { get; init; }
    public required PricingReference PricingReference { get; init; }
    public required ContractPolicyBinding Policy { get; init; }
}

public sealed record SeatLimit(long Maximum, string Unit, string Scope);
/// <summary>Registry ownership reference; not a competing registry or tenant identifier.</summary>
public sealed record ProductOwnershipReference(string Kind, string Owner, string RegisteredProductId);
public sealed record PricingReference(string PriceId, long PriceVersion);
public sealed record ContractPolicyBinding(string PolicyId, long PolicyVersion, string TemplateId,
    string CombiningAlgorithm, IReadOnlyList<string> ExplicitDenyActions);
public sealed record AllocationScope(string ConsumerTenantId, IReadOnlyList<string> ProductIds,
    IReadOnlyList<string> Environments, IReadOnlyList<string> OperationGroups);
public sealed record RateLimit(string Algorithm, double RequestsPerSecond, long BurstCapacity);
public sealed record QuotaLimit(string Algorithm, string Period, long Requests);
public sealed record AllocationLimits
{
    public required string AllocationId { get; init; }
    // Absent in v1. Required positive independent revision in v2.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ConfigRevision { get; init; }
    public required AllocationScope Scope { get; init; }
    public required string CanonicalScope { get; init; }
    public required string Meter { get; init; }
    public required string Unit { get; init; }
    public required RateLimit Rate { get; init; }
    public required QuotaLimit Quota { get; init; }
}

public sealed record AccessEvaluationContext
{
    public required string RequestId { get; init; }
    public required SubjectAttributes Subject { get; init; }
    public required ResourceAttributes Resource { get; init; }
    public required string Action { get; init; }
    public required EnvironmentAttributes Environment { get; init; }
    public required TenantAttributes Tenant { get; init; }
    public required MembershipAttributes Membership { get; init; }
    public required GrantAttributes Grant { get; init; }
    public required EvidenceAttributes Evidence { get; init; }
}
public sealed record SubjectAttributes(string SubjectId, bool AuthenticationValid, bool ClientAuthorized,
    string Audience, IReadOnlyList<string> Scopes);
public sealed record ResourceAttributes(string ProductId, string ConsumerTenantId, string Environment, string OperationGroup);
public sealed record EnvironmentAttributes(string Now, string Zone);
public sealed record TenantAttributes(string TenantId, bool Active);
public sealed record MembershipAttributes(string SubjectId, string TenantId, bool Active, string ValidFrom, string ValidTo);
public sealed record GrantAttributes(string GrantId, string SubjectId, string ConsumerTenantId, string ProductId,
    string Role, IReadOnlyList<string> Actions, bool Active, string ValidFrom, string ValidTo);
public sealed record EvidenceAttributes(string ValidUntil, long ContractVersion, long PolicyVersion);

public sealed record PolicyReference(string PolicyId, long PolicyVersion);
public sealed record ProjectionReference(string ContractId, long ContractVersion, string SubscriptionId);
public sealed record AllocationReference(string AllocationId, string CanonicalScope);
public sealed record DecisionObligation(string Id, string FulfillOn, JsonObject Parameters);
public sealed record ResourceTenantRequirement(string ConsumerTenantId, string ProductId, string Environment, string Action);
public sealed record PolicyDecision
{
    public required string SchemaVersion { get; init; }
    public required string ProfileId { get; init; }
    public required string RequestId { get; init; }
    public required string Decision { get; init; }
    public required string ReasonCode { get; init; }
    public required string? EvaluatedAt { get; init; }
    public required string? ValidUntil { get; init; }
    public required IReadOnlyList<PolicyReference> PolicyReferences { get; init; }
    public required ProjectionReference? ProjectionReference { get; init; }
    public required AllocationReference? AllocationReference { get; init; }
    public required IReadOnlyList<DecisionObligation> Obligations { get; init; }
}
public sealed record ContractProjectionEvent
{
    public required string SchemaVersion { get; init; }
    public required string EventId { get; init; }
    public required string Producer { get; init; }
    public required long AggregateVersion { get; init; }
    public required string OccurredAt { get; init; }
    public required string CorrelationId { get; init; }
    public required ContractAccessProjection Projection { get; init; }
}
public sealed record EventReceipt(string EventId, string Fingerprint);
public sealed record ProjectionState(ContractAccessProjection Projection, IReadOnlyList<EventReceipt> Receipts);
public sealed record ProjectionResult(string Status, ProjectionState? State, IReadOnlyList<string> Errors);

public interface IContractPolicyDecisionEvaluator
{
    PolicyDecision Evaluate(ContractAccessProjection? projection, AccessEvaluationContext? context);
}
public interface IContractProjectionProjector
{
    ProjectionResult Apply(ProjectionState? state, ContractProjectionEvent? projectionEvent, string? authenticatedProducer);
}
