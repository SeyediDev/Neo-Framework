using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Organization;
using Fanasa.AccessManagement.Web.Platform;
using Fanasa.AccessManagement.Web.ContractPolicies;
using Fanasa.ContractPolicy;

namespace Fanasa.AccessManagement.Web.Metering;

/// <summary>A frozen contract/allocation/rate binding for one admitted operation.</summary>
public sealed record OrganizationUsageBinding(Guid ConsumerTenantId, string ContractId, int ContractVersion,
    string AllocationId, string AllocationPeriodId, string PlanKey, string PricingReference, UsageRateSnapshot RateSnapshot);

/// <summary>A frozen local permission invocation. Only this adapter can construct the proof.</summary>
public sealed class OrganizationUsageAdmission
{
    private string? completedFingerprint;
    internal OrganizationUsageAdmission(OrganizationUsageMetering issuer, Guid owner, Guid consumer, string actor,
        OrgCommand command, Guid requestId, string fingerprint, DateTimeOffset acceptedAt,
        OrganizationUsageBinding binding, string decisionId)
    {
        Issuer = issuer; OwnerTenantId = owner; ConsumerTenantId = consumer; Actor = actor; Command = command;
        RequestId = requestId; RequestFingerprint = fingerprint; AcceptedAt = acceptedAt; Binding = binding; DecisionId = decisionId;
    }
    internal OrganizationUsageMetering Issuer { get; }
    internal OrgCommand Command { get; }
    internal OrganizationUsageBinding Binding { get; }
    internal string DecisionId { get; }
    public Guid OwnerTenantId { get; }
    public Guid ConsumerTenantId { get; }
    public string Actor { get; }
    public Guid RequestId { get; }
    public string RequestFingerprint { get; }
    public DateTimeOffset AcceptedAt { get; }
    internal string? CompletedFingerprint => completedFingerprint;
    internal void Seal(UsageEventV1 message)
    {
        var fingerprint = message.EventFingerprint;
        var previous = Interlocked.CompareExchange(ref completedFingerprint, fingerprint, null);
        if (previous is not null && previous != fingerprint) throw new InvalidOperationException("The usage invocation already produced a different completed event.");
    }
}

/// <summary>
/// Opt-in local unit.save metering. Reuses the central product/tenant/subscription/entitlement source.
/// Uses explicit configuration bindings or authenticated ContractPolicy projections with exact persisted prices.
/// </summary>
public sealed class OrganizationUsageMetering : IUsageSnapshotValidator
{
    public const string ProductKey = "organization-fabric";
    public const string PolicyVersion = "organization-permission/v1";
    private readonly IAccessManagement access;
    private readonly PlatformRegistry registry;
    private readonly TimeProvider clock;
    private readonly OrganizationUsageBinding[] bindings;
    private readonly string environment;
    private readonly UsageProducer producer;
    private readonly ContractPolicyStore? contracts;
    private readonly OrganizationUsagePriceStore? prices;
    private readonly bool useContractPolicy;
    private readonly ContractLink[] contractLinks = [];
    public bool Enabled { get; }
    internal string GatewayEnvironment => environment;

