using System.Text.Json;
using System.Text.Json.Nodes;
using static Fanasa.ContractPolicy.ContractProjectionValidator;
using static Fanasa.ContractPolicy.ContractPolicyEvaluator;

namespace Fanasa.ContractPolicy;

/// <summary>Checks a trusted decision and simulated completion IDs; does not execute obligations.</summary>
public sealed class ContractPolicyPep(ContractSchemaValidator schemas)
{
    public bool CanExecute(PolicyDecision? decision, IReadOnlyCollection<string>? fulfilledIds, string? now)
    {
        try { return CanExecuteJson(ContractJson.ToNode(decision), fulfilledIds, now); }
        catch (Exception e) when (e is JsonException or ArgumentException or NotSupportedException) { return false; }
    }

    public bool CanExecuteJson(JsonNode? decision, IReadOnlyCollection<string>? fulfilledIds, string? now)
    {
        if (!ContractJson.TrySnapshot(decision, out decision)) return false;
        var version = VersionOf(decision);
        if (version == 0) return false;
        if (schemas.Validate(Schema("decision", version), decision).Count != 0 || fulfilledIds is null || fulfilledIds.Any(x => x is null) || !ContractJson.IsCanonicalUtc(now)) return false;
        var d = decision!.Deserialize<PolicyDecision>(ContractJson.Options)!;
        if (d.Decision != "Permit" || d.EvaluatedAt is null || d.ValidUntil is null) return false;
        var instant = Instant(now!); var evaluated = Instant(d.EvaluatedAt); var deadline = Instant(d.ValidUntil);
        if (instant < evaluated || instant >= deadline || deadline - evaluated > TimeSpan.FromSeconds(FreshnessSeconds(version))) return false;
        if (d.Obligations.Count != 2 || d.Obligations.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != 2) return false;
        var admission = d.Obligations.SingleOrDefault(x => x.Id == AdmissionObligation)?.Parameters.Deserialize<AllocationLimits>(ContractJson.Options);
        var resource = d.Obligations.SingleOrDefault(x => x.Id == ResourceObligation)?.Parameters.Deserialize<ResourceTenantRequirement>(ContractJson.Options);
        if (admission is null || resource is null || d.AllocationReference is null) return false;
        if (CanonicalScope(admission.Scope) != admission.CanonicalScope || admission.AllocationId != d.AllocationReference.AllocationId || admission.CanonicalScope != d.AllocationReference.CanonicalScope) return false;
        if (!SupportsAction(version, resource.Action) || resource.ConsumerTenantId != admission.Scope.ConsumerTenantId || !admission.Scope.ProductIds.Contains(resource.ProductId, StringComparer.Ordinal) || !admission.Scope.Environments.Contains(resource.Environment, StringComparer.Ordinal)) return false;
        if (version == 3 && (resource.ProductId != HrReadProfile.ProductId
            || admission.Scope.OperationGroups.Count != 1
            || admission.Scope.OperationGroups[0] != HrReadProfile.OperationGroup)) return false;
        return d.Obligations.All(x => fulfilledIds.Contains(x.Id, StringComparer.Ordinal));
    }
}
