using System.Globalization;
using System.Text.Json;
using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.ContractPolicies;
using Fanasa.AccessManagement.Web.Persistence;
using Fanasa.AccessManagement.Web.Platform;
using Fanasa.ContractPolicy;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Fanasa.HrReadHost.Tests;

/// <summary>Real host store over isolated SQLite; profile coexistence never implies live authority.</summary>
public static class CompatibilityTests
{
    public static int Run()
    {
        var count = 0;
        void Check(bool value, string name)
        { count++; if (!value) throw new InvalidOperationException("HR3 legacy compatibility test failed: " + name); }

        using (var f = new Fixture())
        {
            Check(f.Module.Projections.Validate(f.Legacy).Count == 0, "synthetic v2 command projection is valid");
            Check(f.Module.Projections.Validate(f.HrRead).Count == 0, "synthetic HR3 read projection is valid");
            Check(f.Store.Find(f.Consumer) is null, "empty legacy selection remains empty");
            Check(f.Store.Find(Guid.Empty) is null, "empty consumer remains denied");
            f.Put(f.Legacy); var before = f.Snapshot();
            Check(Equal(f.Store.Find(f.Consumer), f.Legacy), "legacy v2 selection is unchanged alone");
            Check(f.Snapshot() == before, "baseline selection does not mutate Fabric or outbox");
            f.Put(f.HrRead); before = f.Snapshot();
            Check(Equal(f.Store.Find(f.Consumer), f.Legacy), "same consumer HR3 contract cannot make its v2 command selection ambiguous");
            Check(f.Snapshot() == before, "coexistence lookup writes no records or outbox");
            var rejected = false;
            try { f.Store.Read(f.HrRead.ContractId); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, "legacy direct reader retains its v2-only boundary");
            Check(Equal(f.Store.Read(f.Legacy.ContractId)!.Projection, f.Legacy), "legacy direct reader still returns its v2 contract");
            f.Put(f.HrRead with { ContractId = "hr-other-consumer", ConsumerTenantId = f.Other.ToString("D"),
                Limits = ForConsumer(f.HrRead.Limits, f.Other) });
            Check(Equal(f.Store.Find(f.Consumer), f.Legacy), "unrelated valid HR3 projection does not affect legacy selection");
            Check(f.Store.Find(f.Other) is null, "HR3-only consumer is not selected by legacy v2 lookup");
            f.Put(f.Legacy with { ContractId = "legacy-duplicate" });
            Check(f.Store.Find(f.Consumer) is null, "two actual v2 contracts remain ambiguous and fail closed");
        }

        void InvalidState(Func<Fixture, ProjectionState> invalid, string name)
        {
            using var f = new Fixture(); f.Put(f.Legacy); f.Put(f.HrRead);
            f.PutState(f.HrRead.ContractId, invalid(f)); var before = f.Snapshot(); var rejected = false;
            try { _ = f.Store.Find(f.Consumer); } catch (InvalidDataException) { rejected = true; }
            Check(rejected, name + " cannot be hidden by the legacy profile filter");
            Check(f.Snapshot() == before && !f.Database.IsInTransaction, name + " leaves Fabric and outbox unchanged");
        }
        InvalidState(f => State(f.HrRead with { ProfileId = "fanasa.contract-policy/2" }), "mismatched HR3 profile");
        InvalidState(f => State(f.HrRead with { SchemaVersion = "4.0.0" }), "unknown schema");
        InvalidState(f => State(f.HrRead with { ProductId = "another-product" }), "wrong HR3 product");
        InvalidState(f => State(f.HrRead with { Ownership = null }), "missing HR3 registry ownership");
        InvalidState(f => State(f.HrRead with { ConsumerTenantId = f.Other.ToString("D") }), "unrelated consumer with invalid allocation binding");
        InvalidState(f => new(f.HrRead, [new("", new string('a', 64))]), "malformed receipt identifier");
        InvalidState(f => new(f.HrRead, [new("event-valid", new string('A', 64))]), "malformed receipt fingerprint");
        InvalidState(f => new(f.HrRead, [new("event-valid", new string('a', 64)), new("event-valid", new string('a', 64))]), "duplicate receipt");
        InvalidState(f => new(f.HrRead, [null!]), "null receipt");
        InvalidState(_ => new(null!, []), "null projection");
        InvalidState(f => State(f.Legacy with { ContractId = f.HrRead.ContractId, ProfileId = HrReadProfile.ProfileId }), "mismatched v2 profile");
        return count;
    }

