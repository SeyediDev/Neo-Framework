using System.Text;
using System.Text.Json;
using Fanasa.AccessManagement.Web.Accounting;
using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Metering;
using Fanasa.AccessManagement.Web.Persistence;
using Fanasa.AccessManagement.Web.Platform;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using System.Reflection;
using System.Security.Claims;

static class UsageReconciliationTests
{
    public static void Run(string directory, Action<bool, string> check)
    {
        CleanAndBounded(directory, check);
        MissingEvidence(directory, check);
        ConflictingEvidence(directory, check);
        MalformedOutboxTimestamp(directory, check);
        TenantIsolation(directory, check);
        AccountingRecovery(directory, check);
        SnapshotConsistency(directory, check);
        AdministrationBoundary(directory, check);
    }

    private static void CleanAndBounded(string directory, Action<bool, string> check)
    {
        using var fixture = new ReconciliationFixture(directory, "clean");
        var empty = fixture.Reconciler.Inspect(fixture.Tenant);
        check(empty.Coverage == "complete" && empty.Status == "clean" && empty.Counts.Operations == 0 && empty.Findings.Length == 0,
            "empty measured reconciliation reports complete clean evidence");
        for (var i = 0; i < 120; i++)
        {
            var usage = fixture.Event("paged-" + i); fixture.Intake.Accept(usage);
            if (i % 10 == 0) fixture.Intake.Accept(Alias(usage));
        }
        var before = fixture.Snapshot(); var report = fixture.Reconciler.Inspect(fixture.Tenant);
        check(report.Coverage == "complete" && report.Status == "clean" && report.IntegrityVerified
            && report.Counts.Operations == 120 && report.Counts.EventReceipts == 132 && report.Counts.Committed == 120
            && report.Counts.LedgerCharges == 120 && report.Counts.LedgerMarkers == 120 && report.Counts.CommittedOutbox == 120,
            "reconciliation scans every outbox document page and accepts stable event aliases without duplicate charge");
        var bounded = fixture.Reconciler.Inspect(fixture.Tenant, 10);
        check(bounded.Coverage == "incomplete" && bounded.Status == "incomplete" && !bounded.IntegrityVerified
            && bounded.Findings.Any(x => x.Code == "scan-limit-exceeded"),
            "bounded reconciliation explicitly reports incomplete coverage rather than a false clean result");
        check(fixture.Snapshot() == before, "complete and bounded reconciliation leave every persisted document ledger and outbox byte unchanged");
        Reject<ArgumentException>(() => fixture.Reconciler.Inspect(Guid.Empty), check, "reconciliation requires an explicit consumer tenant");
        Reject<ArgumentOutOfRangeException>(() => fixture.Reconciler.Inspect(fixture.Tenant, 0), check, "reconciliation rejects a zero scan bound");
        Reject<ArgumentOutOfRangeException>(() => fixture.Reconciler.Inspect(fixture.Tenant, 10001), check, "reconciliation rejects an unbounded scan size");
    }

    private static void MissingEvidence(string directory, Action<bool, string> check)
    {
        void Case(string name, Action<ReconciliationFixture, UsageEventV1> damage)
        {
            using var fixture = new ReconciliationFixture(directory, "missing-" + name); var usage = fixture.Event(name); fixture.Intake.Accept(usage);
            damage(fixture, usage); var before = fixture.Snapshot(); var report = fixture.Reconciler.Inspect(fixture.Tenant);
            check(report.Coverage == "complete" && report.Status == "issues" && !report.IntegrityVerified
                && report.Findings.Any(x => x.LedgerKey == usage.BusinessKey || x.EventKey == usage.EventKey),
                "reconciliation identifies missing " + name + " with the affected measured identity");
            check(fixture.Snapshot() == before, "missing " + name + " inspection never repairs or mutates financial evidence");
        }
        Case("charge", (fixture, usage) => fixture.ChangeAccount(account => account with
        { Entries = account.Entries.Where(x => !(x.Key == usage.BusinessKey && x.Kind == "usage")).ToArray() }));
        Case("ledger-marker", (fixture, usage) => fixture.ChangeAccount(account => account with
        { Entries = account.Entries.Where(x => !(x.Key == usage.BusinessKey && x.Kind == "receipt")).ToArray() }));
        Case("event-receipt", (fixture, usage) => fixture.DeleteDocument("measured.event", usage.EventKey));
        Case("operation", (fixture, usage) => fixture.DeleteDocument("measured.operation", usage.BusinessKey));
        Case("committed-outbox", (fixture, usage) => fixture.DeleteOutbox(usage.BusinessKey + ":committed"));
        Case("account-document", (fixture, _) => fixture.DeleteDocument("accounting", fixture.Tenant.ToString()));
    }

