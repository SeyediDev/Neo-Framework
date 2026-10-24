using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Fanasa.AccessManagement.Web.Accounting;
using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Metering;
using Fanasa.AccessManagement.Web.Organization;
using Fanasa.AccessManagement.Web.Persistence;
using Fanasa.AccessManagement.Web.Platform;
using Microsoft.Extensions.Configuration;

static class UsageAccountingTests
{
    private static readonly DateTimeOffset HistoricalTime = new(2026, 10, 10, 8, 0, 0, TimeSpan.Zero);

    public static void Run(string directory, Action<bool, string> check)
    {
        WireContract(directory, check);
        HistoricalReplay(directory, check);
        TransactionsAndConcurrency(directory, check);
        PendingAccounting(directory, check);
        BackupRecovery(directory, check);
        OrganizationIntegration(directory, check);
    }

    private static void WireContract(string directory, Action<bool, string> check)
    {
        using var fixture = new UsageFixture(directory, "wire");
        var message = fixture.Event("wire"); var wire = message.ToWireJson();
        var parsed = UsageEventV1.FromJson(wire);
        check(parsed == message && parsed.Amount == 12 && parsed.BusinessKey.StartsWith("measured:v1:", StringComparison.Ordinal),
            "UsageEvent 1.0 strict wire roundtrip preserves the frozen snapshot");
        Reject<ArgumentException>(() => UsageEventV1.FromJson(wire.Replace("\"schemaVersion\":\"1.0\"", "\"schemaVersion\":\"1.0\",\"schemaVersion\":\"1.0\"", StringComparison.Ordinal)), check,
            "usage parser rejects duplicate root properties");
        Reject<ArgumentException>(() => UsageEventV1.FromJson(wire.Replace("\"quantity\":\"1\"", "\"quantity\":\"1\",\"quantity\":\"1\"", StringComparison.Ordinal)), check,
            "usage parser rejects duplicate nested properties");
        Reject<ArgumentException>(() => UsageEventV1.FromJson(wire.Insert(1, "\"extra\":true,")), check, "usage parser rejects unknown properties");
        Reject<ArgumentException>(() => UsageEventV1.FromJson(wire.Replace("\"schemaVersion\":\"1.0\",", "", StringComparison.Ordinal)), check, "usage parser requires schema version");
        Reject<ArgumentException>(() => UsageEventV1.FromJson(wire.Replace("\"quantity\":\"1\"", "\"quantity\":1", StringComparison.Ordinal)), check, "usage quantity must be a decimal string");
        Reject<ArgumentException>(() => UsageEventV1.FromJson(wire.Replace("\"unitPrice\":\"12\"", "\"unitPrice\":\"1e1\"", StringComparison.Ordinal)), check, "usage parser rejects exponent prices");
        Reject<ArgumentException>(() => UsageEventV1.FromJson(wire.Replace("\"unitPrice\":\"12\"", "\"unitPrice\":\"-1\"", StringComparison.Ordinal)), check, "usage parser rejects negative prices");
        Reject<ArgumentException>(() => UsageEventV1.FromJson(wire.Replace("Z\"", "+00:00\"", StringComparison.Ordinal)), check, "usage timestamps require explicit UTC Z");
        Reject<ArgumentException>(() => UsageEventV1.FromJson(wire.Replace("\"occurredAt\":\"2026-10-10T06:31:00.0000000Z\"", "\"occurredAt\":123", StringComparison.Ordinal)), check, "usage timestamps require string tokens");
        Reject<ArgumentException>(() => (message with { ProducedAt = message.OccurredAt.AddSeconds(-1) }).Validate(), check, "usage production cannot precede completion");
        Reject<ArgumentException>(() => (message with { Operation = message.Operation with { AcceptedAt = message.RateSnapshot.EffectiveTo } }).Validate(), check,
            "usage admission must be inside the frozen rate window");
        Reject<ArgumentException>(() => (message with { Meter = message.Meter with { Quantity = "2" } }).Validate(), check, "pilot usage requires exactly one committed change");
        Reject<ArgumentException>(() => (message with { Meter = message.Meter with { Unit = "hour" } }).Validate(), check, "usage meter unit is version-bound");
        Reject<ArgumentException>(() => (message with { RateSnapshot = message.RateSnapshot with { Currency = "GBP" } }).Validate(), check, "usage currency must be supported");
        Reject<ArgumentException>(() => (message with { RateSnapshot = message.RateSnapshot with { MinorUnitScale = 2 } }).Validate(), check, "IRR usage requires zero minor digits");
        Reject<ArgumentException>(() => (message with { RateSnapshot = message.RateSnapshot with { UnitPrice = "0.1234567" } }).Validate(), check, "usage price fractional precision is bounded");
        var rounded = message with { RateSnapshot = message.RateSnapshot with { Currency = "USD", MinorUnitScale = 2, UnitPrice = "12.345" } };
        rounded.Validate(); check(rounded.Amount == 12.35m, "usage prices round away from zero at the currency scale");
        var alias = Alias(message);
        check(alias.EventKey != message.EventKey && alias.EventFingerprint != message.EventFingerprint
            && alias.BusinessKey == message.BusinessKey && alias.OperationFingerprint == message.OperationFingerprint,
            "delivery metadata changes retain one logical operation identity");
    }