    public OrganizationUsageMetering(IConfiguration configuration, IAccessManagement access, PlatformRegistry registry, TimeProvider? timeProvider = null,
        ContractPolicyStore? contractPolicyStore = null, OrganizationUsagePriceStore? priceStore = null)
    {
        this.access = access; this.registry = registry; clock = timeProvider ?? TimeProvider.System;
        contracts = contractPolicyStore; prices = priceStore;
        var section = configuration.GetSection("UsageMetering:Organization");
        var flag = section["Enabled"];
        if (flag is not null && !bool.TryParse(flag, out _)) throw new ArgumentException("UsageMetering:Organization:Enabled must be a boolean.");
        Enabled = flag is not null && bool.Parse(flag);
        // An inactive feature requires no new configuration and introduces no synthetic defaults.
        if (!Enabled) { bindings = []; environment = ""; producer = new("", "product-backend", ""); return; }
        environment = Required(section, "Environment");
        if (environment is not ("development" or "pilot" or "production")) throw new ArgumentException("Usage metering environment is invalid.");
        producer = new(Required(section, "ProducerId"), "product-backend", Required(section, "DeploymentId"));
        var source = section["BindingSource"] ?? "configuration";
        if (source is not ("configuration" or "contract-policy")) throw new ArgumentException("Unknown organization usage binding source.");
        useContractPolicy = source == "contract-policy";
        if (useContractPolicy)
        {
            if (contracts is null || prices is null) throw new ArgumentException("Contract-policy metering requires the central projection and price stores.");
            bindings = [];
            contractLinks = section.GetSection("ContractLinks").GetChildren().Select(ParseContractLink).ToArray();
            if (contractLinks.Length == 0 || contractLinks.GroupBy(x => (x.Consumer, x.ContractId, x.SubscriptionId, x.OfferId)).Any(x => x.Count() != 1))
                throw new ArgumentException("Contract-policy metering requires unambiguous explicit commercial-to-central subscription links.");
            return;
        }
        bindings = section.GetSection("Bindings").GetChildren().Select(ParseBinding).ToArray();
        if (bindings.Length == 0) throw new ArgumentException("Enabled organization usage metering requires trusted bindings.");
        foreach (var group in bindings.GroupBy(x => x.ConsumerTenantId))
        {
            var ordered = group.OrderBy(x => x.RateSnapshot.EffectiveFrom).ToArray();
            for (var i = 1; i < ordered.Length; i++)
                if (ordered[i - 1].RateSnapshot.EffectiveTo > ordered[i].RateSnapshot.EffectiveFrom)
                    throw new ArgumentException("Organization usage binding rate windows are ambiguous for a consumer.");
        }
        foreach (var group in bindings.GroupBy(x => (x.RateSnapshot.RateId, x.RateSnapshot.RateVersion)))
            if (group.Select(x => JsonSerializer.Serialize(x.RateSnapshot)).Distinct(StringComparer.Ordinal).Count() != 1)
                throw new ArgumentException("A rate ID/version must identify one immutable snapshot.");
    }
    public DateTimeOffset GetUtcNow() => clock.GetUtcNow().ToUniversalTime();

    /// <summary>Fresh authenticated permission/member/grant check, with no tariff resolution.</summary>
    internal void ValidateInvocation(Guid tenant, OrgCommand command, string actor, ClaimsPrincipal? principal)
    {
        RequireEnabled();
        if (command.Operation != "unit.save") throw new ArgumentException("The metered pilot only accepts unit.save.");
        if (principal?.Identity?.IsAuthenticated != true || !OrgAuthorization.Allows(principal, tenant, true)
            || OrgAuthorization.Subject(principal) != actor) throw new UnauthorizedAccessException("Authenticated organization.write invocation is required.");
        if (!Guid.TryParseExact(command.RequestId, "D", out var requestId) || requestId == Guid.Empty)
            throw new ArgumentException("Metered unit.save requires a UUID requestId.");
        var now = GetUtcNow();
        RequireActiveConsumerAndGrant(tenant, actor, now);
        RequireCurrentProductAccess(tenant, now);
    }
    /// <summary>Read the same frozen rate/contract binding the committed operation will use.
    /// Gateway verification creates no admission, usage event or charge.</summary>
    internal OrganizationUsageBinding ReadGatewayBinding(Guid tenant, string actor, OrgCommand command, ClaimsPrincipal principal)
    {
        ValidateInvocation(tenant, command, actor, principal);
        var now = GetUtcNow();
        var binding = ResolveBinding(tenant, now);
        _ = RequireCentralBindings(tenant, actor, binding, now);
        return binding;
    }