    private static ProjectionState State(ContractAccessProjection projection) => new(projection, [new("event-valid", new string('a', 64))]);
    private static bool Equal<T>(T first, T second) => JsonSerializer.Serialize(first, ContractJson.Options) == JsonSerializer.Serialize(second, ContractJson.Options);
    private static AllocationLimits ForConsumer(AllocationLimits limits, Guid consumer)
    {
        var scope = limits.Scope with { ConsumerTenantId = consumer.ToString("D") };
        return limits with { Scope = scope, CanonicalScope = ContractProjectionValidator.CanonicalScope(scope) };
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "fanasa-hr3-compatibility-" + Guid.NewGuid().ToString("N"));
        public Fixture()
        {
            Database = new(Path.Combine(directory, "fabric.db"));
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["Platform:RegistryPath"] = Path.Combine(directory, "registry.db") }).Build();
            var registry = new PlatformRegistry(configuration);
            var access = new PersistentAccessManagement(Database, new InMemoryAccessManagement(registry));
            Consumer = access.CreateTenant("synthetic-compatibility-consumer", "Synthetic compatibility consumer").Id;
            Other = access.CreateTenant("synthetic-compatibility-other", "Synthetic compatibility other").Id;
            Store = new(Database, access, registry, Module, TimeProvider.System);
            Legacy = Projection(hrRead: false); HrRead = Projection(hrRead: true);
        }
        public FabricDatabase Database { get; }
        public ContractPolicyModule Module { get; } = ContractPolicyModule.FromOutputDirectory();
        public ContractPolicyStore Store { get; }
        public Guid Consumer { get; }
        public Guid Other { get; }
        public ContractAccessProjection Legacy { get; }
        public ContractAccessProjection HrRead { get; }
        public void Put(ContractAccessProjection value) => PutState(value.ContractId, State(value));
        public void PutState(string key, ProjectionState value) => Database.Transaction(() => { Database.Put("contract-policy.projection", key, value); return true; });
        public string Snapshot() => JsonSerializer.Serialize(Database.DocumentPage("contract-policy.projection"), ContractJson.Options)
            + JsonSerializer.Serialize(Database.Outbox(), ContractJson.Options);

        private ContractAccessProjection Projection(bool hrRead)
        {
            var now = new DateTimeOffset(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
            var scope = new AllocationScope(Consumer.ToString("D"), ["organization-fabric"], ["development"], [hrRead ? HrReadProfile.OperationGroup : "organization"]);
            return new()
            {
                SchemaVersion = hrRead ? HrReadProfile.SchemaVersion : "2.0.0", ProfileId = hrRead ? HrReadProfile.ProfileId : "fanasa.contract-policy/2",
                ContractId = hrRead ? "hr-read-contract" : "legacy-command-contract", ContractVersion = 1,
                OwnerTenantId = hrRead ? null : "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                Ownership = hrRead ? new("Platform", "platform", "55555555-5555-4555-8555-555555555555") : null,
                ConsumerTenantId = Consumer.ToString("D"), ProductId = "organization-fabric", OfferId = "synthetic-offer", SubscriptionId = "synthetic-subscription", Status = "Active",
                ValidFrom = Format(now.AddHours(-1)), ValidTo = Format(now.AddHours(1)), EffectiveAt = Format(now.AddHours(-1)),
                FeatureEntitlements = hrRead ? [HrReadProfile.ChartAction, HrReadProfile.HistoryAction] : ["organization.unit.save"],
                EligibleRoles = [hrRead ? HrReadProfile.Role : "ApiInvoker"], Seats = new(25, "active-membership", "consumer-product-subscription"),
                Limits = new() { AllocationId = hrRead ? "hr-allocation" : "legacy-allocation", ConfigRevision = 1, Scope = scope,
                    CanonicalScope = ContractProjectionValidator.CanonicalScope(scope), Meter = "api.request.admitted", Unit = "request",
                    Rate = new("token-bucket", 5, 10), Quota = new("fixed-calendar-utc", "month", 1000) },
                ZoneConstraints = ["internal"], PricingReference = new("synthetic-price", 1),
                Policy = new(hrRead ? "hr-policy" : "legacy-policy", 1, hrRead ? HrReadProfile.TemplateId : "organization-unit-save/1", "deny-overrides", [])
            };
        }
        public void Dispose()
        {
            Database.Dispose(); SqliteConnection.ClearAllPools(); var absolute = Path.GetFullPath(directory);
            if (absolute.StartsWith(Path.GetFullPath(Path.GetTempPath()) + "fanasa-hr3-compatibility-", StringComparison.OrdinalIgnoreCase) && Directory.Exists(absolute))
                Directory.Delete(absolute, recursive: true);
        }
    }
    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