    private static void HistoricalReplay(string directory, Action<bool, string> check)
    {
        using var fixture = new UsageFixture(directory, "historical");
        var message = fixture.Event("historical");
        var result = fixture.Store.Accept(message);
        var charge = fixture.Billing.Read(fixture.Tenant).Account.Entries.Single(x => x.Kind == "usage");
        check(result.Status == "committed" && result.Amount == 12 && charge.UnitPrice == 12 && charge.Amount == 12
            && charge.At == fixture.Clock.Now && charge.UsageOccurredAt == message.OccurredAt,
            "delayed usage bills historical v7 price and records registration plus occurrence time");
        check(fixture.Billing.Read(fixture.Tenant).Account.Tariffs.Single().UnitPrice == 18, "historical intake leaves current v8 tariff unchanged");
        check(charge.Id == UsageEventV1.StableGuid(message.BusinessKey + ":charge"), "measured ledger identity is stable");
        var count = fixture.Database.Outbox().Length; var revision = fixture.Billing.Read(fixture.Tenant).Account.Revision;
        var originalLedger = JsonSerializer.Serialize(fixture.Billing.Read(fixture.Tenant).Account);
        fixture.Validator.Allow = false;
        fixture.Store.Accept(UsageEventV1.FromJson(message.ToWireJson()));
        var alias = Alias(message); fixture.Store.Accept(alias);
        check(fixture.Billing.Read(fixture.Tenant).Account.Revision == revision && fixture.Database.Outbox().Length == count
            && fixture.Database.All<MeasuredEventReceipt>("measured.event").Length == 2
            && fixture.Database.All<MeasuredUsageOperation>("measured.operation").Length == 1 && fixture.Validator.Calls == 1,
            "committed replay and regenerated delivery debit once without revalidating rates");
        Reject<InvalidOperationException>(() => fixture.Store.Accept(message with { Operation = message.Operation with { ActorId = "other" } }), check,
            "same usage event rejects a changed actor");
        Reject<InvalidOperationException>(() => fixture.Store.Accept(Alias(message) with { Evidence = message.Evidence with { ResultDigest = UsageEventV1.Hash("changed") } }), check,
            "same operation rejects changed business evidence");
        Reject<InvalidOperationException>(() => fixture.Store.Accept(Alias(message) with { RateSnapshot = message.RateSnapshot with { UnitPrice = "18" } }), check,
            "same operation cannot be repriced");
        check(JsonSerializer.Serialize(fixture.Billing.Read(fixture.Tenant).Account) == originalLedger && fixture.Database.Outbox().Length == count,
            "usage identity conflicts leave ledger and outbox unchanged");
        var committed = fixture.Database.Outbox().Single(x => x.Kind == "UsageCommitted");
        using var payload = JsonDocument.Parse(committed.Payload);
        var retained = UsageEventV1.FromJson(payload.RootElement.GetProperty("Usage").GetRawText());
        check(committed.Key == message.BusinessKey + ":committed" && retained == message
            && payload.RootElement.GetProperty("TenantId").GetGuid() == fixture.Tenant,
            "usage outbox retains the full original schema and tenant for recovery");
    }

