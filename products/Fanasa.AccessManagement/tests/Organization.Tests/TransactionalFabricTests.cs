using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Domain.Access;
using Fanasa.AccessManagement.Web.Organization;
using Fanasa.AccessManagement.Web.Accounting;
using Fanasa.AccessManagement.Web.Persistence;
using Fanasa.AccessManagement.Web.Platform;
using Microsoft.Extensions.Configuration;
using System.Security.Claims;
using Fanasa.AccessManagement.Web.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Fanasa.AccessManagement.Web.Api;

static class TransactionalFabricTests
{
    public static void Run(string directory, Action<bool, string> check)
    {
        void Reject<T>(Action action, string label) where T : Exception
        { try { action(); } catch (T) { check(true, label); return; } throw new Exception("Expected rejection: " + label); }
        var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Platform:RegistryPath"] = Path.Combine(directory, "catalog.db") }).Build();
        var registry = new PlatformRegistry(settings);
        var catalog = new InMemoryAccessManagement(registry);
        catalog.RegisterProduct(new RegisterProductRequest("organization-fabric", "ساختار سازمانی", "organization", null, "fanasa.ayin", null));
        var path = Path.Combine(directory, "fabric.db");
        using var database = new FabricDatabase(path);
        var access = new PersistentAccessManagement(database, catalog);
        var tenant = access.GetTenants().Single().Id;
        var member = access.AddUser(new(tenant, "member", "عضو"));
        registry.SetTenantSource(access.GetTenants);
        registry.SetMembershipSource(access.GetUsers);
        registry.Grant(new("member", tenant, "developer.launch", null), "test");
        check(registry.Allows("member", tenant, "developer.launch"), "developer grant requires authoritative active membership");
        var plan = access.RegisterPlan(new("organization-fabric", "post", "دوره‌ای", "postpaid", "IRR", 0, 0));
        access.Subscribe(new(tenant, "organization-fabric", plan.Id, 1));
        Reject<InvalidOperationException>(() => access.AddUser(new(tenant, "extra", null)), "durable subscription seat limit enforced");
        Reject<InvalidOperationException>(() => access.Subscribe(new(tenant, "organization-fabric", plan.Id, 1)), "duplicate active subscription rejected");
        var billing = new AccountingStore(directory, access, database: database);
        BillingCommand B(string operation, string key) => new(billing.Read(tenant).Account.Revision, operation, key, "postpaid", "IRR", 100, "organization-fabric", "organization.change", 10, 1, 0, "test", null, null);
        billing.Execute(tenant, B("configure", "config"), "test");
        billing.Execute(tenant, B("tariff", "rate"), "test");
        var org = new OrganizationStore(directory, access, true, database, billing);
        OrgCommand C(string code) => new(org.Read(tenant).Revision, "unit.save", null, code, code, null, "team", null, 1, null, null, null, null, "test");
        var request = C("root") with { RequestId = "stable-request" };
        org.Execute(tenant, request, "member");
        check(org.Read(tenant).Revision == 1 && billing.Read(tenant).Outstanding == 10, "organization change and consumption commit together");
        var outboxCount = database.Outbox().Length;
        check(org.Execute(tenant, request, "member").Revision == 1 && billing.Read(tenant).Outstanding == 10 && database.Outbox().Length == outboxCount, "organization request retry does not repeat charge or outbox");
        Reject<InvalidOperationException>(() => org.Execute(tenant, request with { Code = "different" }, "member"), "organization request identity rejects changed payload");
        Reject<InvalidOperationException>(() => org.Execute(tenant, request, "another-member"), "organization request receipt is actor-bound");
        check(billing.Read(tenant).Account.Entries.Single(x => x.Kind == "usage").UnitPrice == 10, "ledger retains original unit-price snapshot");
        Reject<InvalidOperationException>(() => database.Transaction<bool>(() => { org.Execute(tenant, C("rollback"), "member"); throw new InvalidOperationException("simulated failure before commit"); }), "transaction failure injected");
        check(org.Read(tenant).Revision == 1 && billing.Read(tenant).Outstanding == 10 && database.Outbox().Length == outboxCount, "organization accounting history and outbox all rollback");
        Reject<ArgumentException>(() => org.Execute(tenant, C("root"), "member"), "invalid organization mutation rejected before debit");
        check(billing.Read(tenant).Outstanding == 10, "invalid mutation does not consume credit");
        billing.Execute(tenant, B("configure", "lower-credit") with { CreditLimit = 10 }, "test");
        Reject<InvalidOperationException>(() => org.Execute(tenant, C("unfunded"), "member"), "organization action blocked by postpaid credit limit");
        check(org.Read(tenant).Units.Length == 1 && org.History(tenant, 1).Units.Length == 1, "unfunded action leaves chart and history unchanged");
        using var restartedDb = new FabricDatabase(path);
        var restartedAccess = new PersistentAccessManagement(restartedDb, new InMemoryAccessManagement(new PlatformRegistry(settings)));
        var restartedBilling = new AccountingStore(directory, restartedAccess, database: restartedDb);
        var restartedOrg = new OrganizationStore(directory, restartedAccess, true, restartedDb, restartedBilling);
        check(restartedAccess.GetUsers(tenant).Single().Id == member.Id && restartedAccess.GetPlans().Single().Id == plan.Id && restartedAccess.GetSubscriptions(tenant).Single().SeatLimit == 1, "memberships plans subscriptions persist across restart");
        check(restartedAccess.GetProducts().Single().Key == "organization-fabric", "existing central product registry reused across restart");
        check(restartedOrg.Read(tenant).Revision == 1 && restartedBilling.Read(tenant).Outstanding == 10, "transactional chart and accounting survive restart");
        var eventCommand = new ContractLifecycleEvent("event1", tenant, "contract", "organization-fabric", "fanasa.ayin", "post", "ContractActivated", null, "change", DateTimeOffset.UtcNow);
        access.ApplyContractEvent(eventCommand); access.ApplyContractEvent(eventCommand);
        check(access.GetEntitlements(tenant).Count == 1, "contract event persisted idempotently");
        Reject<InvalidOperationException>(() => access.ApplyContractEvent(eventCommand with { EventType = "ContractExpired" }), "contract event key with different payload rejected");
        var usage = new RecordUsageCommand(tenant, "organization-fabric", "fanasa.ayin", "change", 1, "change", DateTimeOffset.UtcNow, "test", "usage1", null);
        access.RecordUsage(usage); access.RecordUsage(usage);
        Reject<InvalidOperationException>(() => access.RecordUsage(usage with { Quantity = 2 }), "persistent usage identity conflict rejected");
        var successes = new System.Collections.Concurrent.ConcurrentBag<bool>();
        var unmetered = new OrganizationStore(directory, access, database: database);
        var another = new OrganizationStore(directory, restartedAccess, database: restartedDb);
        var version = unmetered.Read(tenant).Revision;
        Parallel.For(0, 2, i => { try { (i == 0 ? unmetered : another).Execute(tenant, C("parallel" + i) with { ExpectedRevision = version }, "test"); successes.Add(true); } catch (InvalidOperationException) { successes.Add(false); } });
        check(successes.Count(x => x) == 1 && successes.Count(x => !x) == 1, "separate database connections serialize stale-version writers");
        Reject<ArgumentException>(() => new OrganizationStore(directory, access, database: database, accounting: restartedBilling), "metering cannot use a different transaction database");
        var multi = new ClaimsPrincipal(new ClaimsIdentity([new("sub", "member"), new("tenant_id", tenant.ToString()), new("tenant_id", Guid.NewGuid().ToString()), new("permission", "organization.write")], "test"));
        check(!TenantAuthorization.Allows(multi, tenant, "organization.write"), "multi-tenant unscoped permissions cannot bleed across tenants");
        var scoped = new ClaimsPrincipal(new ClaimsIdentity([new("sub", "member"), new("tenant_permission", tenant + ":organization.read")], "test"));
        check(TenantAuthorization.Allows(scoped, tenant, "organization.read") && !TenantAuthorization.Allows(scoped, tenant, "organization.write"), "scoped permission pair grants exactly one tenant operation");
        access.SetGrant(new(tenant, "member", "organization.read", null), "admin", "test");
        var transformer = new FabricClaimsTransformation(access);
        var transformed = transformer.TransformAsync(multi).GetAwaiter().GetResult();
        check(TenantAuthorization.Allows(transformed, tenant, "organization.read") && !TenantAuthorization.Allows(transformed, tenant, "organization.write"), "durable grants replace stale session domain permissions");
        access.SetGrant(new(tenant, "member", "organization.read", DateTimeOffset.UtcNow.AddMinutes(-1)), "admin", "revoke");
        check(!TenantAuthorization.Allows(transformer.TransformAsync(transformed).GetAwaiter().GetResult(), tenant, "organization.read"), "grant revocation takes effect on next request");
        access.SetGrant(new(tenant, "member", "organization.read", null), "admin", "test");
        access.SetMembership(tenant, member.Id, false, "admin", "offboarding");
        check(!registry.Allows("member", tenant, "developer.launch"), "offboarding denies existing developer launch grant");
        check(!TenantAuthorization.Allows(transformer.TransformAsync(multi).GetAwaiter().GetResult(), tenant, "organization.read") && access.GetSubscriptions(tenant).Single().ActiveSeats == 0, "offboarding removes effective grants and reconciles seats");
        access.SetMembership(tenant, member.Id, true, "admin", "onboarding");
        check(!TenantAuthorization.Allows(transformer.TransformAsync(multi).GetAwaiter().GetResult(), tenant, "organization.read"), "reactivation does not resurrect revoked grants");
        access.SetSubscription(tenant, access.GetSubscriptions(tenant).Single().Id, "suspended", null, "admin", "test");
        Reject<UnauthorizedAccessException>(() => org.Read(tenant), "suspended subscription blocks organization access");
        access.SetSubscription(tenant, access.GetSubscriptions(tenant).Single().Id, "active", DateTimeOffset.UtcNow.AddMonths(1), "admin", "renew");
        check(org.Read(tenant).Revision == 2, "subscription renewal restores access without recreating chart");
        var manager = new ClaimsPrincipal(new ClaimsIdentity([new("sub", "member"), new("tenant_permission", tenant + ":tenancy.manage")], "test"));
        var api = new TenantAdministrationApi(access) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = manager } } };
        check(api.Grant(tenant, new("member", "billing.write", null, "attempt")) is ForbidResult, "tenant manager cannot escalate accounting grants");
        database.Transaction(() =>
        {
            try { database.Transaction<bool>(() => { database.Put("savepoint", "broken", "partial"); throw new InvalidOperationException(); }); } catch (InvalidOperationException) { }
            database.Put("savepoint", "valid", "complete"); return true;
        });
        check(database.Read<string>("savepoint", "broken") is null && database.Read<string>("savepoint", "valid") == "complete", "nested failure caught by caller rolls back its savepoint");
        using var importedDb = new FabricDatabase(Path.Combine(directory, "import.db"));
        FabricMigration.ImportTenant(importedDb, tenant, directory, directory);
        check(importedDb.Read<OrgState>("organization", tenant.ToString())!.Revision > 0 && importedDb.Read<BillingAccount>("accounting", tenant.ToString())!.Revision > 0, "explicit legacy import preserves chart and billing");
        Reject<InvalidOperationException>(() => FabricMigration.ImportTenant(importedDb, tenant, directory, directory), "legacy import cannot overwrite existing tenant state");
        var chart = unmetered.Read(tenant); var rootId = chart.Units.Single(x => x.Code == "root").Id;
        var positionState = unmetered.Execute(tenant, C("position") with { ExpectedRevision = chart.Revision, Operation = "position.save", UnitId = rootId }, "test");
        var positionId = positionState.Positions.Single().Id;
        var now = DateTimeOffset.UtcNow;
        unmetered.Execute(tenant, C("appointment") with { ExpectedRevision = unmetered.Read(tenant).Revision, Operation = "appointment.add", PositionId = positionId, Subject = "member", From = now.AddDays(-1), To = now.AddDays(1) }, "test");
        unmetered.Execute(tenant, C("future") with { ExpectedRevision = unmetered.Read(tenant).Revision, Operation = "appointment.add", PositionId = positionId, Subject = "member", From = now.AddDays(2), To = now.AddDays(3) }, "test");
        var beforeOffboarding = unmetered.Read(tenant).Revision;
        access.SetMembership(tenant, member.Id, false, "admin", "exit organization");
        check(unmetered.Read(tenant).Appointments.Length == 1 && unmetered.Read(tenant).Appointments.Single().To <= DateTimeOffset.UtcNow, "offboarding ends current appointments and cancels future appointments");
        check(unmetered.History(tenant, beforeOffboarding).Appointments.Length == 2 && unmetered.Read(tenant).Changes.Last().Operation == "membership.offboard", "offboarding preserves previous appointment history");
    }
}
