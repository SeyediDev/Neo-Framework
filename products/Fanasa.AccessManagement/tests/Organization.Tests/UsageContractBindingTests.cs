using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Fanasa.AccessManagement.Web.Accounting;
using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.ContractPolicies;
using Fanasa.AccessManagement.Web.Metering;
using Fanasa.AccessManagement.Web.Organization;
using Fanasa.AccessManagement.Web.Persistence;
using Fanasa.AccessManagement.Web.Platform;
using Fanasa.ContractPolicy;
using Microsoft.Extensions.Configuration;

static class UsageContractBindingTests
{
    public static void Run(string directory, Action<bool, string> check)
    {
        var root = Path.Combine(directory, "usage-contract-bindings"); Directory.CreateDirectory(root);
        var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Platform:RegistryPath"] = Path.Combine(root, "registry.db") }).Build();
        var registry = new PlatformRegistry(settings); using var database = new FabricDatabase(Path.Combine(root, "fabric.db"));
        var access = new PersistentAccessManagement(database, new InMemoryAccessManagement(registry));
        var owner = access.GetTenants().Single().Id; var consumer = access.CreateTenant("projection-consumer", "Projection Consumer").Id;
        registry.SetTenantSource(access.GetTenants); registry.SetMembershipSource(access.GetUsers);
        registry.Register(new(Guid.NewGuid(), "organization-fabric", "Organization", ContractPolicyStore.ProductAudience,
            "fanasa.ayin", null, null, owner, "fanasa"), "test");
        const string actor = "projection-member";
        access.AddUser(new(consumer, actor, "Projection Member")); access.SetGrant(new(consumer, actor, "organization.write", null), "admin", "test");
        var plan = access.RegisterPlan(new("organization-fabric", "projection-postpaid", "Projection Postpaid", "postpaid", "IRR", 0, 0));
        var subscription = access.Subscribe(new(consumer, "organization-fabric", plan.Id, 20));
        var clock = new BindingClock(DateTimeOffset.UtcNow.AddMinutes(1)); var originalTime = clock.Now;
        var accounting = new AccountingStore(root, access, clock, database);
        accounting.Execute(consumer, new(0, "configure", "configure", "postpaid", "IRR", 1000, null, null, 0, 0, 0, "test", null, null), "test");
        var contracts = new ContractPolicyStore(database, access, registry, ContractPolicyModule.FromOutputDirectory(), clock);
        var prices = new OrganizationUsagePriceStore(database, access, registry, clock);
        var values = new Dictionary<string, string?>
        {
            ["UsageMetering:Organization:Enabled"] = "true", ["UsageMetering:Organization:BindingSource"] = "contract-policy",
            ["UsageMetering:Organization:Environment"] = "development", ["UsageMetering:Organization:ProducerId"] = "organization-backend",
            ["UsageMetering:Organization:DeploymentId"] = "test-host", ["UsageMetering:Organization:ContractLinks:0:ConsumerTenantId"] = consumer.ToString("D"),
            ["UsageMetering:Organization:ContractLinks:0:ContractId"] = "commercial-contract", ["UsageMetering:Organization:ContractLinks:0:SubscriptionId"] = "commercial-subscription",
            ["UsageMetering:Organization:ContractLinks:0:OfferId"] = "commercial-offer", ["UsageMetering:Organization:ContractLinks:0:CentralSubscriptionId"] = subscription.Id.ToString("D"),
            ["UsageMetering:Organization:ContractLinks:0:CentralPricingPlanId"] = plan.Id.ToString("D"),
            // This intentionally incomplete legacy binding must never be read or used as a fallback.
            ["UsageMetering:Organization:Bindings:0:RateSnapshot:UnitPrice"] = "999"
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        Reject<ArgumentException>(() => new OrganizationUsageMetering(config, access, registry, clock), check,
            "contract-policy usage source requires actual durable projection and price dependencies");
        var resolver = new OrganizationUsageMetering(config, access, registry, clock, contracts, prices);
        var intake = new MeasuredUsageStore(database, accounting, resolver, clock);
        var organization = new OrganizationStore(root, access, database: database, metering: resolver, measuredUsage: intake);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new("sub", actor), new("tenant_permission", consumer + ":organization.write")], "test"));
        OrgCommand Command(string code) => new(organization.Read(consumer).Revision, "unit.save", null, code, code, null, "team", null, 1,
            null, null, null, null, "projection binding fixture", RequestId: Guid.NewGuid().ToString("D"));
        Reject<UnauthorizedAccessException>(() => organization.Execute(consumer, Command("missing-projection"), actor, principal), check,
            "contract-policy usage mode never falls back to configured price when projection is missing");
        var scope = new AllocationScope(consumer.ToString("D"), ["organization-fabric"], ["development"], ["organization"]);
        var projection = new ContractAccessProjection
        {
            SchemaVersion = "2.0.0", ProfileId = "fanasa.contract-policy/2", ContractId = "commercial-contract", ContractVersion = 7,
            OwnerTenantId = owner.ToString("D"), ConsumerTenantId = consumer.ToString("D"), ProductId = "organization-fabric",
            OfferId = "commercial-offer", SubscriptionId = "commercial-subscription", Status = "Active",
            ValidFrom = Utc(clock.Now.AddDays(-1)), ValidTo = Utc(clock.Now.AddDays(1)), EffectiveAt = Utc(clock.Now.AddMinutes(-1)),
            FeatureEntitlements = ["organization.unit.save"], EligibleRoles = ["ApiInvoker"], Seats = new(20, "active-membership", "consumer-product-subscription"),
            Limits = new()
            {
                AllocationId = "commercial-allocation", ConfigRevision = 1, Scope = scope, CanonicalScope = ContractProjectionValidator.CanonicalScope(scope),
                Meter = "api.request.admitted", Unit = "request", Rate = new("token-bucket", 5, 10), Quota = new("fixed-calendar-utc", "month", 1000)
            },
            ZoneConstraints = ["ir-private-1"], PricingReference = new("commercial-price", 1),
            Policy = new("organization-unit-save", 3, "organization-unit-save/1", "deny-overrides", [])
        };
        var producerBinding = new ContractProducerBinding("https://identity.invalid/realms/fanasa", "contracts-api", "contracts-client", "contracts.project");
        var producer = new ClaimsPrincipal(new ClaimsIdentity([new("iss", producerBinding.Issuer), new("aud", producerBinding.Audience),
            new("azp", producerBinding.ClientId), new("scope", producerBinding.RequiredScope)], "test-producer"));
        void Apply(ContractAccessProjection value, string id)
        {
            var envelope = new ContractProjectionEvent
            {
                SchemaVersion = "2.0.0", EventId = id, Producer = ContractEventAdapter.AuthorizedProducer, AggregateVersion = value.ContractVersion,
                OccurredAt = Utc(clock.Now), CorrelationId = "g-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Projection = value
            };
            var result = contracts.Apply(JsonSerializer.Serialize(envelope, ContractJson.Options), producer, producerBinding, () => true);
            if (result.Status != "applied") throw new Exception("Projection fixture rejected: " + string.Join(",", result.Errors));
        }
        Apply(projection, "usage-projection-seven");
        Reject<UnauthorizedAccessException>(() => organization.Execute(consumer, Command("missing-exact-price"), actor, principal), check,
            "trusted contract projection cannot bill an unpublished exact price reference");
        var rate = new UsageRateSnapshot("commercial-price", 1, clock.Now.AddDays(-1), clock.Now.AddHours(1), "12", "IRR", 0, "away-from-zero", "postpaid");
        prices.Publish(rate.RateId, 1, rate, "publisher", "version one");
        prices.Publish(rate.RateId, 2, rate with { RateVersion = 2, UnitPrice = "18" }, "publisher", "version two");
        var request = Command("projection-v1"); var first = organization.Execute(consumer, request, actor, principal);
        var usage = UsageEventV1.FromJson(database.All<MeasuredUsageOperation>("measured.operation").Single().EventJson);
        check(first.Revision == 1 && accounting.Read(consumer).Outstanding == 12 && usage.ContractId == projection.ContractId
            && usage.ContractVersion == 7 && usage.AllocationId == projection.Limits.AllocationId && usage.RateSnapshot == rate
            && usage.AllocationPeriodId == clock.Now.ToString("yyyy-MM", CultureInfo.InvariantCulture)
            && usage.Meter.Id == "organization.change" && usage.Meter.Unit == "change",
            "real authenticated central projection freezes its exact price allocation and UTC monthly billable event");
        projection = projection with { ContractVersion = 8, PricingReference = new(rate.RateId, 2) }; Apply(projection, "usage-projection-eight");
        organization.Execute(consumer, Command("projection-v2"), actor, principal);
        check(accounting.Read(consumer).Outstanding == 30 && database.All<MeasuredUsageOperation>("measured.operation")
            .Select(x => UsageEventV1.FromJson(x.EventJson).RateSnapshot.RateVersion).Order().SequenceEqual([1, 2]),
            "updated trusted projection selects its explicit newer price without implicit latest selection");
        clock.Now = originalTime.AddHours(2);
        check(organization.Execute(consumer, request, actor, principal).Revision == first.Revision && accounting.Read(consumer).Outstanding == 30,
            "current projected access permits original receipt replay after price snapshot expiry");
        Reject<UnauthorizedAccessException>(() => organization.Execute(consumer, Command("expired-projected-price"), actor, principal), check,
            "fresh projected operation rejects an expired exact price without fallback");
        clock.Now = originalTime;
        projection = projection with { ContractVersion = 9, Status = "Suspended" }; Apply(projection, "usage-projection-suspended");
        Reject<UnauthorizedAccessException>(() => organization.Execute(consumer, request, actor, principal), check,
            "current trusted contract suspension denies replay of previously charged organization response");
        projection = projection with { ContractVersion = 10, Status = "Active" }; Apply(projection, "usage-projection-renewed");
        var wrongLinkValues = new Dictionary<string, string?>(values) { ["UsageMetering:Organization:ContractLinks:0:CentralSubscriptionId"] = Guid.NewGuid().ToString("D") };
        var wrongResolver = new OrganizationUsageMetering(new ConfigurationBuilder().AddInMemoryCollection(wrongLinkValues).Build(), access, registry, clock, contracts, prices);
        var wrongStore = new OrganizationStore(root, access, database: database, metering: wrongResolver,
            measuredUsage: new MeasuredUsageStore(database, accounting, wrongResolver, clock));
        Reject<UnauthorizedAccessException>(() => wrongStore.Execute(consumer, request, actor, principal), check,
            "opaque commercial subscription requires exact explicit central subscription link on replay");
        var allocation = projection.Limits;
        database.Transaction(() =>
        {
            database.Put("contract-policy.allocation", allocation.AllocationId, allocation with { Quota = allocation.Quota with { Requests = 999 } }); return true;
        });
        Reject<UnauthorizedAccessException>(() => organization.Execute(consumer, request, actor, principal), check,
            "central allocation revision and complete projection allocation must match before replay");
        database.Transaction(() => { database.Put("contract-policy.allocation", allocation.AllocationId, allocation); return true; });
        projection = projection with { ContractVersion = (long)int.MaxValue + 1 }; Apply(projection, "usage-projection-version-too-large");
        Reject<UnauthorizedAccessException>(() => organization.Execute(consumer, Command("oversized-contract-version"), actor, principal), check,
            "contract projection version outside UsageEvent integer range cannot be truncated for billing");
        projection = projection with { ContractVersion = projection.ContractVersion + 1, PricingReference = new(rate.RateId, (long)int.MaxValue + 1) };
        Apply(projection, "usage-projection-price-too-large");
        Reject<UnauthorizedAccessException>(() => organization.Execute(consumer, Command("oversized-price-version"), actor, principal), check,
            "contract price version outside UsageEvent integer range cannot be truncated or defaulted");
        check(organization.Read(consumer).Revision == 2 && accounting.Read(consumer).Outstanding == 30
            && database.All<MeasuredUsageOperation>("measured.operation").Length == 2,
            "projected binding failures preserve organization state financial ledger and completed usage count");
    }

    private static string Utc(DateTimeOffset time) => time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    private static void Reject<T>(Action action, Action<bool, string> check, string label) where T : Exception
    { try { action(); } catch (T) { check(true, label); return; } throw new Exception("Expected rejection: " + label); }
    private sealed class BindingClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