    private static void TransactionsAndConcurrency(string directory, Action<bool, string> check)
    {
        using var fixture = new UsageFixture(directory, "transactions"); var message = fixture.Event("rollback");
        var count = fixture.Database.Outbox().Length;
        Reject<InvalidOperationException>(() => fixture.Database.Transaction<bool>(() =>
        {
            fixture.Store.Accept(message); fixture.Database.Put("test.business", "rollback", "staged");
            throw new InvalidOperationException("failure before outer commit");
        }), check, "measured outer transaction failure injected");
        check(fixture.Billing.Read(fixture.Tenant).Outstanding == 0 && fixture.Database.Outbox().Length == count
            && fixture.Database.All<MeasuredUsageOperation>("measured.operation").Length == 0
            && fixture.Database.All<MeasuredEventReceipt>("measured.event").Length == 0
            && fixture.Database.Read<string>("test.business", "rollback") is null,
            "business evidence usage journal receipts ledger and outbox rollback together");
        fixture.Store.Accept(message);
        using var otherDatabase = new FabricDatabase(fixture.FabricPath);
        var otherAccess = new PersistentAccessManagement(otherDatabase, new InMemoryAccessManagement(fixture.Registry));
        var otherBilling = new AccountingStore(fixture.Root, otherAccess, fixture.Clock, otherDatabase);
        var otherStore = new MeasuredUsageStore(otherDatabase, otherBilling, new TestValidator(), fixture.Clock);
        var chargedId = fixture.Billing.Read(fixture.Tenant).Account.Entries.Single(x => x.Kind == "usage").Id;
        var committedCount = fixture.Database.Outbox().Length; otherStore.Accept(message);
        check(otherBilling.Read(fixture.Tenant).Outstanding == 12 && otherDatabase.Outbox().Length == committedCount
            && otherBilling.Read(fixture.Tenant).Account.Entries.Single(x => x.Kind == "usage").Id == chargedId,
            "reopen and replay after a lost acknowledgement preserves one charge");
        var race = fixture.Event("same-request-race");
        Parallel.For(0, 2, i => (i == 0 ? fixture.Store : otherStore).Accept(race));
        check(otherBilling.Read(fixture.Tenant).Account.Entries.Count(x => x.Kind == "usage" && x.Key == race.BusinessKey) == 1
            && otherDatabase.Outbox().Count(x => x.Kind == "UsageCommitted" && x.Key == race.BusinessKey + ":committed") == 1,
            "two Fabric connections racing one event create one ledger debit and usage outbox");

        using var limited = new UsageFixture(directory, "credit-race", 12);
        using var limitedOtherDatabase = new FabricDatabase(limited.FabricPath);
        var limitedOtherAccess = new PersistentAccessManagement(limitedOtherDatabase, new InMemoryAccessManagement(limited.Registry));
        var limitedOtherBilling = new AccountingStore(limited.Root, limitedOtherAccess, limited.Clock, limitedOtherDatabase);
        var limitedOtherStore = new MeasuredUsageStore(limitedOtherDatabase, limitedOtherBilling, new TestValidator(), limited.Clock);
        var results = new ConcurrentBag<bool>();
        Parallel.For(0, 2, i =>
        {
            try { (i == 0 ? limited.Store : limitedOtherStore).Accept(limited.Event("credit-race-" + i)); results.Add(true); }
            catch (MeasuredAccountingException) { results.Add(false); }
        });
        check(results.Count(x => x) == 1 && results.Count(x => !x) == 1 && limited.Billing.Read(limited.Tenant).Outstanding == 12
            && limited.Billing.Read(limited.Tenant).AvailableCredit == 0
            && limited.Database.All<MeasuredUsageOperation>("measured.operation").Length == 1,
            "concurrent distinct operations cannot overspend postpaid credit or retain rejected local evidence");
        Reject<ArgumentException>(() => new MeasuredUsageStore(limited.Database, limitedOtherBilling, new TestValidator()), check,
            "measured intake rejects accounting on a separate Fabric instance");
    }