    private static void ConflictingEvidence(string directory, Action<bool, string> check)
    {
        void Case(string name, Action<ReconciliationFixture, UsageEventV1> damage, string? code = null)
        {
            using var fixture = new ReconciliationFixture(directory, "conflict-" + name); var usage = fixture.Event(name); fixture.Intake.Accept(usage);
            damage(fixture, usage); var before = fixture.Snapshot(); var report = fixture.Reconciler.Inspect(fixture.Tenant);
            check(report.Status == "issues" && !report.IntegrityVerified && report.Findings.Any(x =>
                (code is null || x.Code == code) && (code == "orphan-measured-ledger-entry" || x.LedgerKey == usage.BusinessKey || x.EventKey == usage.EventKey || x.OutboxId.HasValue)),
                "reconciliation detects conflicting " + name + " evidence");
            check(fixture.Snapshot() == before, "conflicting " + name + " reconciliation remains read only");
        }
        Case("operation-storage-key", (fixture, usage) => fixture.MoveDocument("measured.operation", usage.BusinessKey, "wrong-operation-physical-key"), "operation-key-mismatch");
        Case("event-storage-key", (fixture, usage) => fixture.MoveDocument("measured.event", usage.EventKey, "wrong-event-physical-key"), "event-receipt-key-mismatch");
        Case("operation-fingerprint", (fixture, usage) => fixture.ChangeOperation(usage, operation => operation with { Fingerprint = UsageEventV1.Hash("wrong") }));
        Case("event-fingerprint", (fixture, usage) => fixture.ChangeReceipt(usage, receipt => receipt with { Fingerprint = UsageEventV1.Hash("wrong") }));
        Case("operation-amount", (fixture, usage) => fixture.ChangeOperation(usage, operation => operation with { Amount = 99 }));
        Case("charge-amount", (fixture, usage) => fixture.ChangeEntry(usage, "usage", entry => entry with { Amount = 99 }));
        Case("charge-actor", (fixture, usage) => fixture.ChangeEntry(usage, "usage", entry => entry with { Actor = "other-actor" }));
        Case("charge-rate", (fixture, usage) => fixture.ChangeEntry(usage, "usage", entry => entry with { UnitPrice = 18 }));
        Case("charge-occurrence", (fixture, usage) => fixture.ChangeEntry(usage, "usage", entry => entry with { UsageOccurredAt = entry.UsageOccurredAt!.Value.AddDays(1) }));
        Case("ledger-marker-fingerprint", (fixture, usage) => fixture.ChangeEntry(usage, "receipt", entry => entry with { Reference = UsageEventV1.Hash("wrong") }));
        Case("duplicate-charge", (fixture, usage) => fixture.ChangeAccount(account => account with
        { Entries = [.. account.Entries, account.Entries.Single(x => x.Key == usage.BusinessKey && x.Kind == "usage") with { Id = Guid.NewGuid() }] }));
        Case("orphan-marker", (fixture, _) => fixture.ChangeAccount(account => account with
        { Entries = [.. account.Entries, new(Guid.NewGuid(), "measured:v1:" + UsageEventV1.Hash("orphan"), "receipt", null, null, 0, 0, fixture.Clock.Now, "actor", UsageEventV1.Hash("unknown"))] }), "orphan-measured-ledger-entry");
        Case("outbox-storage-key", (fixture, usage) => fixture.ChangeOutboxKey(usage.BusinessKey + ":committed", "wrong-outbox-physical-key"));
        Case("outbox-payload", (fixture, usage) => fixture.ChangeOutbox(usage.BusinessKey + ":committed", usage, 99));
        Case("duplicate-committed-outbox", (fixture, usage) => fixture.RawOutbox("duplicate-committed-copy", "UsageCommitted",
            fixture.Outbox().Single(x => x.Key == usage.BusinessKey + ":committed").Payload), "duplicate-committed-outbox");
    }

    private static void TenantIsolation(string directory, Action<bool, string> check)
    {
        using var fixture = new ReconciliationFixture(directory, "tenant-isolation"); var target = fixture.Event("target"); fixture.Intake.Accept(target);
        var other = fixture.Access.CreateTenant("other-consumer", "Other Consumer").Id; fixture.Configure(other, 1000, "other-configure");
        var foreign = fixture.Event("foreign", other); fixture.Intake.Accept(foreign);
        fixture.ChangeOperation(foreign, operation => operation with { Amount = 99 });
        var report = fixture.Reconciler.Inspect(fixture.Tenant); var otherReport = fixture.Reconciler.Inspect(other);
        check(report.Coverage == "complete" && report.Status == "clean" && report.Counts.Operations == 1 && report.Counts.LedgerCharges == 1
            && otherReport.Status == "issues" && !report.Findings.Any(x => x.LedgerKey == foreign.BusinessKey || x.EventKey == foreign.EventKey),
            "reconciliation scopes known corrupt evidence to its own consumer without leaking foreign identities");
        fixture.RawDocument("measured.operation", "malformed-operation", "{bad-json");
        fixture.RawDocument("measured.event", "malformed-event", "null");
        fixture.RawOutbox("malformed-outbox", "UsageCommitted", "{bad-json");
        var before = fixture.Snapshot(); var incomplete = fixture.Reconciler.Inspect(fixture.Tenant);
        check(incomplete.Coverage == "incomplete" && incomplete.Status == "incomplete" && incomplete.Counts.UnattributableRows >= 3
            && incomplete.Findings.Any(x => x.Code == "unattributable-operation")
            && incomplete.Findings.Any(x => x.Code == "unattributable-event-receipt")
            && incomplete.Findings.Any(x => x.Code == "unattributable-outbox"),
            "malformed unattributable rows explicitly make reconciliation coverage incomplete");
        check(incomplete.Findings.Where(x => x.Code.StartsWith("unattributable-", StringComparison.Ordinal))
            .All(x => x.LedgerKey is null && x.EventKey is null && x.OutboxId is null) && fixture.Snapshot() == before,
            "unattributable evidence returns generic diagnostics without exposing physical keys or altering data");
        check(incomplete.Operations.Length == 1 && incomplete.Operations.Single().LedgerKey == target.BusinessKey,
            "malformed and foreign evidence cannot become selectable accounting recovery summaries");
    }

