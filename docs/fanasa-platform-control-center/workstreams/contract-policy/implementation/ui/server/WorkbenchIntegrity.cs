using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fanasa.ContractPolicy;
using Microsoft.Data.Sqlite;

namespace Fanasa.ContractPolicy.Workbench;

public sealed partial class WorkbenchStore
{
    private string? _validatedDefinitionFingerprint;
    private string? _previousValidatedDefinitionFingerprint;
    private static readonly string[] IntegrityChecks =
    [
        "sqlite-integrity-check", "foreign-key-check", "normalized-column-agreement", "durable-state-checksum", "native-projection-schema",
        "trusted-context-schema-and-source-references", "policy-lifecycle-and-binding", "decision-native-replay",
        "resource-ownership-and-save-count", "atomic-operation-receipt-usage", "revision-and-audit-coherence"
    ];

    private AccessEvaluationContext TrustedContextFor(ContractAccessProjection projection) => Copy(_contextFixture) with
    {
        Membership = _contextFixture.Membership with { ValidFrom = projection.ValidFrom, ValidTo = projection.ValidTo },
        Grant = _contextFixture.Grant with { ValidFrom = projection.ValidFrom, ValidTo = projection.ValidTo }
    };

    public WorkbenchStorageHealth Integrity()
    {
        lock (_gate)
        {
            try
            {
                var snapshot = _database.Read();
                ValidateDefinitions(snapshot.State);
                var s = snapshot.State;
                return new(true, "SQLite", WorkbenchDatabase.SchemaVersion, s.Revision, Time(DateTimeOffset.UtcNow),
                    snapshot.LegacyImported, [], IntegrityChecks,
                    new { policies = s.Policies.Count, drafts = s.Drafts.Count, decisions = s.Decisions.Count,
                        resources = s.Resources.Count, operations = s.Operations.Count, receipts = s.Executions.Count,
                        auditEntries = s.History.Count, admittedInStoredPeriod = s.Admitted, storedPeriod = s.PeriodId,
                        trustedSources = 4, mutationVersion = snapshot.MutationVersion });
            }
            catch (Exception error) when (StorageFailure(error))
            {
                var reason = error is RegexMatchTimeoutException ? "validation-timeout: Native validation did not complete in time; retry when service load decreases."
                    : error is InvalidDataException ? error.Message : "Database could not be read or validated.";
                return new(false, "SQLite", WorkbenchDatabase.SchemaVersion, _state?.Revision ?? 0,
                    Time(DateTimeOffset.UtcNow), false, [reason], IntegrityChecks, new { });
            }
        }
    }

    private void RefreshValidatedStorage()
    {
        try
        {
            var snapshot = _database.Read();
            ValidateDefinitions(snapshot.State);
            _state = snapshot.State; _mutationVersion = snapshot.MutationVersion;
        }
        catch (Exception error) when (StorageFailure(error))
        {
            if (error is RegexMatchTimeoutException)
                throw new WorkbenchError(503, "validation-timeout", "اعتبارسنجی بومی در مهلت مقرر تمام نشد؛ دوباره تلاش کنید. هیچ تغییری ثبت نشد.");
            throw new WorkbenchError(503, "storage-invalid", "تعاریف یا یکپارچگی پایگاه داده معتبر نیست؛ تا اصلاح داده، تصمیم و اجرا متوقف است.");
        }
    }

    private static bool StorageFailure(Exception error) => error is InvalidDataException or SqliteException
        or JsonException or IOException or UnauthorizedAccessException or ArgumentException
        or InvalidOperationException or FormatException or NullReferenceException or OverflowException or RegexMatchTimeoutException;

