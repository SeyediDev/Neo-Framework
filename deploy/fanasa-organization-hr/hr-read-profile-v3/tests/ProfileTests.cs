using System.Text.Json.Nodes;
using Fanasa.ContractPolicy;

namespace Fanasa.HrReadCandidate;

internal static class ProfileTests
{
    private const string Now = "2026-10-15T00:00:00.000Z";
    private const string From = "2026-10-14T00:00:00.000Z";
    private const string Until = "2026-10-16T00:00:00.000Z";
    private static readonly string[] Fulfilled = [ContractPolicyEvaluator.AdmissionObligation, ContractPolicyEvaluator.ResourceObligation];

    public static async Task RunAsync()
    {
        var schemas = new ContractSchemaValidator(Path.Combine(AppContext.BaseDirectory, "Schemas"));
        var validator = new ContractProjectionValidator(schemas);
        var evaluator = new ContractPolicyEvaluator(validator);
        var pep = new ContractPolicyPep(schemas);
        var p = Projection();
        var c = Context(HrReadProfile.ChartAction);
        Check.True(validator.Validate(p).Count == 0, "valid newly designed HR profile projection");
        foreach (var action in new[] { HrReadProfile.ChartAction, HrReadProfile.HistoryAction })
        {
            var read = Context(action);
            var d = evaluator.Evaluate(p, read);
            Check.True(d.Decision == "Permit" && d.SchemaVersion == "3.0.0"
                && d.ProfileId == HrReadProfile.ProfileId, "HR chart/history uses v3 profile");
            Check.True(read.Subject.Scopes.SequenceEqual(new[] { HrReadProfile.RequiredScope })
                && !read.Subject.Scopes.Contains(action), "scope is independent of business action");
            Check.True(schemas.Validate("decision.v3.schema.json", ContractJson.ToNode(d)).Count == 0,
                "HR decision satisfies v3 schema");
            Check.True(pep.CanExecute(d, Fulfilled, Now), "PEP accepts fulfilled HR read obligations");
            Check.True(!pep.CanExecute(d, [], Now), "HR permit still requires both obligations");
            Check.True(d.ValidUntil == "2026-10-15T00:00:05.000Z", "HR freshness ceiling is five seconds");
        }

        void Denied(ContractAccessProjection projection, AccessEvaluationContext context, string name) =>
            Check.True(evaluator.Evaluate(projection, context).Decision != "Permit", name);
        Denied(p, c with { Subject = c.Subject with { Scopes = [HrReadProfile.ChartAction] } }, "action-named scope does not grant HR read");
        Denied(p, c with { Subject = c.Subject with { Scopes = ["organization.write"] } }, "write scope does not grant read");
        Denied(p, c with { Subject = c.Subject with { Scopes = [] } }, "scope is required");
        Denied(p, c with { Action = "organization.unit.save" }, "metered write is outside HR read profile");
        Denied(p, c with { Action = "organization.hr.unknown.read" }, "unknown action denied");
        Denied(p, c with { Subject = c.Subject with { Audience = "fanasa-organization-fabric-api" } }, "old synthetic audience is not HR audience");
        Denied(p, c with { Subject = c.Subject with { AuthenticationValid = false } }, "invalid authentication denied");
        Denied(p, c with { Subject = c.Subject with { ClientAuthorized = false } }, "revoked client denied");
        Denied(p, c with { Tenant = c.Tenant with { Active = false } }, "revoked tenant denied");
        Denied(p, c with { Membership = c.Membership with { Active = false } }, "revoked membership denied");
        Denied(p, c with { Membership = c.Membership with { ValidTo = Now } }, "expired membership denied");
        Denied(p, c with { Grant = c.Grant with { Active = false } }, "revoked grant denied");
        Denied(p, c with { Grant = c.Grant with { Role = "ApiInvoker" } }, "previous profile role cannot grant HR read");
        Denied(p, c with { Grant = c.Grant with { Actions = [HrReadProfile.HistoryAction] } }, "grant is action-specific");
        Denied(p, c with { Grant = c.Grant with { ConsumerTenantId = "different-consumer" } }, "cross tenant grant denied");
        Denied(p, c with { Resource = c.Resource with { OperationGroup = "organization.write" } }, "action and group mismatch denied");
        Denied(p, c with { Evidence = c.Evidence with { ContractVersion = 2 } }, "stale contract evidence denied");
        Denied(p, c with { Evidence = c.Evidence with { PolicyVersion = 2 } }, "stale policy evidence denied");
        Denied(p, c with { Evidence = c.Evidence with { ValidUntil = Now } }, "expired evidence denied");
        Denied(p with { Status = "Suspended" }, c, "suspended contract denied");
        Denied(p with { Status = "Ended" }, c, "ended contract denied");
        Denied(p with { FeatureEntitlements = [HrReadProfile.HistoryAction] }, c, "contract entitlement is action-specific");
        Denied(p with { Policy = p.Policy with { ExplicitDenyActions = [HrReadProfile.ChartAction] } }, c, "explicit deny overrides HR read");
        Denied(p with { SchemaVersion = "4.0.0" }, c, "unknown schema fails closed");
        Denied(p with { ProfileId = "fanasa.organization-hr-read/2" }, c, "unknown profile fails closed");
        Denied(p with { EligibleRoles = ["ApiInvoker"] }, c, "invalid projection role fails closed");
        Denied(p with { Ownership = null }, c, "HR ownership reference required");
        Denied(p with { OwnerTenantId = "platform" }, c, "platform owner cannot be represented as a tenant");
        Denied(p with { Ownership = p.Ownership! with { Owner = "different-owner" } }, c, "platform ownership must match registry owner kind");
        Denied(p with { Ownership = p.Ownership! with { RegisteredProductId = Guid.Empty.ToString("D") } }, c, "empty product UUID rejected");
        Denied(p with { Ownership = p.Ownership! with { RegisteredProductId = "not-a-uuid" } }, c, "malformed product UUID rejected");
        var tenantOwner = "845f4acd-8a55-46da-b7ec-c122787958cf";
        var tenantOwned = p with { OwnerTenantId = tenantOwner, Ownership = p.Ownership! with { Kind = "Tenant", Owner = tenantOwner } };
        Check.True(validator.Validate(tenantOwned).Count == 0, "explicit tenant ownership remains distinct from platform ownership");
        Denied(tenantOwned with { OwnerTenantId = null }, c, "tenant ownership requires same tenant reference");
        Denied(p with { Policy = p.Policy with { TemplateId = "organization-unit-save/1" } }, c, "write template rejected");
        Denied(p with { Limits = p.Limits with { ConfigRevision = null } }, c, "HR requires independent allocation revision");
        Denied(p with { Limits = p.Limits with { ConfigRevision = 0 } }, c, "zero allocation revision rejected");
        var wrongScope = p.Limits.Scope with { OperationGroups = ["organization.write"] };
        Denied(p with { Limits = p.Limits with { Scope = wrongScope, CanonicalScope = ContractProjectionValidator.CanonicalScope(wrongScope) } },
            c, "projection cannot allocate write group to read profile");

        var selector = new ProjectionSelector(p.ConsumerTenantId, p.ProfileId, p.ProductId, p.ContractId);
        Check.True(ContractProjectionSelector.FindSingle([p, p with { ProfileId = "fanasa.contract-policy/2" }], selector)?.ProfileId == p.ProfileId,
            "consumer/profile/product/contract selects unambiguously");
        Check.True(ContractProjectionSelector.FindSingle([p], selector with { ContractId = "other-contract" }) is null, "selector does not pick another contract");
        Check.True(ContractProjectionSelector.FindSingle([p], selector with { ProfileId = "unknown-profile" }) is null, "selector does not pick another profile");
        Check.Throws<InvalidOperationException>(() => ContractProjectionSelector.FindSingle([p, p], selector), "duplicate matches rejected");
        Check.Throws<ArgumentException>(() => ContractProjectionSelector.FindSingle([p], selector with { ContractId = "" }), "partial selector rejected");

        var evt = new ContractProjectionEvent { SchemaVersion = "3.0.0", EventId = "hr-fixture-event", Producer = "fanasa-contracts",
            AggregateVersion = 1, OccurredAt = Now, CorrelationId = "hr-fixture-correlation", Projection = p };
        var projector = new ContractProjectionProjector(validator);
        var applied = projector.Apply(null, evt, "fanasa-contracts");
        Check.True(applied.Status == "applied", "v3 event applies to copied projector");
        Check.True(projector.Apply(null, evt with { SchemaVersion = "4.0.0" }, "fanasa-contracts").Status == "rejected", "unknown event version rejected");
        Check.True(projector.Apply(null, evt, "untrusted").Status == "rejected", "HR projection producer still authenticated");
        var next = evt with { EventId = "hr-fixture-event-two", AggregateVersion = 2,
            Projection = p with { ContractVersion = 2, Limits = p.Limits with { Rate = new("token-bucket", 2, 10) } } };
        Check.True(projector.Apply(applied.State, next, "fanasa-contracts").Status == "conflict", "same config revision cannot alter HR limits");
        Check.True(projector.Apply(applied.State, next with { Projection = next.Projection with {
            Limits = next.Projection.Limits with { ConfigRevision = 2 } } }, "fanasa-contracts").Status == "applied", "independent revision authorizes changed HR allocation");

        var d0 = evaluator.Evaluate(p, c);
        Check.True(!pep.CanExecute(d0 with { SchemaVersion = "4.0.0" }, Fulfilled, Now), "PEP unknown version fails closed");
        Check.True(!pep.CanExecute(d0 with { ProfileId = "unknown-profile" }, Fulfilled, Now), "PEP unknown profile fails closed");
        Check.True(!pep.CanExecute(d0, Fulfilled, d0.ValidUntil), "PEP expiry boundary is closed");
        Check.True(!pep.CanExecute(d0 with { ValidUntil = "2026-10-15T00:00:05.001Z" }, Fulfilled, Now), "PEP rejects HR witness beyond five seconds");
        var snapshot = new AccessAttributeSnapshotData(p, c.Tenant, c.Membership, c.Grant, c.Evidence, DateTimeOffset.Parse(Now));
        var provider = new TrustedAccessSnapshotProvider(new SnapshotSource(snapshot), validator, new FixtureTime(), TimeSpan.FromSeconds(60));
        var query = new TrustedAccessSnapshotQuery(c.RequestId, c.Subject, c.Resource, c.Action, c.Environment.Zone, Until);
        Check.True(!(await provider.ReadAsync(query)).Succeeded, "HR PIP cannot use legacy consumer-only selection");
        Check.True(!(await provider.ReadAsync(query with { Selector = selector with { ContractId = "other-contract" } })).Succeeded,
            "HR PIP correlates selected contract");
        Check.True(!(await provider.ReadAsync(query with { Selector = selector with { ProfileId = "fanasa.contract-policy/2" } })).Succeeded,
            "HR PIP correlates selected profile");
        var ready = await provider.ReadAsync(query with { Selector = selector });
        Check.True(ready.Succeeded && ready.Context!.Evidence.ValidUntil == "2026-10-15T00:00:05.000Z",
            "HR PIP requires full selector and applies five-second ceiling");
    }

