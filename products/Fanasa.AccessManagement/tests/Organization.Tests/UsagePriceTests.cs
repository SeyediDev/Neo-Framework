using System.Text.Json;
using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Metering;
using Fanasa.AccessManagement.Web.Persistence;
using Fanasa.AccessManagement.Web.Platform;
using Microsoft.Extensions.Configuration;

static class UsagePriceTests
{
    public static void Run(string directory, Action<bool, string> check)
    {
        var root = Path.Combine(directory, "usage-prices"); Directory.CreateDirectory(root);
        var fabricPath = Path.Combine(root, "fabric.db"); var registryPath = Path.Combine(root, "registry.db");
        var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Platform:RegistryPath"] = registryPath }).Build();
        var registry = new PlatformRegistry(settings); using var database = new FabricDatabase(fabricPath);
        var access = new PersistentAccessManagement(database, new InMemoryAccessManagement(registry));
        var owner = access.GetTenants().Single().Id; var consumer = access.CreateTenant("price-consumer", "Price Consumer").Id;
        registry.SetTenantSource(access.GetTenants); registry.SetMembershipSource(access.GetUsers);
        registry.Register(new(Guid.NewGuid(), "organization-fabric", "Organization", "fanasa-organization-fabric-api", "fanasa.ayin", null, null, owner, "fanasa"), "test");
        var clock = new PriceClock(new(2026, 10, 10, 8, 0, 0, TimeSpan.Zero));
        var store = new OrganizationUsagePriceStore(database, access, registry, clock);
        var rate = new UsageRateSnapshot("organization-change-rate", 7, clock.Now.AddDays(-1), clock.Now.AddHours(1), "12", "IRR", 0, "away-from-zero", "postpaid");
        var first = store.Publish(rate.RateId, rate.RateVersion, rate, "publisher", "approved version seven");
        check(first.PriceId == rate.RateId && first.PriceVersion == 7 && first.OwnerTenantId == owner
            && first.ProductKey == "organization-fabric" && first.MeterId == "organization.change" && first.MeterVersion == "1"
            && first.MeterUnit == "change" && first.RateSnapshot == rate && first.RecordedAt == clock.Now,
            "durable usage price binds exact version snapshot meter and central owner");
        var outbox = database.Outbox().Length;
        clock.Now = clock.Now.AddMinutes(1);
        check(store.Publish(rate.RateId, 7, rate, "another-publisher", "exact duplicate retry") == first
            && database.Outbox().Length == outbox && database.All<OrganizationUsagePrice>("organization-usage.price").Length == 1,
            "exact price publication replay returns original immutable audit without duplicate outbox");
        Reject<InvalidOperationException>(() => store.Publish(rate.RateId, 7, rate with { UnitPrice = "18" }, "publisher", "attempted overwrite"), check,
            "one price ID and version cannot identify changed numeric snapshot");
        Reject<InvalidOperationException>(() => store.Publish(rate.RateId, 7, rate with { EffectiveTo = rate.EffectiveTo.AddHours(1) }, "publisher", "attempted interval overwrite"), check,
            "one price ID and version cannot identify changed effective window");
        check(store.Read(rate.RateId, 7) == first && database.Outbox().Length == outbox,
            "conflicting price publication leaves original data and audit unchanged");
        var next = rate with { RateVersion = 8, UnitPrice = "18", EffectiveFrom = rate.EffectiveTo, EffectiveTo = rate.EffectiveTo.AddDays(1) };
        store.Publish(next.RateId, next.RateVersion, next, "publisher", "approved version eight");
        check(store.Resolve(rate.RateId, 7, owner, rate.EffectiveFrom) == rate
            && store.Resolve(next.RateId, 8, owner, next.EffectiveFrom) == next,
            "usage prices resolve the requested historical version at inclusive interval start");
        check(store.Read(rate.RateId, 9) is null && store.Read("missing-price", 7) is null,
            "usage price lookup has no implicit latest version or synthetic default");
        Reject<UnauthorizedAccessException>(() => store.Resolve(rate.RateId, 9, owner, rate.EffectiveFrom), check,
            "usage resolution never substitutes latest price for a missing exact version");
        Reject<UnauthorizedAccessException>(() => store.Resolve(rate.RateId, 7, consumer, rate.EffectiveFrom), check,
            "usage resolution rejects consumer substitution for authoritative owner");
        Reject<UnauthorizedAccessException>(() => store.Resolve(rate.RateId, 7, owner, rate.EffectiveFrom.AddTicks(-1)), check,
            "usage resolution rejects admission before exact price effective interval");
        Reject<UnauthorizedAccessException>(() => store.Resolve(rate.RateId, 7, owner, rate.EffectiveTo), check,
            "usage resolution treats the exact price interval end as exclusive");
        Reject<ArgumentException>(() => store.Resolve(rate.RateId, 7, owner, rate.EffectiveFrom.ToOffset(TimeSpan.FromHours(1))), check,
            "usage resolution admission timestamp must be UTC");
        var overlapping = rate with { RateVersion = 10, UnitPrice = "24" };
        store.Publish(overlapping.RateId, overlapping.RateVersion, overlapping, "publisher", "explicit independent version");
        check(store.Resolve(rate.RateId, 7, owner, rate.EffectiveFrom) == rate
            && store.Resolve(rate.RateId, 10, owner, rate.EffectiveFrom) == overlapping,
            "overlapping price windows resolve only the explicit immutable version");
        Reject<ArgumentException>(() => store.Publish("different-price-id", 7, rate, "publisher", "mismatch"), check,
            "price reference ID must match frozen rate identity");
        Reject<ArgumentException>(() => store.Publish(rate.RateId, 8, rate, "publisher", "mismatch"), check,
            "price reference version must match frozen rate version");
        Reject<ArgumentException>(() => store.Publish(rate.RateId, 0, rate with { RateVersion = 0 }, "publisher", "zero version"), check,
            "usage price versions must be positive");
        Reject<ArgumentException>(() => store.Publish("invalid-rate", 1, rate with { RateId = "invalid-rate", RateVersion = 1, EffectiveTo = rate.EffectiveFrom }, "publisher", "empty interval"), check,
            "usage price interval must have positive duration");
        Reject<ArgumentException>(() => store.Publish("invalid-rate", 1, rate with { RateId = "invalid-rate", RateVersion = 1, EffectiveFrom = rate.EffectiveFrom.ToOffset(TimeSpan.FromHours(1)) }, "publisher", "non UTC"), check,
            "usage price interval timestamps require UTC");
        Reject<ArgumentException>(() => store.Publish("invalid-rate", 1, rate with { RateId = "invalid-rate", RateVersion = 1, Currency = "USD", MinorUnitScale = 0 }, "publisher", "currency scale"), check,
            "usage price scale must match currency");
        var prepaidRate = rate with { RateId = "payg-rate", RateVersion = 1, BillingMode = "payg" };
        store.Publish("payg-rate", 1, prepaidRate, "publisher", "explicit payg rate");
        check(store.Resolve("payg-rate", 1, owner, rate.EffectiveFrom) == prepaidRate,
            "usage price retains an explicit immutable PAYG rate");
        Reject<ArgumentException>(() => store.Publish("invalid-rate", 1, rate with { RateId = "invalid-rate", RateVersion = 1, BillingMode = "unsupported" }, "publisher", "unsupported mode"), check,
            "usage price rejects unknown billing modes");
        Reject<ArgumentException>(() => store.Publish("invalid-rate", 1, rate with { RateId = "invalid-rate", RateVersion = 1, UnitPrice = "-12" }, "publisher", "negative price"), check,
            "usage price decimal cannot be negative");
        Reject<ArgumentException>(() => store.Publish("invalid-rate", 1, rate with { RateId = "invalid-rate", RateVersion = 1, UnitPrice = "12.1234567" }, "publisher", "precision"), check,
            "usage price decimal precision is bounded");
        Reject<ArgumentException>(() => store.Publish("invalid-rate", 1, rate with { RateId = "invalid-rate", RateVersion = 1, Rounding = "to-even" }, "publisher", "rounding"), check,
            "usage price rounding method is version-bound");
        Reject<ArgumentException>(() => store.Publish("invalid-rate", 1, rate with { RateId = "invalid-rate", RateVersion = 1 }, "", "no actor"), check,
            "usage price publication requires an audit actor");
        Reject<ArgumentException>(() => store.Publish("invalid-rate", 1, rate with { RateId = "invalid-rate", RateVersion = 1 }, "publisher", ""), check,
            "usage price publication requires an audit reason");
        var beforeRollback = database.Outbox().Length;
        Reject<InvalidOperationException>(() => database.Transaction<bool>(() =>
        {
            store.Publish("rolled-back-price", 1, rate with { RateId = "rolled-back-price", RateVersion = 1 }, "publisher", "outer rollback");
            throw new InvalidOperationException("failure before commit");
        }), check, "usage price publication outer failure injected");
        check(store.Read("rolled-back-price", 1) is null && database.Outbox().Length == beforeRollback,
            "usage price and publication audit rollback in the same Fabric transaction");
        using var reopened = new FabricDatabase(fabricPath);
        var reopenedAccess = new PersistentAccessManagement(reopened, new InMemoryAccessManagement(registry));
        var restarted = new OrganizationUsagePriceStore(reopened, reopenedAccess, registry, clock);
        check(restarted.Read(rate.RateId, 7) == first && restarted.Resolve(rate.RateId, 7, owner, rate.EffectiveFrom) == rate,
            "exact immutable historical usage price survives store and Fabric restart");
        var bundle = Path.Combine(root, "bundle"); FabricRecovery.Backup(fabricPath, registryPath, bundle, true);
        var restored = Path.Combine(root, "restored"); FabricRecovery.Restore(bundle, restored, true);
        using var restoredDatabase = new FabricDatabase(Path.Combine(restored, "fabric.db"));
        var restoredSettings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Platform:RegistryPath"] = Path.Combine(restored, "platform-registry.db") }).Build();
        var restoredRegistry = new PlatformRegistry(restoredSettings);
        var restoredAccess = new PersistentAccessManagement(restoredDatabase, new InMemoryAccessManagement(restoredRegistry));
        restoredRegistry.SetTenantSource(restoredAccess.GetTenants); restoredRegistry.SetMembershipSource(restoredAccess.GetUsers);
        var restoredStore = new OrganizationUsagePriceStore(restoredDatabase, restoredAccess, restoredRegistry, clock);
        check(restoredStore.Resolve(rate.RateId, 7, owner, rate.EffectiveFrom) == rate
            && restoredStore.Resolve(next.RateId, 8, owner, next.EffectiveFrom) == next
            && restoredDatabase.Outbox().Length == database.Outbox().Length,
            "actual recovery bundle restores every immutable usage price and publication audit");
        check(consumer != owner && JsonSerializer.Serialize(store.Read(rate.RateId, 7)) == JsonSerializer.Serialize(first),
            "usage price ownership remains the central product owner rather than the billed consumer");
        var ownerTenant = access.GetTenants().Single(x => x.Id == owner);
        database.Transaction(() => { database.Put("tenant", owner.ToString(), ownerTenant with { IsActive = false }); return true; });
        Reject<UnauthorizedAccessException>(() => store.Resolve(rate.RateId, 7, owner, rate.EffectiveFrom), check,
            "usage price resolution requires a currently active authoritative owner");
        Reject<UnauthorizedAccessException>(() => store.Publish("inactive-owner-price", 1, rate with { RateId = "inactive-owner-price", RateVersion = 1 }, "publisher", "inactive owner"), check,
            "usage price publication cannot create prices for an inactive central owner");
        database.Transaction(() => { database.Put("tenant", owner.ToString(), ownerTenant); return true; });
        var corruptKey = UsageEventV1.Hash(JsonSerializer.Serialize(new[] { first.PriceId, first.PriceVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) }));
        database.Transaction(() => { database.Put("organization-usage.price", corruptKey, first with { MeterUnit = "hour" }); return true; });
        Reject<InvalidDataException>(() => store.Read(rate.RateId, 7), check,
            "corrupted durable usage price cannot bypass stored meter validation");
        UsageContractBindingTests.Run(directory, check);
    }

    private static void Reject<T>(Action action, Action<bool, string> check, string label) where T : Exception
    { try { action(); } catch (T) { check(true, label); return; } throw new Exception("Expected rejection: " + label); }
    private sealed class PriceClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