    private void ValidateDefinitions(WorkbenchState state)
    {
        var stateFingerprint = Fingerprint(state);
        if (stateFingerprint == _validatedDefinitionFingerprint || stateFingerprint == _previousValidatedDefinitionFingerprint) return;
        static void Require(bool valid, string reason)
        { if (!valid) throw new InvalidDataException(reason); }
        static bool Stamp(string? value) => value is not null && ContractJson.IsCanonicalUtc(value);
        static void Unique(IEnumerable<string> ids, string reason)
        {
            var list = ids.ToArray();
            Require(list.All(x => !string.IsNullOrWhiteSpace(x)) && list.Distinct(StringComparer.Ordinal).Count() == list.Length, reason);
        }
        Require(state.Revision > 0 && state.ProjectionState is not null && state.TrustedContext is not null,
            "Positive revision, accepted projection and trusted database context are required.");
        var projection = state.ProjectionState!.Projection;
        var projectionErrors = _module.Projections.Validate(projection);
        Require(projectionErrors.Count == 0, "Native accepted projection validation failed: " + string.Join("; ", projectionErrors));
        Require(state.ProjectionState.Receipts.Count > 0, "Accepted projection event receipt missing.");
        Unique(state.ProjectionState.Receipts.Select(x => x.EventId), "Projection event IDs must be unique.");
        Require(state.ProjectionState.Receipts.All(x => x.Fingerprint.Length == 64 && x.Fingerprint.All(Uri.IsHexDigit)), "Projection receipt fingerprints are invalid.");
        var context = state.TrustedContext!;
        Require(_module.Schemas.Validate("request-context.v2.schema.json", ContractJson.ToNode(context)).Count == 0,
            "Trusted context failed native request-context schema.");
        Require(context.Resource.ProductId == projection.ProductId && context.Grant.ProductId == projection.ProductId
            && context.Resource.ConsumerTenantId == projection.ConsumerTenantId && context.Tenant.TenantId == projection.ConsumerTenantId
            && context.Membership.TenantId == projection.ConsumerTenantId && context.Grant.ConsumerTenantId == projection.ConsumerTenantId
            && context.Subject.SubjectId == context.Membership.SubjectId && context.Subject.SubjectId == context.Grant.SubjectId
            && context.Action == "organization.unit.save" && context.Subject.Audience == "fanasa-organization-fabric-api"
            && context.Resource.Environment == "development" && context.Resource.OperationGroup == "organization",
            "Trusted context identity, membership, grant and product source references disagree.");
        Require(Instant(context.Membership.ValidFrom) < Instant(context.Membership.ValidTo)
            && Instant(context.Grant.ValidFrom) < Instant(context.Grant.ValidTo), "Stored membership/grant validity windows are not ordered.");

        Require(state.Policies.Count > 0, "At least one persisted policy is required.");
        var documents = state.Policies.Concat(state.Drafts).ToArray();
        Unique(documents.Select(x => x.PolicyId), "Policy IDs must be unique across published and draft definitions.");
        Require(documents.Select(x => x.PolicyVersion).Distinct().Count() == documents.Length, "Policy versions must be unique.");
        Unique(state.Drafts.Select(x => x.DraftId!), "Draft IDs must be unique.");
        foreach (var policy in documents)
        {
            Require(policy.PolicyVersion > 0 && !string.IsNullOrWhiteSpace(policy.Name) && !string.IsNullOrWhiteSpace(policy.Author)
                && !string.IsNullOrWhiteSpace(policy.Reason) && Stamp(policy.CreatedAt), "Policy metadata is invalid.");
            Require(policy.ZoneConstraints is { Length: 1 } && new[] { "ir-private-1", "ir-private-2" }.Contains(policy.ZoneConstraints[0])
                && double.IsFinite(policy.RequestsPerSecond) && policy.RequestsPerSecond > 0 && policy.RequestsPerSecond <= 1000000
                && policy.BurstCapacity is >= 1 and <= 1000000 && policy.MonthlyQuota is >= 0 and <= 1000000000,
                "Typed policy limits or zone are invalid.");
            Require(_module.Projections.Validate(WithPolicy(projection, policy, false)).Count == 0, "Policy definition failed native projection validation.");
            if (state.Drafts.Contains(policy))
                Require(policy.Status == "Draft" && !string.IsNullOrWhiteSpace(policy.DraftId)
                    && policy.Reviewer is null && policy.PublishedAt is null, "Draft lifecycle is invalid.");
            else
                Require(policy.Status is "Published" or "Retired" && !string.IsNullOrWhiteSpace(policy.Reviewer)
                    && !string.Equals(policy.Author, policy.Reviewer, StringComparison.OrdinalIgnoreCase) && Stamp(policy.PublishedAt)
                    && Instant(policy.PublishedAt!) >= Instant(policy.CreatedAt), "Published/retired policy requires an independent recorded review.");
        }
        var bound = state.Policies.SingleOrDefault(x => x.PolicyId == projection.Policy.PolicyId && x.PolicyVersion == projection.Policy.PolicyVersion);
        Require(bound is not null, "Accepted binding refers to a missing policy or different version.");
        var expectedBinding = WithPolicy(projection, bound!, false);
        Require(Fingerprint(expectedBinding) == Fingerprint(projection), "Accepted projection fields differ from its persisted bound policy definition.");

        Unique(state.Resources.Select(x => x.ResourceId), "Resource IDs must be unique.");
        Require(state.Resources.Any(x => x.ResourceId == "unit-primary" && x.TenantId == projection.ConsumerTenantId)
            && state.Resources.Any(x => x.ResourceId == "unit-foreign" && x.TenantId != projection.ConsumerTenantId),
            "Required same-tenant and foreign-tenant example records are missing or mislabeled.");
        Require(state.Resources.All(x => Guid.TryParse(x.TenantId, out _) && x.ProductId == projection.ProductId
            && x.Environment == "development" && !string.IsNullOrWhiteSpace(x.Name)
            && x.Name.Length is >= 2 and <= 120 && x.Name == x.Name.Trim() && x.SaveCount >= 0
            && (x.LastSavedAt is null || Stamp(x.LastSavedAt))), "Stored product resource definitions are invalid.");
        Unique(state.Decisions.Select(x => x.DecisionId), "Decision IDs must be unique.");
        foreach (var run in state.Decisions)
        {
            Require(Scenarios.Any(x => x.Id == run.ScenarioId) && run.PolicySelection is "accepted" or "candidate"
                && Stamp(run.CreatedAt) && Stamp(run.ExecutionClock) && run.BindingFingerprint.Length == 64,
                "Decision run metadata is invalid.");
            Require(run.ProposedUnitName is null || (run.ProposedUnitName.Length is >= 2 and <= 120
                && run.ProposedUnitName == run.ProposedUnitName.Trim()), "Decision product input name is invalid.");
            Require(run.TrustedSourcesFingerprint is null || (run.TrustedSourcesFingerprint.Length == 64
                && run.TrustedSourcesFingerprint.All(Uri.IsHexDigit)), "Decision trusted-source fingerprint is invalid.");
            Require(_module.Projections.Validate(run.Projection).Count == 0
                && _module.Schemas.Validate("request-context.v2.schema.json", ContractJson.ToNode(run.Context)).Count == 0
                && _module.Schemas.Validate("decision.v2.schema.json", ContractJson.ToNode(run.RawDecision)).Count == 0,
                "Stored decision input/output failed native schema validation.");
            Require(Fingerprint(_module.Evaluator.Evaluate(run.Projection, run.Context)) == Fingerprint(run.RawDecision),
                "Stored decision differs from deterministic native evaluation of its saved inputs.");
            var saved = state.Resources.SingleOrDefault(x => x.ResourceId == run.Resource.ResourceId);
            Require(saved is not null && saved.TenantId == run.Resource.TenantId && saved.ProductId == run.Resource.ProductId
                && saved.Environment == run.Resource.Environment, "Decision resource provenance no longer matches the product record.");
        }

        Unique(state.Operations.Select(x => x.OperationId), "Operation IDs must be unique.");
        Unique(state.Operations.Select(x => x.DecisionId), "One decision may produce only one product effect.");
        Require(state.Executions.Count == state.Operations.Count, "Operation and successful receipt counts disagree.");
        var monthCounts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var operation in state.Operations)
        {
            var run = state.Decisions.SingleOrDefault(x => x.DecisionId == operation.DecisionId);
            var resource = state.Resources.SingleOrDefault(x => x.ResourceId == operation.ResourceId);
            Require(run is not null && resource is not null && run.PolicySelection == "accepted" && run.RawDecision.Decision == "Permit"
                && operation.ScenarioId == run.ScenarioId && operation.ResourceId == run.Resource.ResourceId
                && operation.TenantId == resource.TenantId && operation.TenantId == run.Projection.ConsumerTenantId
                && operation.Action == run.Context.Action && operation.MeterUnits == 1 && Stamp(operation.ExecutedAt),
                "Product operation has invalid decision, ownership, action or meter references.");
            Require((operation.SavedName is null || (operation.SavedName.Length is >= 2 and <= 120
                && operation.SavedName == operation.SavedName.Trim()))
                && (run!.ProposedUnitName is null || operation.SavedName == run.ProposedUnitName),
                "Saved product name differs from the validated product input retained with the decision.");
            Require(_module.Pep.CanExecute(run!.RawDecision, [Admission, Resource], operation.ExecutedAt),
                "Saved product operation was outside the native decision/obligation validity window.");
            Require(state.Executions.TryGetValue(operation.DecisionId, out var receipt) && receipt.Accepted && !receipt.Idempotent
                && receipt.ReasonCode == "executed" && receipt.Operation == operation
                && Fingerprint(receipt.RawDecision) == Fingerprint(run.RawDecision)
                && receipt.Obligations.Length == 2 && receipt.Obligations.All(x => x.Status == "fulfilled")
                && receipt.Obligations.Select(x => x.Id).Order().SequenceEqual(new[] { Admission, Resource }.Order()),
                "Successful operation receipt or fulfilled obligations are inconsistent.");
            var period = Instant(operation.ExecutedAt).ToString("yyyy-MM", CultureInfo.InvariantCulture);
            monthCounts[period] = monthCounts.GetValueOrDefault(period) + 1;
            var usage = receipt!.Usage; var limits = run.Projection.Limits;
            Require(usage.PeriodId == period && usage.Admitted == monthCounts[period]
                && usage.AllocationId == limits.AllocationId && usage.ConfigRevision == limits.ConfigRevision
                && usage.Remaining == Math.Max(0, limits.Quota.Requests - usage.Admitted)
                && usage.LastAdmittedAt == operation.ExecutedAt && double.IsFinite(usage.Tokens)
                && usage.Tokens >= 0 && usage.Tokens <= limits.Rate.BurstCapacity,
                "Receipt quota, rate or allocation revisions are incoherent.");
        }
        Require(state.Admitted >= 0 && state.Admitted == monthCounts.GetValueOrDefault(state.PeriodId)
            && DateTime.TryParseExact(state.PeriodId, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            && double.IsFinite(state.Tokens) && state.Tokens >= 0 && state.Tokens <= projection.Limits.Rate.BurstCapacity
            && Stamp(state.LastRefillAt) && (state.LastAdmittedAt is null || Stamp(state.LastAdmittedAt)),
            "Admission counters, stored UTC month or token balance disagree with product effects.");
        foreach (var resource in state.Resources)
        {
            var effects = state.Operations.Where(x => x.ResourceId == resource.ResourceId).ToArray();
            Require(resource.SaveCount == effects.Length && resource.LastSavedAt == effects.LastOrDefault()?.ExecutedAt,
                "Product record save count or last-save timestamp differs from committed operations.");
            var lastSavedName = effects.LastOrDefault()?.SavedName;
            Require(lastSavedName is null || resource.Name == lastSavedName,
                "Product record name differs from the latest committed product effect.");
        }
        Require(state.LastAdmittedAt == state.Operations.LastOrDefault()?.ExecutedAt, "Last admission timestamp differs from committed operations.");
        Unique(state.History.Select(x => x.Id), "Audit IDs must be unique.");
        Require(state.History.Count > 0 && state.History.All(x => x.Revision > 0 && x.Revision <= state.Revision
            && Stamp(x.At) && !string.IsNullOrWhiteSpace(x.Type) && !string.IsNullOrWhiteSpace(x.Actor)),
            "Audit revisions or metadata are invalid.");
        _previousValidatedDefinitionFingerprint = _validatedDefinitionFingerprint;
        _validatedDefinitionFingerprint = stateFingerprint;
    }
}