    private static ContractAccessProjection Projection()
    {
        var scope = new AllocationScope("fixture-consumer", [HrReadProfile.ProductId], ["development"], [HrReadProfile.OperationGroup]);
        return new ContractAccessProjection {
            SchemaVersion = HrReadProfile.SchemaVersion, ProfileId = HrReadProfile.ProfileId,
            ContractId = "fixture-hr-contract", ContractVersion = 1, OwnerTenantId = null,
            Ownership = new("Platform", "platform", "58ba3f2f-2435-4c4d-9496-dcbfef88eadf"),
            ConsumerTenantId = "fixture-consumer", ProductId = HrReadProfile.ProductId,
            OfferId = "fixture-offer", SubscriptionId = "fixture-subscription", Status = "Active",
            ValidFrom = From, ValidTo = Until, EffectiveAt = From,
            FeatureEntitlements = [HrReadProfile.ChartAction, HrReadProfile.HistoryAction], EligibleRoles = [HrReadProfile.Role],
            Seats = new(10, "active-membership", "consumer-product-subscription"),
            Limits = new AllocationLimits { AllocationId = "fixture-hr-allocation", ConfigRevision = 1,
                Scope = scope, CanonicalScope = ContractProjectionValidator.CanonicalScope(scope), Meter = "api.request.admitted", Unit = "request",
                Rate = new("token-bucket", 10, 10), Quota = new("fixed-calendar-utc", "month", 100) },
            ZoneConstraints = ["internal"], PricingReference = new("fixture-price", 1),
            Policy = new("fixture-hr-policy", 1, HrReadProfile.TemplateId, "deny-overrides", [])
        };
    }

    private static AccessEvaluationContext Context(string action) => new() {
        RequestId = "fixture-hr-request", Subject = new("fixture-subject", true, true, HrReadProfile.Audience, [HrReadProfile.RequiredScope]),
        Resource = new(HrReadProfile.ProductId, "fixture-consumer", "development", HrReadProfile.OperationGroup), Action = action,
        Environment = new(Now, "internal"), Tenant = new("fixture-consumer", true),
        Membership = new("fixture-subject", "fixture-consumer", true, From, Until),
        Grant = new("fixture-grant", "fixture-subject", "fixture-consumer", HrReadProfile.ProductId, HrReadProfile.Role,
            [HrReadProfile.ChartAction, HrReadProfile.HistoryAction], true, From, Until), Evidence = new(Until, 1, 1)
    };

    private sealed class SnapshotSource(AccessAttributeSnapshotData data) : IAccessAttributeSnapshotSource
    {
        public ValueTask<AccessAttributeSnapshotData?> ReadAsync(TrustedAccessSnapshotQuery query, CancellationToken cancellationToken = default)
            => ValueTask.FromResult<AccessAttributeSnapshotData?>(data);
    }
    private sealed class FixtureTime : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => DateTimeOffset.Parse(Now);
    }
}