    internal OrganizationUsageAdmission Admit(Guid tenant, OrgCommand command, string actor, ClaimsPrincipal principal, string requestFingerprint)
    {
        ValidateInvocation(tenant, command, actor, principal);
        var serialized = JsonSerializer.Serialize(command with { ExpectedRevision = 0 });
        if (requestFingerprint != serialized) throw new ArgumentException("Organization command fingerprint does not match the invocation.");
        var acceptedAt = GetUtcNow(); var binding = ResolveBinding(tenant, acceptedAt);
        var owner = RequireCentralBindings(tenant, actor, binding, acceptedAt);
        return new(this, owner, tenant, actor, command, Guid.Parse(command.RequestId!), UsageEventV1.Hash(serialized), acceptedAt, binding,
            "organization-permission:" + Guid.NewGuid().ToString("N"));
    }
    /// <summary>
    /// Builds a candidate completed event; its financial admission reference becomes authoritative only
    /// when the shared Fabric transaction accepts ledger credit and commits the organization result.
    /// </summary>
    internal UsageEventV1 CreateCompleted(OrganizationUsageAdmission admission, OrgState result)
    {
        RequireEnabled();
        if (!ReferenceEquals(admission.Issuer, this) || result.TenantId != admission.ConsumerTenantId
            || result.Revision != admission.Command.ExpectedRevision + 1 || result.Changes.LastOrDefault() is not { } change
            || change.Revision != result.Revision || change.Operation != "unit.save" || change.Actor != admission.Actor
            || !result.Units.Any(x => x.Active && x.Code == admission.Command.Code?.Trim()
                && (!admission.Command.Id.HasValue || x.Id == admission.Command.Id.Value)))
            throw new ArgumentException("Organization result does not match the authenticated metered invocation.");
        var occurredAt = GetUtcNow(); var binding = admission.Binding;
        var receipt = $"organization:{result.TenantId:D}:{result.Revision}";
        var operation = new UsageOperation("organization-unit-save:" + admission.RequestId.ToString("D"), "organization.unit.save/v1",
            admission.Actor, admission.RequestId, admission.RequestFingerprint, admission.AcceptedAt, "organization-unit-committed/v1");
        var candidate = new UsageEventV1("1.0", Guid.NewGuid(), occurredAt, GetUtcNow(), producer, environment,
            admission.OwnerTenantId, admission.ConsumerTenantId, ProductKey, binding.ContractId, binding.ContractVersion,
            binding.AllocationId, binding.AllocationPeriodId, operation,
            new(admission.DecisionId, PolicyVersion, "ledger-credit-candidate"), new("organization.change", "1", "change", "1"),
            binding.RateSnapshot, "completed", new(receipt, UsageEventV1.Hash(JsonSerializer.Serialize(result)), "fabric://" + receipt),
            admission.RequestId) { InvocationProof = admission };
        candidate = candidate with
        {
            EventId = UsageEventV1.StableGuid("usage-event:v1:" + candidate.BusinessKey),
            Authorization = candidate.Authorization with { AdmissionId = "ledger-credit:" + UsageEventV1.Hash(candidate.BusinessKey) }
        };
        candidate.Validate(); admission.Seal(candidate); return candidate;
    }
    public void ValidateNew(UsageEventV1 message)
    {
        RequireEnabled(); message.Validate();
        var proof = message.InvocationProof;
        if (proof is null || !ReferenceEquals(proof.Issuer, this) || proof.CompletedFingerprint != message.EventFingerprint || message.ConsumerTenantId != proof.ConsumerTenantId
            || message.OwnerTenantId != proof.OwnerTenantId || message.Operation.ActorId != proof.Actor
            || message.Operation.RequestId != proof.RequestId || message.Operation.Id != "organization-unit-save:" + proof.RequestId.ToString("D")
            || message.Operation.Version != "organization.unit.save/v1" || message.Operation.AcceptedAt != proof.AcceptedAt
            || message.Operation.RequestFingerprint != proof.RequestFingerprint || message.Authorization.DecisionId != proof.DecisionId
            || message.Authorization.PolicyVersion != PolicyVersion
            || message.Authorization.AdmissionId != "ledger-credit:" + UsageEventV1.Hash(message.BusinessKey)
            || message.ProductKey != ProductKey || message.Environment != environment || message.Producer != producer)
            throw new UnauthorizedAccessException("A new local usage event requires the actual authenticated invocation proof.");
        var binding = proof.Binding;
        if (message.ContractId != binding.ContractId || message.ContractVersion != binding.ContractVersion
            || message.AllocationId != binding.AllocationId || message.AllocationPeriodId != binding.AllocationPeriodId
            || message.RateSnapshot != binding.RateSnapshot || ResolveBinding(message.ConsumerTenantId, proof.AcceptedAt) != binding
            || RequireCentralBindings(message.ConsumerTenantId, proof.Actor, binding, GetUtcNow()) != message.OwnerTenantId)
            throw new UnauthorizedAccessException("Usage contract/allocation/owner/rate binding is not trusted.");
    }
    private OrganizationUsageBinding ResolveBinding(Guid consumer, DateTimeOffset acceptedAt)
    {
        if (useContractPolicy)
        {
            var current = RequireProjectionAccess(consumer, acceptedAt); var projection = current.Projection;
            if (projection.ContractVersion is < 1 or > int.MaxValue || projection.PricingReference.PriceVersion is < 1 or > int.MaxValue)
                throw new UnauthorizedAccessException("Contract/price version exceeds the UsageEvent 1.0 version range.");
            var rate = prices!.Resolve(projection.PricingReference.PriceId, (int)projection.PricingReference.PriceVersion, current.Owner, acceptedAt);
            if (rate.Currency != current.Currency || rate.BillingMode != current.BillingMode)
                throw new UnauthorizedAccessException("The exact contract price does not match the central subscription plan.");
            return new(consumer, projection.ContractId, (int)projection.ContractVersion, projection.Limits.AllocationId,
                acceptedAt.ToString("yyyy-MM", CultureInfo.InvariantCulture), current.PlanKey, projection.PricingReference.PriceId, rate);
        }
        var eligible = bindings.Where(x => x.ConsumerTenantId == consumer
            && x.RateSnapshot.EffectiveFrom <= acceptedAt && acceptedAt < x.RateSnapshot.EffectiveTo).ToArray();
        if (eligible.Length != 1) throw new UnauthorizedAccessException("No unique admitted organization contract/rate binding exists.");
        return eligible[0];
    }
    private Guid RequireCentralBindings(Guid consumer, string actor, OrganizationUsageBinding binding, DateTimeOffset now)
    {
        RequireActiveConsumerAndGrant(consumer, actor, now);
        if (useContractPolicy)
        {
            var current = RequireProjectionAccess(consumer, now); var projection = current.Projection;
            if (binding.ContractId != projection.ContractId || binding.ContractVersion != projection.ContractVersion
                || binding.AllocationId != projection.Limits.AllocationId || binding.PricingReference != projection.PricingReference.PriceId
                || binding.RateSnapshot.RateVersion != projection.PricingReference.PriceVersion || binding.PlanKey != current.PlanKey
                || binding.RateSnapshot.BillingMode != current.BillingMode || binding.RateSnapshot.Currency != current.Currency)
                throw new UnauthorizedAccessException("The admitted contract price does not match current central access.");
            return current.Owner;
        }
        var products = registry.Products().Where(x => x.Key == ProductKey).ToArray();
        if (products.Length != 1 || products[0].TenantId is not { } owner
            || access.GetTenants().Count(x => x.Id == owner && x.IsActive) != 1)
            throw new UnauthorizedAccessException("Central organization product has no unique active owner tenant.");
        var product = products[0];
        var subscriptions = access.GetSubscriptions(consumer).Where(x => x.ProductKey == ProductKey && x.Status == "active"
            && x.StartedAt <= now && (!x.RenewsAt.HasValue || x.RenewsAt > now)).ToArray();
        if (subscriptions.Length != 1) throw new UnauthorizedAccessException("Exactly one active organization subscription is required.");
        var plans = access.GetPlans(ProductKey).Where(x => x.Id == subscriptions[0].PricingPlanId && x.IsActive && x.Key == binding.PlanKey
            && x.BillingMode == binding.RateSnapshot.BillingMode && x.Currency == binding.RateSnapshot.Currency).ToArray();
        var entitlements = access.GetEntitlements(consumer).Where(x => x.ProductKey == ProductKey && x.ExternalContractId == binding.ContractId
            && x.CenterSlug == product.Center && x.PlanKey == binding.PlanKey && x.Status == "active"
            && x.EffectiveFrom <= now && (!x.EffectiveTo.HasValue || x.EffectiveTo > now)).ToArray();
        if (plans.Length != 1 || entitlements.Length != 1) throw new UnauthorizedAccessException("Central subscription/entitlement does not match the trusted usage binding.");
        return owner;
    }
    // Replay still requires current product access, independently of the original financial
    // contract/rate/allocation. A retired tariff cannot reprice or invalidate an exact receipt.
    private void RequireCurrentProductAccess(Guid consumer, DateTimeOffset now)
    {
        if (useContractPolicy) { _ = RequireProjectionAccess(consumer, now); return; }
        var products = registry.Products().Where(x => x.Key == ProductKey).ToArray();
        if (products.Length != 1 || products[0].TenantId is not { } owner
            || access.GetTenants().Count(x => x.Id == owner && x.IsActive) != 1)
            throw new UnauthorizedAccessException("Current central organization product has no unique active owner.");
        var subscriptions = access.GetSubscriptions(consumer).Where(x => x.ProductKey == ProductKey && x.Status == "active"
            && x.StartedAt <= now && (!x.RenewsAt.HasValue || x.RenewsAt > now)).ToArray();
        if (subscriptions.Length != 1) throw new UnauthorizedAccessException("Current organization subscription is not uniquely active.");
        var plans = access.GetPlans(ProductKey).Where(x => x.ProductKey == ProductKey
            && x.Id == subscriptions[0].PricingPlanId && x.IsActive).ToArray();
        if (plans.Length != 1) throw new UnauthorizedAccessException("Current organization subscription plan is not uniquely active.");
        var entitlements = access.GetEntitlements(consumer).Where(x => x.ProductKey == ProductKey
            && x.CenterSlug == products[0].Center && x.PlanKey == plans[0].Key && x.Status == "active"
            && x.EffectiveFrom <= now && (!x.EffectiveTo.HasValue || x.EffectiveTo > now)).ToArray();
        if (entitlements.Length != 1) throw new UnauthorizedAccessException("Current organization entitlement is not uniquely active.");
    }
    private ProjectionAccess RequireProjectionAccess(Guid consumer, DateTimeOffset now)
    {
        var projection = contracts!.Find(consumer) ?? throw new UnauthorizedAccessException("A unique central contract projection is required.");
        var products = registry.Products().Where(x => x.Key == ProductKey).Take(2).ToArray();
        if (products.Length != 1 || products[0].TenantId is not { } owner
            || access.GetTenants().Count(x => x.Id == owner && x.IsActive) != 1
            || projection.SchemaVersion != "2.0.0" || projection.ProductId != ProductKey
            || !Guid.TryParseExact(projection.ConsumerTenantId, "D", out var projectionConsumer) || projectionConsumer != consumer
            || !Guid.TryParseExact(projection.OwnerTenantId, "D", out var projectionOwner) || projectionOwner != owner
            || projection.Status != "Active" || ProjectionInstant(projection.EffectiveAt) > now
            || ProjectionInstant(projection.ValidFrom) > now || now >= ProjectionInstant(projection.ValidTo)
            || !projection.FeatureEntitlements.Contains("organization.unit.save", StringComparer.Ordinal)
            || projection.Policy.ExplicitDenyActions.Contains("organization.unit.save", StringComparer.Ordinal)
            || !projection.Limits.Scope.Environments.Contains(environment, StringComparer.Ordinal)
            || !projection.Limits.Scope.OperationGroups.Contains("organization", StringComparer.Ordinal)
            || projection.Limits.Meter != "api.request.admitted" || projection.Limits.Unit != "request"
            || projection.Limits.Quota.Algorithm != "fixed-calendar-utc" || projection.Limits.Quota.Period != "month")
            throw new UnauthorizedAccessException("Current contract owner, entitlement, window or organization scope is denied.");
        var allocation = contracts.ReadAllocation(projection.Limits.AllocationId);
        if (allocation is null || JsonSerializer.Serialize(allocation, ContractJson.Options) != JsonSerializer.Serialize(projection.Limits, ContractJson.Options))
            throw new UnauthorizedAccessException("Contract allocation does not match the current central allocation revision.");
        var links = contractLinks.Where(x => x.Consumer == consumer && x.ContractId == projection.ContractId
            && x.SubscriptionId == projection.SubscriptionId && x.OfferId == projection.OfferId).Take(2).ToArray();
        if (links.Length != 1) throw new UnauthorizedAccessException("The commercial subscription/offer has no exact central link.");
        var subscriptions = access.GetSubscriptions(consumer).Where(x => x.Id == links[0].CentralSubscriptionId
            && x.TenantId == consumer && x.ProductKey == ProductKey && x.Status == "active" && x.StartedAt <= now
            && (!x.RenewsAt.HasValue || x.RenewsAt > now) && x.PricingPlanId == links[0].CentralPricingPlanId).Take(2).ToArray();
        var plans = access.GetPlans(ProductKey).Where(x => x.Id == links[0].CentralPricingPlanId && x.ProductKey == ProductKey
            && x.IsActive && x.BillingMode == "postpaid").Take(2).ToArray();
        if (subscriptions.Length != 1 || plans.Length != 1)
            throw new UnauthorizedAccessException("The explicitly linked central subscription/plan is not uniquely active.");
        return new(projection, owner, plans[0].Key, plans[0].Currency, plans[0].BillingMode);
    }
    private sealed record ProjectionAccess(ContractAccessProjection Projection, Guid Owner, string PlanKey, string Currency, string BillingMode);
    private sealed record ContractLink(Guid Consumer, string ContractId, string SubscriptionId, string OfferId, Guid CentralSubscriptionId, Guid CentralPricingPlanId);
    private static ContractLink ParseContractLink(IConfigurationSection section) => new(ConfigurationGuid(section, "ConsumerTenantId"),
        Required(section, "ContractId"), Required(section, "SubscriptionId"), Required(section, "OfferId"),
        ConfigurationGuid(section, "CentralSubscriptionId"), ConfigurationGuid(section, "CentralPricingPlanId"));
    private static Guid ConfigurationGuid(IConfiguration section, string name)
    {
        if (!Guid.TryParseExact(Required(section, name), "D", out var value) || value == Guid.Empty)
            throw new ArgumentException("A commercial subscription link requires exact central UUIDs.");
        return value;
    }
    private static DateTimeOffset ProjectionInstant(string text) => DateTimeOffset.ParseExact(text, "yyyy-MM-dd'T'HH:mm:ss.fff'Z'",
        CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    private void RequireActiveConsumerAndGrant(Guid consumer, string actor, DateTimeOffset now)
    {
        if (access.GetTenants().Count(x => x.Id == consumer && x.IsActive) != 1
            || access.GetUsers(consumer).Count(x => x.KeycloakSubject == actor && x.IsActive) != 1
            || access is not PersistentAccessManagement durable
            || durable.GetGrants(actor).Count(x => x.TenantId == consumer && x.Permission == "organization.write"
                && (!x.ExpiresAt.HasValue || x.ExpiresAt > now)) != 1)
            throw new UnauthorizedAccessException("An active central member and current durable organization.write grant are required.");
    }
    private void RequireEnabled()
    { if (!Enabled) throw new InvalidOperationException("Organization usage metering is disabled."); }
    private static OrganizationUsageBinding ParseBinding(IConfigurationSection section)
    {
        if (!Guid.TryParseExact(Required(section, "ConsumerTenantId"), "D", out var consumer) || consumer == Guid.Empty)
            throw new ArgumentException("Usage binding consumerTenantId must be a UUID.");
        var rate = section.GetSection("RateSnapshot");
        var snapshot = new UsageRateSnapshot(Required(rate, "RateId"), PositiveInt(rate, "RateVersion"), Utc(rate, "EffectiveFrom"), Utc(rate, "EffectiveTo"),
            Required(rate, "UnitPrice"), Required(rate, "Currency"), Int(rate, "MinorUnitScale"), Required(rate, "Rounding"), Required(rate, "BillingMode"));
        _ = UsageEventV1.ParseDecimal(snapshot.UnitPrice);
        if (snapshot.EffectiveFrom >= snapshot.EffectiveTo || snapshot.Currency is not ("IRR" or "USD" or "EUR")
            || snapshot.MinorUnitScale != (snapshot.Currency == "IRR" ? 0 : 2) || snapshot.Rounding != "away-from-zero" || snapshot.BillingMode != "postpaid")
            throw new ArgumentException("Trusted usage rate snapshot is invalid.");
        var reference = Required(section, "PricingReference");
        if (reference != snapshot.RateId) throw new ArgumentException("PricingReference must identify this immutable rate ID.");
        return new(consumer, Required(section, "ContractId"), PositiveInt(section, "ContractVersion"), Required(section, "AllocationId"),
            Required(section, "AllocationPeriodId"), Required(section, "PlanKey"), reference, snapshot);
    }
    private static string Required(IConfiguration section, string name)
    { var value = section[name]; UsageEventV1.Label(value); return value!; }
    private static int Int(IConfiguration section, string name)
    { if (!int.TryParse(Required(section, name), NumberStyles.None, CultureInfo.InvariantCulture, out var value)) throw new ArgumentException("Invalid usage binding integer."); return value; }
    private static int PositiveInt(IConfiguration section, string name)
    { var value = Int(section, name); if (value < 1) throw new ArgumentException("Usage binding version must be positive."); return value; }
    private static DateTimeOffset Utc(IConfiguration section, string name)
    {
        var text = Required(section, name);
        if (!text.EndsWith('Z') || !DateTimeOffset.TryParseExact(text, ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"], CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value)) throw new ArgumentException("Usage binding timestamp must use UTC Z.");
        return value;
    }
}
