using System.Text.Json;
using System.Text.Json.Nodes;
using static Fanasa.ContractPolicy.ContractProjectionValidator;

namespace Fanasa.ContractPolicy;

/// <summary>Deterministic policy evaluation. PIP input must be trusted by the consuming host.</summary>
public sealed class ContractPolicyEvaluator(ContractProjectionValidator validator) : IContractPolicyDecisionEvaluator
{
    public const string AdmissionObligation = "urn:fanasa:obligation:admit-allocation:1";
    public const string ResourceObligation = "urn:fanasa:obligation:resource-tenant:1";

    public PolicyDecision Evaluate(ContractAccessProjection? projection, AccessEvaluationContext? context)
    {
        static JsonNode? Node<T>(T value)
        {
            try { return ContractJson.ToNode(value); }
            catch (Exception e) when (e is JsonException or ArgumentException or NotSupportedException) { return null; }
        }
        return EvaluateJson(Node(projection), Node(context));
    }

    public PolicyDecision EvaluateJson(JsonNode? projection, JsonNode? context)
    {
        // External JsonNode parsers may retain duplicate properties lazily.
        // Own a strict snapshot before reading attributes or selecting a schema.
        ContractJson.TrySnapshot(projection, out projection);
        ContractJson.TrySnapshot(context, out context);
        var version = VersionOf(projection);
        var validProjection = validator.ValidateJson(projection).Count == 0;
        var p = validProjection ? projection!.Deserialize<ContractAccessProjection>(ContractJson.Options)! : null;
        var time = Text(Property(Property(context, "environment"), "now"));
        var requestId = Text(Property(context, "requestId"));
        var result = new PolicyDecision
        {
            // An unsupported input is rejected before any profile evaluation.
            // Preserve the existing v1 error-response envelope for malformed input;
            // this never selects or validates an unknown projection as v1.
            SchemaVersion = version switch { 3 => "3.0.0", 2 => "2.0.0", _ => "1.0.0" },
            ProfileId = ProfileFor(version == 0 ? 1 : version),
            RequestId = requestId is not null && Id.IsMatch(requestId) ? requestId : "unknown",
            Decision = "Indeterminate", ReasonCode = "invalid-projection",
            EvaluatedAt = ContractJson.IsCanonicalUtc(time) ? time : null, ValidUntil = null,
            PolicyReferences = p is null ? [] : [new(p.Policy.PolicyId, p.Policy.PolicyVersion)],
            ProjectionReference = p is null ? null : new(p.ContractId, p.ContractVersion, p.SubscriptionId),
            AllocationReference = p is null ? null : new(p.Limits.AllocationId, p.Limits.CanonicalScope),
            Obligations = []
        };
        PolicyDecision Finish(string decision, string reason) => result with { Decision = decision, ReasonCode = reason };
        if (p is null) return result;
        var resource = Property(context, "resource");
        var product = Text(Property(resource, "productId"));
        if (product is null || !Id.IsMatch(product)) return Finish("Indeterminate", "missing-attribute");
        if (product != p.ProductId) return Finish("NotApplicable", "target-mismatch");
        var action = Text(Property(context, "action"));
        if (action is not null && p.Policy.ExplicitDenyActions.Contains(action, StringComparer.Ordinal)) return Finish("Deny", "explicit-deny");
        if (validator.Schemas.Validate(Schema("request-context", version), context).Count != 0) return Finish("Indeterminate", "missing-or-invalid-attribute");
        var c = context!.Deserialize<AccessEvaluationContext>(ContractJson.Options)!;
        var now = Instant(c.Environment.Now);
        bool Window(string from, string to) => Instant(from) <= now && now < Instant(to);
        var gates = new (bool Pass, string Reason)[]
        {
            (c.Subject.AuthenticationValid && c.Subject.ClientAuthorized && c.Subject.Audience == AudienceFor(version) && c.Subject.Scopes.Contains(ScopeFor(version, c.Action), StringComparer.Ordinal), "identity-denied"),
            (c.Resource.ConsumerTenantId == p.ConsumerTenantId && c.Tenant.TenantId == p.ConsumerTenantId && c.Tenant.Active, "tenant-denied"),
            (c.Membership.Active && c.Membership.SubjectId == c.Subject.SubjectId && c.Membership.TenantId == p.ConsumerTenantId && Window(c.Membership.ValidFrom, c.Membership.ValidTo), "membership-denied"),
            (p.Status == "Active", "contract-inactive"),
            (Instant(p.EffectiveAt) <= now && Window(p.ValidFrom, p.ValidTo), "contract-time-denied"),
            (SupportsAction(version, c.Action) && p.FeatureEntitlements.Contains(c.Action, StringComparer.Ordinal), "action-denied"),
            (c.Grant.Active && c.Grant.SubjectId == c.Subject.SubjectId && c.Grant.ConsumerTenantId == p.ConsumerTenantId && c.Grant.ProductId == p.ProductId && c.Grant.Actions.Contains(c.Action, StringComparer.Ordinal) && p.EligibleRoles.Contains(c.Grant.Role, StringComparer.Ordinal) && Window(c.Grant.ValidFrom, c.Grant.ValidTo), "grant-denied"),
            (p.Limits.Scope.Environments.Contains(c.Resource.Environment, StringComparer.Ordinal) && p.Limits.Scope.OperationGroups.Contains(c.Resource.OperationGroup, StringComparer.Ordinal)
                && (version != 3 || c.Resource.OperationGroup == HrReadProfile.OperationGroup), "scope-denied"),
            (p.ZoneConstraints.Contains(c.Environment.Zone, StringComparer.Ordinal), "zone-denied")
        };
        foreach (var gate in gates) if (!gate.Pass) return Finish("Deny", gate.Reason);
        if (c.Evidence.ContractVersion != p.ContractVersion || c.Evidence.PolicyVersion != p.Policy.PolicyVersion || Instant(c.Evidence.ValidUntil) <= now)
            return Finish("Indeterminate", "stale-evidence");
        var freshness = TimeSpan.FromSeconds(FreshnessSeconds(version));
        var ceiling = now.Ticks > DateTimeOffset.MaxValue.Ticks - freshness.Ticks ? DateTimeOffset.MaxValue : now.Add(freshness);
        var deadline = new[] { Instant(p.ValidTo), Instant(c.Membership.ValidTo), Instant(c.Grant.ValidTo), Instant(c.Evidence.ValidUntil), ceiling }.Min();
        return Finish("Permit", "all-conditions-satisfied") with
        {
            ValidUntil = Format(deadline),
            Obligations = [
                new(AdmissionObligation, "Permit", (JsonObject)ContractJson.ToNode(p.Limits)!),
                new(ResourceObligation, "Permit", (JsonObject)ContractJson.ToNode(new ResourceTenantRequirement(p.ConsumerTenantId, p.ProductId, c.Resource.Environment, c.Action))!)
            ]
        };
    }
}