    private static void PendingAccounting(string directory, Action<bool, string> check)
    {
        using var fixture = new UsageFixture(directory, "pending", 0); var original = fixture.Event("pending"); var alias = Alias(original);
        var first = fixture.Store.Accept(original, retainPending: true); var repeated = fixture.Store.Accept(alias, retainPending: true);
        var pending = fixture.Database.Read<MeasuredUsageOperation>("measured.operation", original.BusinessKey)!;
        check(first.Status == "accounting-pending" && repeated.Status == "accounting-pending" && first.Error is not null
            && pending.EventJson == original.ToWireJson() && fixture.Billing.Read(fixture.Tenant).Outstanding == 0
            && fixture.Database.Outbox().Count(x => x.Kind == "UsageAccountingPending") == 1,
            "completed unfunded usage retains original evidence and one pending outbox across aliases");
        var count = fixture.Database.Outbox().Length;
        Reject<InvalidOperationException>(() => fixture.Store.Accept(Alias(original) with { RateSnapshot = original.RateSnapshot with { UnitPrice = "18" } }, true), check,
            "pending logical identity rejects conflicting historical prices");
        check(fixture.Database.Read<MeasuredUsageOperation>("measured.operation", original.BusinessKey) == pending
            && fixture.Database.Outbox().Length == count, "pending conflict cannot replace recovery evidence");
        fixture.Validator.Allow = false;
        Reject<UnauthorizedAccessException>(() => fixture.Store.Accept(fixture.Event("untrusted"), true), check,
            "invalid trusted bindings never become accounting-pending");
        var calls = fixture.Validator.Calls;
        fixture.ConfigureCredit(100, "grant-credit");
        check(fixture.Store.Accept(alias).Status == "committed" && fixture.Store.Accept(original).Status == "committed"
            && fixture.Store.Accept(Alias(original)).Status == "committed" && fixture.Validator.Calls == calls
            && fixture.Billing.Read(fixture.Tenant).Outstanding == 12
            && fixture.Database.Outbox().Count(x => x.Kind == "UsageCommitted") == 1,
            "pending retry commits original rate despite changed validation and all aliases resolve once");
        check(fixture.Database.All<MeasuredUsageOperation>("measured.operation").Single().EventJson == original.ToWireJson(),
            "pending completion preserves the first received complete event");

        using var invoiced = new UsageFixture(directory, "issued-period"); invoiced.Clock.Now = HistoricalTime.AddMonths(1);
        invoiced.Bill("invoice", "october-invoice", period: "2026-10"); var late = invoiced.Event("late-after-invoice");
        var account = JsonSerializer.Serialize(invoiced.Billing.Read(invoiced.Tenant).Account);
        check(invoiced.Store.Accept(late, true).Status == "accounting-pending"
            && invoiced.Store.Accept(late, true).Status == "accounting-pending"
            && JsonSerializer.Serialize(invoiced.Billing.Read(invoiced.Tenant).Account) == account
            && invoiced.Database.Outbox().Count(x => x.Kind == "UsageAccountingPending") == 1,
            "late usage for an issued period remains durable pending without rewriting invoice or ledger");

        using var inactive = new UsageFixture(directory, "inactive-consumer");
        var inactiveUsage = inactive.Event("completed-before-offboarding");
        var tenant = inactive.Access.GetTenants().Single(x => x.Id == inactive.Tenant);
        var priorAccount = JsonSerializer.Serialize(inactive.Billing.Read(inactive.Tenant).Account);
        // Central tenant status is changed directly because this adapter has no tenant-status command.
        inactive.Database.Transaction(() => { inactive.Database.Put("tenant", tenant.Id.ToString(), tenant with { IsActive = false }); return true; });
        var inactiveReceipt = inactive.Store.Accept(inactiveUsage, true);
        check(inactiveReceipt.Status == "accounting-pending" && inactiveReceipt.Account is null
            && inactive.Database.Read<MeasuredUsageOperation>("measured.operation", inactiveUsage.BusinessKey)!.EventJson == inactiveUsage.ToWireJson()
            && JsonSerializer.Serialize(inactive.Database.Read<BillingAccount>("accounting", inactive.Tenant.ToString())) == priorAccount,
            "inactive consumer holds completed remote evidence without losing event or mutating ledger");
        inactive.Database.Transaction(() => { inactive.Database.Put("tenant", tenant.Id.ToString(), tenant); return true; });
        check(inactive.Store.Accept(Alias(inactiveUsage)).Status == "committed" && inactive.Billing.Read(inactive.Tenant).Outstanding == 12,
            "reactivated consumer resolves the retained original usage exactly once");
        inactive.Database.Transaction(() => { inactive.Database.Put("tenant", tenant.Id.ToString(), tenant with { IsActive = false }); return true; });
        var inactiveReplay = inactive.Store.Accept(inactiveUsage);
        check(inactiveReplay.Status == "committed" && inactiveReplay.Account is null
            && inactive.Database.Read<BillingAccount>("accounting", inactive.Tenant.ToString())!.Entries.Count(x => x.Kind == "usage") == 1,
            "inactive committed consumer still yields its durable usage receipt without duplicating charge");
    }