    private static void MalformedOutboxTimestamp(string directory, Action<bool, string> check)
    {
        using var fixture = new ReconciliationFixture(directory, "malformed-outbox-time"); var broken = fixture.Event("broken-recorded-time");
        fixture.Intake.Accept(broken); fixture.Intake.Accept(fixture.Event("valid-row-after-broken-time"));
        fixture.ChangeOutboxRecorded(broken.BusinessKey + ":committed", "not-a-timestamp");
        var before = fixture.Snapshot(); var report = fixture.Reconciler.Inspect(fixture.Tenant);
        check(report.Status == "issues" && report.Coverage == "complete" && report.Counts.Operations == 2
            && report.Findings.Any(x => x.Code == "usage-outbox-timestamp-invalid" && x.LedgerKey == broken.BusinessKey && x.OutboxId.HasValue),
            "malformed committed outbox timestamp yields a scoped finding while later valid evidence is still scanned");
        var recovery = new MeasuredUsageRecoveryStore(fixture.Database, fixture.Intake, fixture.Clock);
        Reject<InvalidDataException>(() => recovery.Recover(fixture.Tenant, broken.BusinessKey, broken.OperationFingerprint, Guid.NewGuid(), "finance", "malformed timestamp"), check,
            "accounting recovery rejects malformed retained outbox timestamps without acknowledging them");
        check(fixture.Snapshot() == before, "malformed timestamp inspection and rejected recovery never rewrite retained outbox bytes");
    }

