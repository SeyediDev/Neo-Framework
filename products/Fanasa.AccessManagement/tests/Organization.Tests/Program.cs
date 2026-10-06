using System.Security.Claims;
using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Domain.Access;
using Fanasa.AccessManagement.Web.Organization;
using Fanasa.AccessManagement.Web.Accounting;
using Fanasa.AccessManagement.Web.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

if (args.Contains("--preview")) { await TestPreview.Run(); return; }
if (args is ["--recovery-fixture", var recoveryFixture])
{
    Directory.CreateDirectory(recoveryFixture);
    RecoveryTests.Run(recoveryFixture, (passed, label) => { if (!passed) throw new Exception(label); Console.WriteLine("PASS " + label); });
    return;
}

var directory = Path.Combine(Path.GetTempPath(), "fanasa-org-tests-" + Guid.NewGuid());
var access = new InMemoryAccessManagement();
var tenant = access.GetTenants().Single().Id;
var other = access.CreateTenant("other", "Other").Id;
var store = new OrganizationStore(directory, access);
var count = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception(name); Console.WriteLine("PASS " + name); count++; }
void Reject<T>(Action action, string name) where T : Exception { try { action(); } catch (T) { Check(true, name); return; } throw new Exception("Expected rejection: " + name); }
OrgCommand Command(string operation, long? revision = null) => new(revision ?? store.Read(tenant).Revision, operation, null, "واحد", "root", null, "department", null, 1, null, null, null, null, "آزمون تغییر");
OrgState Run(OrgCommand command) => store.Execute(tenant, command, "actor");
try
{
    var first = Run(Command("unit.save")); var root = first.Units.Single().Id;
    var second = Run(Command("unit.save") with { Code = "child", ParentId = root }); var child = second.Units.Single(x => x.Code == "child").Id;
    Reject<ArgumentException>(() => Run(Command("unit.save") with { Id = root, ParentId = child }), "cycle rejected");
    Reject<ArgumentException>(() => Run(Command("unit.save") with { Code = "ROOT" }), "duplicate code rejected");
    Reject<InvalidOperationException>(() => Run(Command("unit.save", 0)), "stale revision rejected");
    Reject<ArgumentException>(() => Run(Command("unit.archive") with { Id = root }), "parent archive rejected");
    var third = Run(Command("position.save") with { UnitId = child, Code = "manager" }); var position = third.Positions.Single().Id;
    var now = DateTimeOffset.UtcNow;
    access.AddUser(new AddTenantUserRequest(tenant, "alice", "Alice"));
    access.AddUser(new AddTenantUserRequest(tenant, "bob", "Bob"));
    access.AddUser(new AddTenantUserRequest(other, "outsider", "Outside"));
    Reject<ArgumentException>(() => Run(Command("appointment.add") with { PositionId = position, Subject = "outsider", From = now }), "cross-tenant appointment rejected");
    Run(Command("appointment.add") with { PositionId = position, Subject = "alice", From = now, To = now.AddDays(1) });
    Reject<ArgumentException>(() => Run(Command("appointment.add") with { PositionId = position, Subject = "bob", From = now, To = now.AddHours(1) }), "over-capacity rejected");
    Reject<ArgumentException>(() => Run(Command("appointment.add") with { PositionId = position, Subject = "alice", From = now, To = now.AddHours(1) }), "duplicate appointment rejected");
    Run(Command("appointment.add") with { PositionId = position, Subject = "bob", From = now.AddDays(1), To = now.AddDays(2) });
    Check(store.Read(tenant).Appointments.Length == 2, "adjacent intervals accepted");
    Reject<ArgumentException>(() => Run(Command("position.archive") with { Id = position }), "future appointment prevents archive");
    Check(store.History(tenant, 1).Units.Length == 1 && store.History(tenant, 1).Positions.Length == 0, "historical snapshot preserved");
    var restarted = new OrganizationStore(directory, access);
    Check(restarted.Read(tenant).Revision == 5 && restarted.Read(tenant).Changes.Length == 5, "restart preserves state and audit");
    Check(store.Read(other).Units.Length == 0, "tenant data isolated");
    var user = new ClaimsPrincipal(new ClaimsIdentity([new("sub", "alice"), new("tenant_id", tenant.ToString()), new("permission", "organization.read")], "test"));
    Check(OrgAuthorization.Allows(user, tenant, false) && !OrgAuthorization.Allows(user, tenant, true) && !OrgAuthorization.Allows(user, other, false), "tenant and permission authorization");
    var organizationApi = new OrganizationApi(store) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } } };
    Check(organizationApi.Read(other, null) is ForbidResult, "API cross-tenant read forbidden");
    Check(organizationApi.Execute(tenant, Command("unit.save")) is ForbidResult, "API read-only user cannot mutate");
    Check(!OrgAuthorization.Allows(new ClaimsPrincipal(), tenant, false), "anonymous access rejected");
    Reject<UnauthorizedAccessException>(() => new OrganizationStore(directory, access, true).Read(tenant), "SaaS subscription gate enforced");
    var version = store.Read(tenant).Revision; var results = new System.Collections.Concurrent.ConcurrentBag<bool>();
    Parallel.For(0, 2, i => { try { Run(Command("unit.save", version) with { Code = "parallel" + i }); results.Add(true); } catch (InvalidOperationException) { results.Add(false); } });
    Check(results.Count(x => x) == 1 && results.Count(x => !x) == 1, "concurrent writers serialized");
    Run(Command("role.save") with { Code = "org-manager", Name = "مدیر سازمانی" });
    var role = store.Read(tenant).Roles!.Single();
    Run(Command("position.save") with { Id = position, UnitId = child, Code = "manager", RoleId = role.Id });
    Check(store.Read(tenant).Positions.Single().RoleId == role.Id, "organizational role linked to chart position");
    var clock = new TestClock(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero));
    var billing = new AccountingStore(directory, access, clock);
    var accountingApi = new AccountingApi(billing) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } } };
    Check(accountingApi.Read(tenant) is ForbidResult, "organization permission does not grant accounting access");
    access.RegisterProduct(new RegisterProductRequest("organization-fabric", "ساختار سازمانی", "org", null, Fanasa.AccessManagement.Web.CapabilityCatalog.All.First().Audience, null));
    BillingCommand Bill(Guid id, string operation, string key) => new(billing.Read(id).Account.Revision, operation, key, "payg", "IRR", 0, "organization-fabric", "unit-month", 10, 1, 0, "test-reference", null, null);
    billing.Execute(tenant, Bill(tenant, "tariff", "tariff"), "actor");
    Reject<InvalidOperationException>(() => billing.Execute(tenant, Bill(tenant, "usage", "no-credit"), "actor"), "payg insufficient balance rejected");
    billing.Execute(tenant, Bill(tenant, "credit", "credit") with { Amount = 100 }, "actor");
    var usage = Bill(tenant, "usage", "usage") with { Quantity = 3 };
    var charged = billing.Execute(tenant, usage, "actor");
    Check(charged.WalletBalance == 70, "payg debits wallet");
    Check(billing.Execute(tenant, usage, "actor").WalletBalance == 70, "billing retry idempotent before version check");
    Reject<InvalidOperationException>(() => billing.Execute(tenant, usage with { Quantity = 4 }, "actor"), "duplicate billing key with changed payload rejected");
    billing.Execute(other, Bill(other, "configure", "configure") with { Mode = "postpaid", CreditLimit = 100 }, "actor");
    billing.Execute(other, Bill(other, "tariff", "tariff"), "actor");
    billing.Execute(other, Bill(other, "usage", "usage") with { Quantity = 7 }, "actor");
    Check(billing.Read(other).Outstanding == 70 && billing.Read(other).AvailableCredit == 30, "postpaid accrues debt within credit limit");
    Reject<InvalidOperationException>(() => billing.Execute(other, Bill(other, "usage", "over-limit") with { Quantity = 4 }, "actor"), "postpaid credit limit enforced");
    Reject<ArgumentException>(() => billing.Execute(other, Bill(other, "invoice", "early") with { Period = "2026-09" }, "actor"), "open period cannot be invoiced");
    clock.Now = clock.Now.AddMonths(1);
    var issued = billing.Execute(other, Bill(other, "invoice", "invoice") with { Period = "2026-09" }, "actor");
    Check(issued.Account.Invoices.Single().Amount == 70, "closed monthly invoice uses recorded charges");
    var invoiceId = issued.Account.Invoices.Single().Id;
    billing.Execute(other, Bill(other, "payment", "payment") with { InvoiceId = invoiceId, Amount = 40 }, "actor");
    Check(billing.Read(other).Outstanding == 30 && billing.Read(other).AvailableCredit == 70, "partial invoice payment restores credit");
    Reject<ArgumentException>(() => billing.Execute(other, Bill(other, "payment", "overpay") with { InvoiceId = invoiceId, Amount = 31 }, "actor"), "overpayment rejected");
    Check(new AccountingStore(directory, access, clock).Read(other).Outstanding == 30, "billing ledger survives restart");
    TransactionalFabricTests.Run(directory, Check);
    ZarinpalTests.Run(directory, Check);
    FabricStartupTests.Run(directory, Check);
    RecoveryTests.Run(directory, Check);
    AuditTests.Run(directory, Check);
    ComparisonTests.Run(directory, Check);
    Console.WriteLine($"{count} checks passed.");
}
finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(directory)) Directory.Delete(directory, true); }

sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}