    private static void BackupRecovery(string directory, Action<bool, string> check)
    {
        using var fixture = new UsageFixture(directory, "usage-recovery", 12);
        var committed = fixture.Event("backup-committed"); var alias = Alias(committed); var pending = fixture.Event("backup-pending");
        fixture.Store.Accept(committed); fixture.Store.Accept(alias); fixture.Store.Accept(pending, true);
        var count = fixture.Database.Outbox().Length;
        var bundle = Path.Combine(fixture.Root, "bundle");
        var manifest = FabricRecovery.Backup(fixture.FabricPath, fixture.RegistryPath, bundle, true);
        fixture.ConfigureCredit(36, "source-more-credit"); fixture.Store.Accept(pending);
        var afterBackup = fixture.Event("post-backup"); fixture.Store.Accept(afterBackup);
        var restored = Path.Combine(fixture.Root, "restored"); FabricRecovery.Restore(bundle, restored, true);
        using var database = new FabricDatabase(Path.Combine(restored, "fabric.db"));
        var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["Platform:RegistryPath"] = Path.Combine(restored, "platform-registry.db") }).Build();
        var access = new PersistentAccessManagement(database, new InMemoryAccessManagement(new PlatformRegistry(settings)));
        var billing = new AccountingStore(restored, access, fixture.Clock, database);
        var validator = new TestValidator(); var intake = new MeasuredUsageStore(database, billing, validator, fixture.Clock);
        check(manifest.Files.Length == 2 && database.Outbox().Length == count
            && database.Read<MeasuredUsageOperation>("measured.operation", committed.BusinessKey)!.EventJson == committed.ToWireJson()
            && database.Read<MeasuredUsageOperation>("measured.operation", pending.BusinessKey)!.Status == "accounting-pending"
            && database.All<MeasuredEventReceipt>("measured.event").Length == 3,
            "actual recovery bundle restores complete usage journals pending evidence aliases and outbox");
        intake.Accept(committed); intake.Accept(alias);
        check(billing.Read(fixture.Tenant).Outstanding == 12 && database.Outbox().Length == count && validator.Calls == 0,
            "restored committed identities replay without duplicate charge or rate resolution");
        billing.Execute(fixture.Tenant, new(billing.Read(fixture.Tenant).Account.Revision, "configure", "restored-credit", "postpaid", "IRR", 36,
            null, null, 0, 0, 0, "recovery", null, null), "test");
        intake.Accept(pending); intake.Accept(afterBackup); intake.Accept(Alias(afterBackup));
        check(billing.Read(fixture.Tenant).Outstanding == 36 && billing.Read(fixture.Tenant).Account.Entries.Count(x => x.Kind == "usage") == 3
            && database.Outbox().Count(x => x.Kind == "UsageCommitted") == 3 && validator.Calls == 1
            && database.All<MeasuredUsageOperation>("measured.operation").All(x => x.Status == "committed"),
            "post-restore stable replay completes pending and missing post-backup events exactly once");
    }

    private static void OrganizationIntegration(string directory, Action<bool, string> check)
    {
        using var fixture = new UsageFixture(directory, "organization-usage", clock: new UsageClock(DateTimeOffset.UtcNow.AddMinutes(1)));
        var disabled = new OrganizationUsageMetering(fixture.Settings, fixture.Access, fixture.Registry, fixture.Clock);
        var unmetered = new OrganizationStore(fixture.Root, fixture.Access, database: fixture.Database, metering: disabled);
        unmetered.Execute(fixture.Tenant, Command(0, "disabled"), "test");
        check(!disabled.Enabled && fixture.Billing.Read(fixture.Tenant).Outstanding == 0
            && fixture.Database.All<MeasuredUsageOperation>("measured.operation").Length == 0,
            "new usage metering defaults disabled and existing organization callers remain compatible");
        const string actor = "usage-member";
        fixture.Access.AddUser(new(fixture.Tenant, actor, "Usage Member"));
        fixture.Access.SetGrant(new(fixture.Tenant, actor, "organization.write", null), "admin", "usage fixture");
        var plan = fixture.Access.RegisterPlan(new("organization-fabric", "usage-post", "Usage Postpaid", "postpaid", "IRR", 0, 0));
        var subscription = fixture.Access.Subscribe(new(fixture.Tenant, "organization-fabric", plan.Id, 10));
        fixture.Access.ApplyContractEvent(new("usage-contract-activated", fixture.Tenant, "test-contract", "organization-fabric", "fanasa.ayin",
            "usage-post", "ContractActivated", null, "change", fixture.Clock.Now.AddMinutes(-2)));
        var firstTime = fixture.Clock.Now; var settings = OrganizationSettings(fixture, firstTime);
        var resolver = new OrganizationUsageMetering(settings, fixture.Access, fixture.Registry, fixture.Clock);
        var intake = new MeasuredUsageStore(fixture.Database, fixture.Billing, resolver, fixture.Clock);
        var store = new OrganizationStore(fixture.Root, fixture.Access, true, fixture.Database, metering: resolver, measuredUsage: intake);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new("sub", actor), new("tenant_permission", fixture.Tenant + ":organization.write")], "test"));
        var request = Command(store.Read(fixture.Tenant).Revision, "metered") with { RequestId = Guid.NewGuid().ToString("D") };
        Reject<ArgumentException>(() => store.Execute(fixture.Tenant, request with { RequestId = null }, actor, principal), check,
            "enabled metered unit.save requires an explicit UUID request identity");
        Reject<UnauthorizedAccessException>(() => store.Execute(fixture.Tenant, request, actor), check, "enabled metered unit.save requires actual authenticated principal");
        Reject<ArgumentException>(() => new OrganizationStore(fixture.Root, fixture.Access, true, fixture.Database, fixture.Billing, resolver, intake), check,
            "both organization billing switches reject startup to prevent double charging");
        var first = store.Execute(fixture.Tenant, request, actor, principal);
        var original = UsageEventV1.FromJson(fixture.Database.All<MeasuredUsageOperation>("measured.operation").Single().EventJson);
        check(first.Revision == 2 && fixture.Billing.Read(fixture.Tenant).Outstanding == 12 && original.Operation.ActorId == actor
            && original.Operation.RequestId == Guid.Parse(request.RequestId!) && original.OwnerTenantId == fixture.Owner
            && original.ConsumerTenantId == fixture.Tenant && original.ContractVersion == 7,
            "authenticated unit.save commits central contract-bound usage with domain state in shared Fabric");
        var rollbackRequest = Command(first.Revision, "outer-rollback") with { RequestId = Guid.NewGuid().ToString("D") };
        var beforeRollback = fixture.Database.Outbox().Length;
        Reject<InvalidOperationException>(() => fixture.Database.Transaction<bool>(() =>
        {
            store.Execute(fixture.Tenant, rollbackRequest, actor, principal);
            throw new InvalidOperationException("organization commit acknowledgement interrupted");
        }), check, "failure after metered organization staging injected");
        check(store.Read(fixture.Tenant).Revision == first.Revision && fixture.Billing.Read(fixture.Tenant).Outstanding == 12
            && fixture.Database.Read<OrgState>("organization.history", $"{fixture.Tenant}:{first.Revision + 1}") is null
            && fixture.Database.Read<OrgReceipt>("organization.receipt", $"{fixture.Tenant}:{rollbackRequest.RequestId}") is null
            && fixture.Database.Outbox().Length == beforeRollback && fixture.Database.All<MeasuredUsageOperation>("measured.operation").Length == 1,
            "outer failure rolls back actual chart history org receipt usage journal charge and every outbox together");
        Reject<UnauthorizedAccessException>(() => intake.Accept(UsageEventV1.FromJson(fixture.Event("forged-local").ToWireJson()), true), check,
            "new local usage deserialized from JSON cannot impersonate invocation proof");
        var revision = store.Read(fixture.Tenant).Revision; var outbox = fixture.Database.Outbox().Length;
        Reject<ArgumentException>(() => store.Execute(fixture.Tenant, Command(revision, "metered") with { RequestId = Guid.NewGuid().ToString("D") }, actor, principal), check,
            "invalid metered organization mutation is rejected");
        check(store.Read(fixture.Tenant).Revision == revision && fixture.Billing.Read(fixture.Tenant).Outstanding == 12
            && fixture.Database.Outbox().Length == outbox && fixture.Database.All<MeasuredUsageOperation>("measured.operation").Length == 1,
            "invalid domain mutation leaves usage accounting organization history and outbox untouched");
        fixture.Clock.Now = firstTime.AddHours(2);
        check(store.Execute(fixture.Tenant, request, actor, principal).Revision == first.Revision
            && fixture.Billing.Read(fixture.Tenant).Outstanding == 12 && fixture.Database.Outbox().Length == outbox,
            "organization request replay returns original receipt after configured v8 price change");
        Reject<InvalidOperationException>(() => store.Execute(fixture.Tenant, request with { Code = "changed" }, actor, principal), check,
            "metered organization request rejects changed payload before repricing");
        var next = Command(revision, "new-rate") with { RequestId = Guid.NewGuid().ToString("D") };
        store.Execute(fixture.Tenant, next, actor, principal);
        check(fixture.Billing.Read(fixture.Tenant).Outstanding == 30
            && fixture.Database.All<MeasuredUsageOperation>("measured.operation").Select(x => UsageEventV1.FromJson(x.EventJson).RateSnapshot.RateVersion).Order().SequenceEqual([7, 8]),
            "fresh organization action uses new configured v8 rate without repricing v7");
        var beforeRole = fixture.Billing.Read(fixture.Tenant).Outstanding;
        store.Execute(fixture.Tenant, Command(store.Read(fixture.Tenant).Revision, "role") with { Operation = "role.save" }, actor);
        check(fixture.Billing.Read(fixture.Tenant).Outstanding == beforeRole && fixture.Database.All<MeasuredUsageOperation>("measured.operation").Length == 2,
            "opted-in usage pilot meters only unit.save");
        fixture.Clock.Now = firstTime.AddDays(2);
        check(store.Execute(fixture.Tenant, request, actor, principal).Revision == first.Revision
            && fixture.Billing.Read(fixture.Tenant).Outstanding == 30,
            "expired configured pricing windows still permit an exact authorized organization receipt replay");
        Reject<UnauthorizedAccessException>(() => store.Execute(fixture.Tenant,
            Command(store.Read(fixture.Tenant).Revision, "no-current-rate") with { RequestId = Guid.NewGuid().ToString("D") }, actor, principal), check,
            "expired configured pricing windows reject a fresh metered action");
        fixture.Clock.Now = firstTime.AddHours(2);
        var replayStore = new OrganizationStore(fixture.Root, fixture.Access, false, fixture.Database, metering: resolver, measuredUsage: intake);
        fixture.Access.SetSubscription(fixture.Tenant, subscription.Id, "suspended", null, "admin", "usage suspension");
        Reject<UnauthorizedAccessException>(() => replayStore.Execute(fixture.Tenant, request, actor, principal), check,
            "current subscription suspension rejects metered replay even with legacy subscription gate disabled");
        fixture.Access.SetSubscription(fixture.Tenant, subscription.Id, "active", fixture.Clock.Now.AddMinutes(-1), "admin", "usage expiry");
        Reject<UnauthorizedAccessException>(() => replayStore.Execute(fixture.Tenant, request, actor, principal), check,
            "expired current subscription rejects metered receipt replay");
        fixture.Access.SetSubscription(fixture.Tenant, subscription.Id, "active", fixture.Clock.Now.AddMonths(1), "admin", "usage renewal");
        fixture.Access.ApplyContractEvent(new("usage-contract-expired", fixture.Tenant, "test-contract", "organization-fabric", "fanasa.ayin",
            "usage-post", "ContractExpired", null, "change", fixture.Clock.Now.AddSeconds(-1)));
        Reject<UnauthorizedAccessException>(() => replayStore.Execute(fixture.Tenant, request, actor, principal), check,
            "expired current entitlement rejects metered receipt replay");
        fixture.Access.ApplyContractEvent(new("usage-contract-renewed", fixture.Tenant, "test-contract", "organization-fabric", "fanasa.ayin",
            "usage-post", "ContractRenewed", null, "change", fixture.Clock.Now));
        check(replayStore.Execute(fixture.Tenant, request, actor, principal).Revision == first.Revision
            && fixture.Billing.Read(fixture.Tenant).Outstanding == 30,
            "current entitlement renewal restores original response without a new charge");
        fixture.ConfigureCredit(30, "organization-limit");
        var rejected = Command(store.Read(fixture.Tenant).Revision, "unfunded") with { RequestId = Guid.NewGuid().ToString("D") };
        var before = JsonSerializer.Serialize(store.Read(fixture.Tenant)); outbox = fixture.Database.Outbox().Length;
        Reject<MeasuredAccountingException>(() => store.Execute(fixture.Tenant, rejected, actor, principal), check,
            "financial admission rejects unfunded local action before business commit");
        check(JsonSerializer.Serialize(store.Read(fixture.Tenant)) == before && fixture.Database.Outbox().Length == outbox
            && fixture.Database.All<MeasuredUsageOperation>("measured.operation").Length == 2
            && fixture.Database.Read<OrgReceipt>("organization.receipt", $"{fixture.Tenant}:{rejected.RequestId}") is null,
            "local credit rejection rolls back chart history receipts and leaves no pending completed event");
        fixture.Access.SetGrant(new(fixture.Tenant, actor, "organization.write", fixture.Clock.Now.AddMinutes(-1)), "admin", "revoke");
        Reject<UnauthorizedAccessException>(() => store.Execute(fixture.Tenant, request, actor, principal), check,
            "current durable grant revocation blocks even an authorized historical response replay");
    }

    private static OrgCommand Command(long revision, string code) => new(revision, "unit.save", null, code, code, null, "team", null, 1,
        null, null, null, null, "usage fixture");

    private static IConfiguration OrganizationSettings(UsageFixture fixture, DateTimeOffset now)
    {
        var values = new Dictionary<string, string?>
        {
            ["UsageMetering:Organization:Enabled"] = "true", ["UsageMetering:Organization:Environment"] = "development",
            ["UsageMetering:Organization:ProducerId"] = "organization-backend", ["UsageMetering:Organization:DeploymentId"] = "test-host"
        };
        void Binding(int index, int version, string price, DateTimeOffset from, DateTimeOffset to)
        {
            var prefix = $"UsageMetering:Organization:Bindings:{index}:";
            values[prefix + "ConsumerTenantId"] = fixture.Tenant.ToString("D"); values[prefix + "ContractId"] = "test-contract";
            values[prefix + "ContractVersion"] = "7"; values[prefix + "AllocationId"] = "test-allocation";
            values[prefix + "AllocationPeriodId"] = "2026-10"; values[prefix + "PlanKey"] = "usage-post";
            values[prefix + "PricingReference"] = "organization-change-rate";
            var rate = prefix + "RateSnapshot:";
            values[rate + "RateId"] = "organization-change-rate"; values[rate + "RateVersion"] = version.ToString(CultureInfo.InvariantCulture);
            values[rate + "EffectiveFrom"] = UtcText(from); values[rate + "EffectiveTo"] = UtcText(to);
            values[rate + "UnitPrice"] = price; values[rate + "Currency"] = "IRR"; values[rate + "MinorUnitScale"] = "0";
            values[rate + "Rounding"] = "away-from-zero"; values[rate + "BillingMode"] = "postpaid";
        }
        Binding(0, 7, "12", now.AddDays(-1), now.AddHours(1)); Binding(1, 8, "18", now.AddHours(1), now.AddDays(1));
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static string UtcText(DateTimeOffset time) => time.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
    private static UsageEventV1 Alias(UsageEventV1 message) => message with
    {
        EventId = Guid.NewGuid(), ProducedAt = message.ProducedAt.AddSeconds(10), CorrelationId = Guid.NewGuid(), TraceId = new string('a', 32),
        Operation = message.Operation with { RequestId = Guid.NewGuid() }
    };
    private static void Reject<T>(Action action, Action<bool, string> check, string label) where T : Exception
    { try { action(); } catch (T) { check(true, label); return; } throw new Exception("Expected rejection: " + label); }

    private sealed class UsageFixture : IDisposable
    {
        public string Root { get; }
        public string FabricPath { get; }
        public string RegistryPath { get; }
        public IConfiguration Settings { get; }
        public PlatformRegistry Registry { get; }
        public FabricDatabase Database { get; }
        public PersistentAccessManagement Access { get; }
        public Guid Owner { get; }
        public Guid Tenant { get; }
        public UsageClock Clock { get; }
        public AccountingStore Billing { get; }
        public TestValidator Validator { get; } = new();
        public MeasuredUsageStore Store { get; }
        public UsageFixture(string directory, string name, decimal credit = 1000, UsageClock? clock = null)
        {
            Root = Path.Combine(directory, "usage-" + name); Directory.CreateDirectory(Root);
            FabricPath = Path.Combine(Root, "fabric.db"); RegistryPath = Path.Combine(Root, "registry.db");
            Settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Platform:RegistryPath"] = RegistryPath }).Build();
            Registry = new PlatformRegistry(Settings); Database = new FabricDatabase(FabricPath);
            Access = new PersistentAccessManagement(Database, new InMemoryAccessManagement(Registry)); Owner = Access.GetTenants().Single().Id;
            Tenant = Access.CreateTenant("usage-consumer", "Usage Consumer").Id;
            Registry.SetTenantSource(Access.GetTenants); Registry.SetMembershipSource(Access.GetUsers);
            Registry.Register(new(Guid.NewGuid(), "organization-fabric", "Organization", "organization", "fanasa.ayin", null, null, Owner, "fanasa"), "test");
            Clock = clock ?? new UsageClock(HistoricalTime); Billing = new AccountingStore(Root, Access, Clock, Database);
            ConfigureCredit(credit, "configure"); Bill("tariff", "current-v8");
            Store = new MeasuredUsageStore(Database, Billing, Validator, Clock);
        }
        public void ConfigureCredit(decimal amount, string key) => Bill("configure", key, amount);
        public AccountView Bill(string operation, string key, decimal credit = 1000, string? period = null) => Billing.Execute(Tenant,
            new(Billing.Read(Tenant).Account.Revision, operation, key, "postpaid", "IRR", credit, "organization-fabric", "organization.change", 18, 1, 0, "test", period, null), "test");
        public UsageEventV1 Event(string operation) => new("1.0", Guid.NewGuid(), HistoricalTime.AddMinutes(-89), HistoricalTime.AddMinutes(-89).AddSeconds(1),
            new("organization-backend", "product-backend", "test-host"), "development", Owner, Tenant, "organization-fabric", "test-contract", 7,
            "test-allocation", "2026-10", new(operation, "organization.unit.save/v1", "usage-member", Guid.NewGuid(), UsageEventV1.Hash("request:" + operation),
                HistoricalTime.AddMinutes(-90), "organization-unit-committed/v1"),
            new("decision:" + operation, "organization-permission/v1", "credit:" + operation), new("organization.change", "1", "change", "1"),
            new("organization-change-rate", 7, new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero), HistoricalTime.AddHours(-1), "12", "IRR", 0, "away-from-zero", "postpaid"),
            "completed", new("receipt:" + operation, UsageEventV1.Hash("result:" + operation), "fabric://receipt/" + operation), Guid.NewGuid());
        public void Dispose() => Database.Dispose();
    }

    private sealed class TestValidator : IUsageSnapshotValidator
    {
        public bool Allow { get; set; } = true;
        public int Calls { get; private set; }
        public void ValidateNew(UsageEventV1 message)
        { Calls++; if (!Allow) throw new UnauthorizedAccessException("The current trusted projection no longer admits this event."); }
    }
    private sealed class UsageClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