    private static void AccountingRecovery(string directory, Action<bool, string> check)
    {
        using var fixture = new ReconciliationFixture(directory, "recovery", 0); var usage = fixture.Event("recover-low-credit");
        fixture.Intake.Accept(usage, true); fixture.Intake.Accept(Alias(usage), true);
        var report = fixture.Reconciler.Inspect(fixture.Tenant);
        check(report.Coverage == "complete" && report.Status == "issues" && report.IntegrityVerified && report.CanRecoverPending
            && report.Counts.Pending == 1 && report.Findings.Single().Code == "accounting-pending",
            "intact pending usage is financially unresolved and explicitly eligible for reviewed accounting recovery");
        var summary = report.Operations.Single();
        check(summary.LedgerKey == usage.BusinessKey && summary.OperationFingerprint == usage.OperationFingerprint
            && summary.Status == "accounting-pending" && summary.Amount == 12 && summary.Currency == "IRR" && summary.OccurredAt == usage.OccurredAt,
            "pending reconciliation exposes the exact retained fingerprint amount currency and occurrence for reviewed recovery");
        var recovery = new MeasuredUsageRecoveryStore(fixture.Database, fixture.Intake, fixture.Clock); var request = Guid.NewGuid();
        var failed = recovery.Recover(fixture.Tenant, usage.BusinessKey, usage.OperationFingerprint, request, "finance", "review low credit");
        check(failed.PreviousStatus == "accounting-pending" && failed.Status == "accounting-pending" && failed.Amount == 12
            && fixture.Accounting.Read(fixture.Tenant).Outstanding == 0,
            "reviewed recovery retains pending historical evidence when credit remains insufficient");
        var count = fixture.Outbox().Length;
        check(recovery.Recover(fixture.Tenant, usage.BusinessKey, usage.OperationFingerprint, request, "finance", "review low credit") == failed
            && fixture.Outbox().Length == count, "same accounting recovery request returns its durable historical receipt without repeated audit");
        Reject<InvalidOperationException>(() => recovery.Recover(fixture.Tenant, usage.BusinessKey, usage.OperationFingerprint, request, "other-finance", "review low credit"), check,
            "accounting recovery request identity cannot change its actor");
        Reject<InvalidOperationException>(() => recovery.Recover(fixture.Tenant, usage.BusinessKey, usage.OperationFingerprint, request, "finance", "different reason"), check,
            "accounting recovery request identity cannot change its audit reason");
        fixture.Configure(fixture.Tenant, 1000, "credit-restored"); fixture.Validator.Allow = false; var validationCalls = fixture.Validator.Calls;
        check(recovery.Recover(fixture.Tenant, usage.BusinessKey, usage.OperationFingerprint, request, "finance", "review low credit") == failed,
            "reusing failed recovery request after credit correction returns original result and requires a new request");
        var successRequest = Guid.NewGuid();
        var success = recovery.Recover(fixture.Tenant, usage.BusinessKey, usage.OperationFingerprint, successRequest, "finance", "credit approved");
        var charge = fixture.Accounting.Read(fixture.Tenant).Account.Entries.Single(x => x.Kind == "usage");
        check(success.Status == "committed" && success.PreviousStatus == "accounting-pending" && success.Amount == 12 && charge.UnitPrice == 12
            && charge.UsageOccurredAt == usage.OccurredAt && fixture.Accounting.Read(fixture.Tenant).Account.Tariffs.Single().UnitPrice == 18
            && fixture.Validator.Calls == validationCalls && fixture.Database.All<MeasuredEventReceipt>("measured.event").Length == 2,
            "new reviewed recovery appends original historical rate without backend execution evolving validation or new event aliases");
        count = fixture.Outbox().Length;
        check(recovery.Recover(fixture.Tenant, usage.BusinessKey, usage.OperationFingerprint, successRequest, "finance", "credit approved") == success
            && fixture.Outbox().Length == count && fixture.Outbox().Count(x => x.Kind == "UsageCommitted") == 1
            && fixture.Outbox().Count(x => x.Kind == "UsageAccountingRecoveryAttempted") == 2
            && fixture.Reconciler.Inspect(fixture.Tenant).Status == "clean",
            "successful accounting recovery charges and emits once with stable audit receipts and clean reconciliation");

        using var missing = new ReconciliationFixture(directory, "recovery-unavailable"); var unavailable = missing.Event("unavailable");
        var tenant = missing.Access.GetTenants().Single(x => x.Id == missing.Tenant);
        missing.Put("tenant", tenant.Id.ToString(), tenant with { IsActive = false }); missing.Intake.Accept(unavailable, true);
        var missingRecovery = new MeasuredUsageRecoveryStore(missing.Database, missing.Intake, missing.Clock);
        check(missingRecovery.Recover(missing.Tenant, unavailable.BusinessKey, unavailable.OperationFingerprint, Guid.NewGuid(), "finance", "account unavailable").Status == "accounting-pending",
            "reviewed recovery cannot override an unavailable consumer account");
        missing.Put("tenant", tenant.Id.ToString(), tenant);
        check(missingRecovery.Recover(missing.Tenant, unavailable.BusinessKey, unavailable.OperationFingerprint, Guid.NewGuid(), "finance", "account restored").Status == "committed"
            && missing.Accounting.Read(missing.Tenant).Outstanding == 12,
            "account restoration permits reviewed append of the retained original event exactly once");

        using var unprovisioned = new ReconciliationFixture(directory, "recovery-no-account");
        var newConsumer = unprovisioned.Access.CreateTenant("unprovisioned-consumer", "Unprovisioned Consumer").Id;
        var noAccount = unprovisioned.Event("account-not-provisioned", newConsumer); unprovisioned.Intake.Accept(noAccount, true);
        var unprovisionedRecovery = new MeasuredUsageRecoveryStore(unprovisioned.Database, unprovisioned.Intake, unprovisioned.Clock);
        check(unprovisioned.Reconciler.Inspect(newConsumer).CanRecoverPending
            && unprovisionedRecovery.Recover(newConsumer, noAccount.BusinessKey, noAccount.OperationFingerprint, Guid.NewGuid(), "finance", "account not provisioned").Status == "accounting-pending"
            && unprovisioned.Database.Read<BillingAccount>("accounting", newConsumer.ToString()) is null,
            "missing accounting provision keeps intact original usage pending without silently creating an account");
        unprovisioned.Configure(newConsumer, 1000, "provisioned-postpaid");
        check(unprovisionedRecovery.Recover(newConsumer, noAccount.BusinessKey, noAccount.OperationFingerprint, Guid.NewGuid(), "finance", "account provisioned").Status == "committed"
            && unprovisioned.Accounting.Read(newConsumer).Outstanding == 12 && unprovisioned.Outbox().Count(x => x.Kind == "UsageCommitted") == 1,
            "explicit account provision allows one reviewed append of retained historical usage");

        using var invoiced = new ReconciliationFixture(directory, "recovery-invoiced"); invoiced.Clock.Now = invoiced.Clock.Now.AddMonths(1);
        invoiced.Accounting.Execute(invoiced.Tenant, new(invoiced.Accounting.Read(invoiced.Tenant).Account.Revision, "invoice", "october", null, null, 0,
            null, null, 0, 0, 0, "test", "2026-10", null), "finance");
        var late = invoiced.Event("issued-period"); invoiced.Intake.Accept(late, true);
        var beforeInvoice = JsonSerializer.Serialize(invoiced.Accounting.Read(invoiced.Tenant).Account);
        var issuedRecovery = new MeasuredUsageRecoveryStore(invoiced.Database, invoiced.Intake, invoiced.Clock);
        check(issuedRecovery.Recover(invoiced.Tenant, late.BusinessKey, late.OperationFingerprint, Guid.NewGuid(), "finance", "issued invoice review").Status == "accounting-pending"
            && JsonSerializer.Serialize(invoiced.Accounting.Read(invoiced.Tenant).Account) == beforeInvoice
            && invoiced.Outbox().All(x => x.Kind != "UsageCommitted"),
            "reviewed recovery preserves issued invoice and leaves late usage pending for approved financial adjustment");

        using var corrupt = new ReconciliationFixture(directory, "recovery-corrupt", 0); var damaged = corrupt.Event("damaged"); corrupt.Intake.Accept(damaged, true);
        corrupt.ChangeReceipt(damaged, receipt => receipt with { Fingerprint = UsageEventV1.Hash("damaged") });
        var rejectedRecovery = new MeasuredUsageRecoveryStore(corrupt.Database, corrupt.Intake, corrupt.Clock); var beforeCorrupt = corrupt.Snapshot();
        Reject<InvalidDataException>(() => rejectedRecovery.Recover(corrupt.Tenant, damaged.BusinessKey, damaged.OperationFingerprint, Guid.NewGuid(), "finance", "corrupt evidence"), check,
            "accounting recovery rejects inconsistent retained receipt evidence before appending");
        check(corrupt.Snapshot() == beforeCorrupt, "rejected corrupt recovery writes no recovery receipt audit ledger event or outbox");
        Reject<KeyNotFoundException>(() => rejectedRecovery.Recover(corrupt.Tenant, "measured:v1:" + UsageEventV1.Hash("missing"), damaged.OperationFingerprint, Guid.NewGuid(), "finance", "missing evidence"), check,
            "accounting recovery accepts only retained operations and cannot create an event");
        using var wrong = new ReconciliationFixture(directory, "recovery-identity", 0); var wrongUsage = wrong.Event("identity"); wrong.Intake.Accept(wrongUsage, true);
        var identityRecovery = new MeasuredUsageRecoveryStore(wrong.Database, wrong.Intake, wrong.Clock);
        Reject<InvalidOperationException>(() => identityRecovery.Recover(wrong.Tenant, wrongUsage.BusinessKey, UsageEventV1.Hash("wrong-fingerprint"), Guid.NewGuid(), "finance", "wrong fingerprint"), check,
            "accounting recovery requires the exact retained operation fingerprint");
        Reject<UnauthorizedAccessException>(() => identityRecovery.Recover(wrong.Owner, wrongUsage.BusinessKey, wrongUsage.OperationFingerprint, Guid.NewGuid(), "finance", "wrong tenant"), check,
            "accounting recovery cannot use a retained operation belonging to another consumer");
        wrong.DeleteOutbox(wrongUsage.BusinessKey + ":pending"); var missingPendingSnapshot = wrong.Snapshot();
        Reject<InvalidDataException>(() => identityRecovery.Recover(wrong.Tenant, wrongUsage.BusinessKey, wrongUsage.OperationFingerprint, Guid.NewGuid(), "finance", "missing pending outbox"), check,
            "accounting recovery requires the retained pending outbox evidence as well as journals");
        check(wrong.Snapshot() == missingPendingSnapshot, "missing pending outbox rejection adds no recovery receipt or financial effect");

        using var unknown = new ReconciliationFixture(directory, "recovery-incomplete", 0); var retained = unknown.Event("retained"); unknown.Intake.Accept(retained, true);
        unknown.RawDocument("measured.event", "unknown-event", "{not-json"); var unknownSnapshot = unknown.Snapshot();
        var incompleteRecovery = new MeasuredUsageRecoveryStore(unknown.Database, unknown.Intake, unknown.Clock);
        Reject<InvalidDataException>(() => incompleteRecovery.Recover(unknown.Tenant, retained.BusinessKey, retained.OperationFingerprint, Guid.NewGuid(), "finance", "incomplete evidence"), check,
            "accounting recovery refuses incomplete reconciliation coverage even when its selected event looks intact");
        check(unknown.Snapshot() == unknownSnapshot, "incomplete recovery does not replace journals or add an acknowledgement");

        using var concurrent = new ReconciliationFixture(directory, "recovery-concurrent", 0); var parallelUsage = concurrent.Event("parallel-recovery");
        concurrent.Intake.Accept(parallelUsage, true); concurrent.Configure(concurrent.Tenant, 1000, "parallel-credit");
        concurrent.Validator.Allow = false; var parallelValidationCalls = concurrent.Validator.Calls;
        var parallelRecovery = new MeasuredUsageRecoveryStore(concurrent.Database, concurrent.Intake, concurrent.Clock);
        var attempts = new System.Collections.Concurrent.ConcurrentBag<MeasuredUsageRecoveryReceipt>();
        Parallel.For(0, 2, i => attempts.Add(parallelRecovery.Recover(concurrent.Tenant, parallelUsage.BusinessKey, parallelUsage.OperationFingerprint,
            Guid.NewGuid(), "finance-" + i, "concurrent reviewed retry")));
        check(attempts.Count(x => x.PreviousStatus == "accounting-pending" && x.Status == "committed") == 1
            && attempts.Count(x => x.PreviousStatus == "committed" && x.Status == "committed") == 1
            && concurrent.Accounting.Read(concurrent.Tenant).Account.Entries.Count(x => x.Kind == "usage") == 1
            && concurrent.Outbox().Count(x => x.Kind == "UsageCommitted") == 1
            && concurrent.Outbox().Count(x => x.Kind == "UsageAccountingRecoveryAttempted") == 2
            && concurrent.Validator.Calls == parallelValidationCalls,
            "distinct concurrent reviewed recovery requests serialize into one charge one committed event and two durable audit receipts");
    }

    private static void SnapshotConsistency(string directory, Action<bool, string> check)
    {
        using var fixture = new ReconciliationFixture(directory, "snapshot-consistency"); var usage = fixture.Event("snapshot"); fixture.Intake.Accept(usage);
        using var writer = new FabricDatabase(fixture.Path);
        var original = fixture.Database.Read<MeasuredUsageOperation>("measured.operation", usage.BusinessKey)!;
        var pinned = fixture.Database.ReadSnapshot(() =>
        {
            var first = fixture.Database.Read<MeasuredUsageOperation>("measured.operation", usage.BusinessKey)!;
            writer.Transaction(() => { writer.Put("measured.operation", usage.BusinessKey, original with { Amount = 99 }); return true; });
            var second = fixture.Database.Read<MeasuredUsageOperation>("measured.operation", usage.BusinessKey)!;
            return first == second && first == original;
        });
        check(pinned && fixture.Database.Read<MeasuredUsageOperation>("measured.operation", usage.BusinessKey)!.Amount == 99,
            "Fabric read snapshot stays pinned across a second connection commit and later reads see the new state");
        Reject<SqliteException>(() => fixture.Database.ReadSnapshot(() =>
        { fixture.Database.Put("snapshot-test", "forbidden", "write"); return true; }), check,
            "SQLite query-only Fabric snapshot rejects an attempted document write");
        check(fixture.Database.Read<string>("snapshot-test", "forbidden") is null && !fixture.Database.IsInTransaction,
            "failed snapshot write leaves no document and releases the thread-local transaction context");
        Reject<InvalidOperationException>(() => fixture.Database.Transaction(() => fixture.Reconciler.Inspect(fixture.Tenant)), check,
            "public reconciliation cannot report uncommitted evidence inside an ambient writer");
    }

    private static void AdministrationBoundary(string directory, Action<bool, string> check)
    {
        using var fixture = new ReconciliationFixture(directory, "admin-boundary", 0); var usage = fixture.Event("admin"); fixture.Intake.Accept(usage, true);
        var recovery = new MeasuredUsageRecoveryStore(fixture.Database, fixture.Intake, fixture.Clock);
        var api = new UsageAccountingRecoveryApi(fixture.Reconciler, recovery)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
            { User = new ClaimsPrincipal(new ClaimsIdentity([new("sub", "finance")], "synthetic-cookie")) } }
        };
        var authorization = typeof(UsageAccountingRecoveryApi).GetCustomAttribute<AuthorizeAttribute>()!;
        var method = typeof(UsageAccountingRecoveryApi).GetMethod(nameof(UsageAccountingRecoveryApi.Recover))!;
        var size = method.CustomAttributes.Single(x => x.AttributeType == typeof(RequestSizeLimitAttribute)).ConstructorArguments.Single().Value;
        check(authorization.Policy == "PlatformAdmin" && authorization.AuthenticationSchemes == CookieAuthenticationDefaults.AuthenticationScheme
            && method.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>() is not null && Convert.ToInt64(size) == 16384,
            "accounting recovery API retains Cookie PlatformAdmin anti-forgery and bounded request metadata");
        check(api.Inspect(fixture.Tenant) is OkObjectResult && api.Inspect(fixture.Tenant, 0) is BadRequestObjectResult,
            "accounting reconciliation API maps valid inspection and invalid scan bounds");
        check(api.Recover(fixture.Tenant, new(usage.BusinessKey, usage.OperationFingerprint, Guid.Empty, "invalid request")) is BadRequestObjectResult,
            "accounting recovery API maps malformed request identity to a client error");
        check(api.Recover(fixture.Tenant, new("measured:v1:" + UsageEventV1.Hash("missing"), usage.OperationFingerprint, Guid.NewGuid(), "missing")) is NotFoundObjectResult,
            "accounting recovery API reports missing retained evidence without accepting a new event");
        check(api.Recover(fixture.Owner, new(usage.BusinessKey, usage.OperationFingerprint, Guid.NewGuid(), "wrong tenant")) is ForbidResult,
            "accounting recovery API forbids a retained event consumer mismatch");
        check(api.Recover(fixture.Tenant, new(usage.BusinessKey, UsageEventV1.Hash("wrong"), Guid.NewGuid(), "fingerprint conflict")) is ConflictObjectResult,
            "accounting recovery API maps retained fingerprint conflict without mutating the selected event");
        var response = api.Recover(fixture.Tenant, new(usage.BusinessKey, usage.OperationFingerprint, Guid.NewGuid(), "reviewed pending")) as OkObjectResult;
        check(response?.Value is MeasuredUsageRecoveryReceipt { Actor: "finance", Status: "accounting-pending" },
            "accounting recovery API derives its audit actor from authenticated controller identity");
    }

    private static UsageEventV1 Alias(UsageEventV1 usage) => usage with
    { EventId = Guid.NewGuid(), ProducedAt = usage.ProducedAt.AddSeconds(1), CorrelationId = Guid.NewGuid(), Operation = usage.Operation with { RequestId = Guid.NewGuid() } };
    private static void Reject<T>(Action action, Action<bool, string> check, string label) where T : Exception
    { try { action(); } catch (T) { check(true, label); return; } throw new Exception("Expected rejection: " + label); }

    private sealed class ReconciliationFixture : IDisposable
    {
        private static readonly DateTimeOffset AcceptedAt = new(2026, 10, 10, 6, 30, 0, TimeSpan.Zero);
        public string Path { get; }
        public FabricDatabase Database { get; }
        public PersistentAccessManagement Access { get; }
        public Guid Owner { get; }
        public Guid Tenant { get; }
        public FixtureClock Clock { get; } = new(AcceptedAt.AddHours(2));
        public AccountingStore Accounting { get; }
        public FixtureValidator Validator { get; } = new();
        public MeasuredUsageStore Intake { get; }
        public UsageReconciliationStore Reconciler { get; }
        public ReconciliationFixture(string directory, string name, decimal credit = 10000)
        {
            var root = System.IO.Path.Combine(directory, "reconciliation-" + name); Directory.CreateDirectory(root); Path = System.IO.Path.Combine(root, "fabric.db");
            var settings = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Platform:RegistryPath"] = System.IO.Path.Combine(root, "registry.db") }).Build();
            var registry = new PlatformRegistry(settings); Database = new FabricDatabase(Path);
            Access = new PersistentAccessManagement(Database, new InMemoryAccessManagement(registry)); Owner = Access.GetTenants().Single().Id;
            Tenant = Access.CreateTenant("consumer", "Consumer").Id; registry.SetTenantSource(Access.GetTenants); registry.SetMembershipSource(Access.GetUsers);
            registry.Register(new(Guid.NewGuid(), "organization-fabric", "Organization", "organization", "fanasa.ayin", null, null, Owner, "fanasa"), "test");
            Accounting = new AccountingStore(root, Access, Clock, Database); Configure(Tenant, credit, "configure");
            Accounting.Execute(Tenant, new(Accounting.Read(Tenant).Account.Revision, "tariff", "current-v8", null, null, 0,
                "organization-fabric", "organization.change", 18, 1, 0, "test", null, null), "test");
            Intake = new MeasuredUsageStore(Database, Accounting, Validator, Clock); Reconciler = new UsageReconciliationStore(Database);
        }
        public void Configure(Guid consumer, decimal credit, string key) => Accounting.Execute(consumer,
            new(Accounting.Read(consumer).Account.Revision, "configure", key, "postpaid", "IRR", credit, null, null, 0, 0, 0, "test", null, null), "test");
        public UsageEventV1 Event(string operation, Guid? consumer = null) => new("1.0", Guid.NewGuid(), AcceptedAt.AddMinutes(1), AcceptedAt.AddMinutes(1).AddSeconds(1),
            new("synthetic-backend", "product-backend", "synthetic-host"), "development", Owner, consumer ?? Tenant, "organization-fabric", "test-contract", 7,
            "test-allocation", "2026-10", new(operation, "organization.unit.save/v1", "synthetic-member", Guid.NewGuid(), UsageEventV1.Hash("request:" + operation),
                AcceptedAt, "organization-unit-committed/v1"), new("decision:" + operation, "organization-permission/v1", "credit:" + operation),
            new("organization.change", "1", "change", "1"), new("historical-price", 7, AcceptedAt.AddDays(-1), AcceptedAt.AddHours(1), "12", "IRR", 0, "away-from-zero", "postpaid"),
            "completed", new("receipt:" + operation, UsageEventV1.Hash("result:" + operation), "fabric://test/" + operation), Guid.NewGuid());
        public void Put<T>(string kind, string key, T value) => Database.Transaction(() => { Database.Put(kind, key, value); return true; });
        public void ChangeAccount(Func<BillingAccount, BillingAccount> change) => Put("accounting", Tenant.ToString(), change(Accounting.Read(Tenant).Account));
        public void ChangeEntry(UsageEventV1 usage, string kind, Func<LedgerEntry, LedgerEntry> change) => ChangeAccount(account => account with
        { Entries = account.Entries.Select(entry => entry.Key == usage.BusinessKey && entry.Kind == kind ? change(entry) : entry).ToArray() });
        public void ChangeOperation(UsageEventV1 usage, Func<MeasuredUsageOperation, MeasuredUsageOperation> change) => Put("measured.operation", usage.BusinessKey,
            change(Database.Read<MeasuredUsageOperation>("measured.operation", usage.BusinessKey)!));
        public void ChangeReceipt(UsageEventV1 usage, Func<MeasuredEventReceipt, MeasuredEventReceipt> change) => Put("measured.event", usage.EventKey,
            change(Database.Read<MeasuredEventReceipt>("measured.event", usage.EventKey)!));
        public void DeleteDocument(string kind, string key) => Sql("DELETE FROM fabric_documents WHERE kind=$kind AND key=$key", ("$kind", kind), ("$key", key));
        public void MoveDocument(string kind, string key, string physicalKey) => Sql("UPDATE fabric_documents SET key=$physical WHERE kind=$kind AND key=$key",
            ("$physical", physicalKey), ("$kind", kind), ("$key", key));
        public void DeleteOutbox(string key) => Sql("DELETE FROM fabric_outbox WHERE key=$key", ("$key", key));
        public void ChangeOutboxKey(string key, string physicalKey) => Sql("UPDATE fabric_outbox SET key=$physical WHERE key=$key", ("$physical", physicalKey), ("$key", key));
        public void ChangeOutboxRecorded(string key, string recorded) => Sql("UPDATE fabric_outbox SET recorded=$recorded WHERE key=$key", ("$recorded", recorded), ("$key", key));
        public void ChangeOutbox(string key, UsageEventV1 usage, decimal amount)
        {
            using var wire = JsonDocument.Parse(usage.ToWireJson());
            var payload = JsonSerializer.Serialize(new { TenantId = usage.ConsumerTenantId, LedgerKey = usage.BusinessKey, Status = "committed", Amount = amount, Usage = wire.RootElement.Clone() });
            Sql("UPDATE fabric_outbox SET payload=$payload WHERE key=$key", ("$payload", payload), ("$key", key));
        }
        public void RawDocument(string kind, string key, string json) => Sql("INSERT INTO fabric_documents(kind,key,json) VALUES($kind,$key,$json)",
            ("$kind", kind), ("$key", key), ("$json", json));
        public void RawOutbox(string key, string kind, string payload) => Sql("INSERT INTO fabric_outbox(key,kind,payload,recorded) VALUES($key,$kind,$payload,$recorded)",
            ("$key", key), ("$kind", kind), ("$payload", payload), ("$recorded", Clock.Now.ToString("O")));
        public FabricOutbox[] Outbox()
        {
            var result = new List<FabricOutbox>(); long after = 0;
            while (true) { var page = Database.Outbox(after); if (page.Length == 0) return result.ToArray(); result.AddRange(page); after = page[^1].Id; }
        }
        public string Snapshot()
        {
            using var connection = Open(); using var command = connection.CreateCommand(); var text = new StringBuilder();
            command.CommandText = "SELECT kind,key,json FROM fabric_documents ORDER BY kind,key";
            using (var reader = command.ExecuteReader()) while (reader.Read()) text.AppendLine(JsonSerializer.Serialize(new[] { reader.GetString(0), reader.GetString(1), reader.GetString(2) }));
            command.CommandText = "SELECT id,key,kind,payload,recorded FROM fabric_outbox ORDER BY id";
            using (var reader = command.ExecuteReader()) while (reader.Read()) text.AppendLine(JsonSerializer.Serialize(new object[] { reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4) }));
            return text.ToString();
        }
        private SqliteConnection Open() { var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false }.ToString()); connection.Open(); return connection; }
        private void Sql(string statement, params (string Name, string Value)[] parameters)
        {
            using var connection = Open(); using var command = connection.CreateCommand(); command.CommandText = statement;
            foreach (var (name, value) in parameters) command.Parameters.AddWithValue(name, value); command.ExecuteNonQuery();
        }
        public void Dispose() => Database.Dispose();
    }
    private sealed class FixtureValidator : IUsageSnapshotValidator
    {
        public bool Allow { get; set; } = true;
        public int Calls { get; private set; }
        public void ValidateNew(UsageEventV1 message) { Calls++; if (!Allow) throw new UnauthorizedAccessException("Current snapshot changed."); }
    }
    private sealed class FixtureClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
