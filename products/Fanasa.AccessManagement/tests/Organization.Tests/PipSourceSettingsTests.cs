using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Json;
using Fanasa.AccessManagement.Web.Api;
using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Governance;
using Fanasa.AccessManagement.Web.Organization;
using Fanasa.AccessManagement.Web.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

static class PipSourceSettingsTests
{
    public static void Run(string directory, Action<bool, string> check)
    {
        var path = Path.Combine(directory, "pip-source-settings", "fabric.db");
        using var db = new FabricDatabase(path);
        var access = new PersistentAccessManagement(db, new InMemoryAccessManagement());
        var tenant = access.CreateTenant("pip-settings", "PIP settings").Id;
        var other = access.CreateTenant("pip-settings-other", "Other").Id;
        access.AddUser(new(tenant, "alice", "Alice"));
        var store = new PipStore(db, access);
        var clock = new TestClock(new(2026, 10, 26, 8, 0, 0, TimeSpan.Zero));
        var source = store.Register(tenant, "Original HR", "https://hr.example.test/attributes", "hr.example.test", "hr-reader",
            maxAgeSeconds: 300, syncSubjects: ["alice"], pollIntervalSeconds: 60);
        var transport = new PipTransportOptions { Bindings = [new() { TenantId = tenant, Endpoint = source.Endpoint, SecretReference = source.SecretReference }] };
        var connector = new Connector(clock);
        var diagnostics = new PipDiagnostics(store, connector, transport, db, clock);
        var sync = new PipSyncService(store, connector, clock);
        var policies = new AccessPolicyStore(db, access, pip: store);
        var policy = policies.Draft(tenant, new(0, "Employment", "attribute", "organization.read", "Permit", null, null,
            null, null, null, null, "settings fixture", AttributeName: "employment.active", AttributeValue: "true", PipSourceId: source.Id), "author");
        diagnostics.TestAsync(tenant, source.Id, "alice", "manager", "verify source").GetAwaiter().GetResult();
        var active = store.EnableAfterTest(tenant, source.Id, true, "manager", "activate source", transport, clock.Now);
        var oldRun = sync.SyncAsync(tenant, source.Id, "alice").GetAwaiter().GetResult();
        check(oldRun.Status == "succeeded" && store.Read(tenant, "alice", clock.Now, source.Id).IsFresh,
            "settings fixture starts with operational evidence and checkpoint");

        RejectWithoutMutation<PipSourceSettingsConflictException>(() => store.UpdateSettings(tenant, source.Id, active.Generation, "not allowed"),
            store, db, tenant, source.Id, check, "enabled source settings are rejected atomically", "source-must-be-disabled");
        var disabled = store.SetEnabled(tenant, source.Id, false, "manager", "pause before correction");
        var receipt = diagnostics.TestAsync(tenant, source.Id, "alice", "manager", "verify disabled generation").GetAwaiter().GetResult();
        check(receipt.CanEnable, "disabled source has an activation receipt before settings correction");
        var pendingResponse = new TaskCompletionSource<PipFetchedSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
        connector.PendingResponse = pendingResponse.Task;
        var pendingTest = diagnostics.TestAsync(tenant, source.Id, "alice", "manager", "test before settings correction");
        connector.PendingResponse = null;
        var subjects = new[] { "alice", "bob" };
        var beforeOutbox = db.Outbox().Length;
        var updated = store.UpdateSettings(tenant, source.Id, disabled.Generation, "  Corrected HR  ", 180, 15, 65536,
            subjects, 45, "operator", "add a subject and shorten freshness");
        pendingResponse.SetResult(connector.FetchAsync(disabled, "alice").GetAwaiter().GetResult());
        var lateTest = pendingTest.GetAwaiter().GetResult();
        check(lateTest is { Status: "failed", ErrorCode: "source-changed", CanEnable: false, Attributes.Length: 0 }
            && !diagnostics.Setup(updated, clock.Now).CanEnable,
            "late connection test for previous settings generation cannot authorize the corrected source");
        subjects[0] = "mutated-after-call";
        check(updated.Id == source.Id && updated.TenantId == tenant && updated.CreatedAt == source.CreatedAt
            && updated.Endpoint == source.Endpoint && updated.AllowedHost == source.AllowedHost
            && updated.SecretReference == source.SecretReference && updated.SchemaVersion == source.SchemaVersion
            && updated.Definitions!.SequenceEqual(source.Definitions!), "settings preserve source identity and its authority contract");
        check(updated.Name == "Corrected HR" && updated.MaxAgeSeconds == 180 && updated.TimeoutSeconds == 15
            && updated.MaxResponseBytes == 65536 && updated.PollIntervalSeconds == 45
            && updated.SyncSubjects!.SequenceEqual(["alice", "bob"]), "settings update accepts polling controls and copies subject input");
        check(updated.Generation == disabled.Generation + 1 && !updated.Enabled && updated.Status == "disabled"
            && updated.LastSyncAt is null && updated.LastSourceVersion is null && updated.LastEtag is null && updated.LastError is null,
            "settings create a disabled generation without stale current synchronization summaries");
        var audit = db.Outbox().Last();
        using (var json = JsonDocument.Parse(audit.Payload))
        {
            check(db.Outbox().Length == beforeOutbox + 1 && audit.Kind == "PipSourceSettingsChanged"
                && json.RootElement.GetProperty("Actor").GetString() == "operator"
                && json.RootElement.GetProperty("PreviousGeneration").GetInt64() == disabled.Generation
                && json.RootElement.GetProperty("Generation").GetInt64() == updated.Generation
                && json.RootElement.EnumerateObject().Select(x => x.Name).Order().SequenceEqual(
                    new[] { "TenantId", "Id", "PreviousGeneration", "Generation", "Actor", "Reason" }.Order()),
                "settings audit records only identity generation actor and reason");
        }
        check(policies.Read(tenant).Revision == policy.Revision && policies.Read(tenant).Patterns.Single().PipSourceId == source.Id,
            "source settings preserve policy references and do not publish or edit policies");
        check(db.All<PipAttributeSnapshot>(PipStore.SnapshotDocumentKind).Length == 1
            && store.ListRuns(tenant, source.Id).Single().Id == oldRun.Id,
            "settings preserve retained snapshot and synchronization history");
        check(!diagnostics.Setup(updated, clock.Now).CanEnable && !store.Read(tenant, "alice", clock.Now, source.Id).IsFresh
            && store.Checkpoint(tenant, source.Id, "alice") is null,
            "settings invalidate old activation receipt snapshot and checkpoint");
        Reject<InvalidOperationException>(() => store.EnableAfterTest(tenant, source.Id, true, "manager", "old receipt", transport, clock.Now),
            check, "settings require a fresh successful connection test");
        var late = connector.FetchAsync(active, "alice").GetAwaiter().GetResult();
        Reject<InvalidOperationException>(() => store.SaveSnapshot(active, "alice", late, clock.Now), check,
            "previous generation cannot store a late synchronization response after settings change");
        store.RecordRun(oldRun with { Id = Guid.NewGuid(), StartedAt = clock.Now, CompletedAt = clock.Now });
        check(store.Checkpoint(tenant, source.Id, "alice") is null && store.ListRuns(tenant, source.Id).Length == 2,
            "late old completion stays in history without creating a current settings checkpoint");
        using (var reopened = new FabricDatabase(path))
        {
            var restarted = new PipStore(reopened, new PersistentAccessManagement(reopened, new InMemoryAccessManagement()));
            check(JsonSerializer.Serialize(restarted.ReadSource(tenant, source.Id)) == JsonSerializer.Serialize(updated),
                "settings and generation persist across a database and service restart");
        }

        foreach (var invalid in new Action[] {
            () => store.UpdateSettings(tenant, source.Id, 0, "wrong generation"),
            () => store.UpdateSettings(tenant, source.Id, updated.Generation, " "),
            () => store.UpdateSettings(tenant, source.Id, updated.Generation, "invalid age", maxAgeSeconds: 0),
            () => store.UpdateSettings(tenant, source.Id, updated.Generation, "invalid timeout", timeoutSeconds: 121),
            () => store.UpdateSettings(tenant, source.Id, updated.Generation, "invalid response cap", maxResponseBytes: 1023),
            () => store.UpdateSettings(tenant, source.Id, updated.Generation, "duplicate subjects", syncSubjects: ["alice", "alice"]),
            () => store.UpdateSettings(tenant, source.Id, updated.Generation, "invalid subject", syncSubjects: ["alice\n"]),
            () => store.UpdateSettings(tenant, source.Id, updated.Generation, "too many subjects", syncSubjects: Enumerable.Range(0, 101).Select(x => "subject" + x).ToArray()),
            () => store.UpdateSettings(tenant, source.Id, updated.Generation, "missing subjects", pollIntervalSeconds: 30),
            () => store.UpdateSettings(tenant, source.Id, updated.Generation, "short poll", syncSubjects: ["alice"], pollIntervalSeconds: 29),
            () => store.UpdateSettings(tenant, source.Id, updated.Generation, "expiry poll", maxAgeSeconds: 60, syncSubjects: ["alice"], pollIntervalSeconds: 60),
            () => store.UpdateSettings(tenant, source.Id, updated.Generation, "missing actor", actor: " "),
            () => store.UpdateSettings(tenant, source.Id, updated.Generation, "missing reason", reason: " ") })
            RejectWithoutMutation<ArgumentException>(invalid, store, db, tenant, source.Id, check,
                "invalid settings are rejected before source or audit changes");
        RejectWithoutMutation<PipSourceSettingsConflictException>(() => store.UpdateSettings(tenant, source.Id, disabled.Generation, "stale writer"),
            store, db, tenant, source.Id, check, "stale source generation cannot overwrite settings", "source-generation-conflict");
        RejectWithoutMutation<KeyNotFoundException>(() => store.UpdateSettings(other, source.Id, updated.Generation, "other tenant"),
            store, db, tenant, source.Id, check, "source settings remain tenant scoped");
        var educational = store.Register(tenant, "Educational", "https://example.invalid/pip", "example.invalid", null, educational: true);
        RejectWithoutMutation<PipSourceSettingsConflictException>(() => store.UpdateSettings(tenant, educational.Id, educational.Generation, "edited lesson"),
            store, db, tenant, educational.Id, check, "educational sources reject ordinary settings edits", "educational-source-read-only");

        using (var secondDb = new FabricDatabase(path))
        {
            var secondStore = new PipStore(secondDb, new PersistentAccessManagement(secondDb, new InMemoryAccessManagement()));
            var results = new ConcurrentBag<string>(); var audits = db.Outbox().Length;
            Parallel.For(0, 2, index =>
            {
                try { (index == 0 ? store : secondStore).UpdateSettings(tenant, source.Id, updated.Generation, "writer " + index,
                    syncSubjects: ["alice", "bob"], pollIntervalSeconds: 60, actor: "writer" + index, reason: "concurrent correction"); results.Add("saved"); }
                catch (PipSourceSettingsConflictException error) { results.Add(error.ErrorCode); }
            });
            check(results.Count(x => x == "saved") == 1 && results.Count(x => x == "source-generation-conflict") == 1
                && db.Outbox().Length == audits + 1 && store.ReadSource(tenant, source.Id).Generation == updated.Generation + 1,
                "two database writers produce exactly one settings generation and one audit");
        }
        diagnostics.TestAsync(tenant, source.Id, "alice", "manager", "test corrected source").GetAwaiter().GetResult();
        var reenabled = store.EnableAfterTest(tenant, source.Id, true, "manager", "enable corrected source", transport, clock.Now);
        check(store.NextDueAt(reenabled, "bob", clock.Now) == clock.Now && !store.Read(tenant, "alice", clock.Now, source.Id).IsFresh,
            "corrected schedule is immediately due and old evidence is not promoted on reenable");
        Reject<InvalidOperationException>(() => store.SaveSnapshot(active, "alice", late, clock.Now), check,
            "late old response remains rejected after the corrected source is enabled");
        check(sync.SyncAsync(tenant, source.Id, "alice").GetAwaiter().GetResult().Status == "succeeded"
            && store.Read(tenant, "alice", clock.Now, source.Id).IsFresh,
            "normal test enable and synchronize flow recovers evidence for corrected settings");
        ApiChecks(db, access, store, sync, diagnostics, transport, clock, tenant, other, source.Id, educational, check);
    }

    private static void ApiChecks(FabricDatabase db, PersistentAccessManagement access, PipStore store,
        PipSyncService sync, PipDiagnostics diagnostics, PipTransportOptions transport, TestClock clock,
        Guid tenant, Guid other, Guid sourceId, PipSourceDefinition educational, Action<bool, string> check)
    {
        var api = new PipApi(store, sync, transport, clock, diagnostics: diagnostics)
            { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        UpdatePipSourceSettingsCommand Command(long generation) => new(generation, "API settings", SyncSubjects: ["alice"], PollIntervalSeconds: 60, Reason: "operator correction");
        check(api.Settings(tenant, sourceId, Command(1)) is ForbidResult, "settings API rejects anonymous mutation");
        api.HttpContext.User = new(new ClaimsIdentity([new("sub", "manager"), new("tenant_id", tenant.ToString()), new("permission", "organization.read")], "test"));
        check(api.Settings(tenant, sourceId, Command(1)) is ForbidResult, "settings API requires tenant management permission");
        api.HttpContext.User = new(new ClaimsIdentity([new("sub", "manager"), new("tenant_id", tenant.ToString()), new("permission", "tenancy.manage")], "test"));
        check(api.Settings(other, sourceId, Command(1)) is ForbidResult, "settings API does not authorize a different tenant");
        check(Code(api.Settings(tenant, sourceId, Command(store.ReadSource(tenant, sourceId).Generation))) == "source-must-be-disabled",
            "settings API returns a stable disabled-source conflict code");
        var source = store.SetEnabled(tenant, sourceId, false);
        check(Code(api.Settings(tenant, sourceId, Command(source.Generation - 1))) == "source-generation-conflict",
            "settings API returns a stable generation conflict code");
        check(Code(api.Settings(tenant, educational.Id, Command(educational.Generation))) == "educational-source-read-only",
            "settings API returns a stable educational-source conflict code");
        check(api.Settings(tenant, sourceId, Command(0)) is BadRequestObjectResult
            && api.Settings(tenant, Guid.NewGuid(), Command(1)) is NotFoundResult,
            "settings API distinguishes invalid generation and missing source");
        check(api.Settings(tenant, sourceId, Command(source.Generation)) is OkObjectResult { Value: PipSourceDefinition { Enabled: false } },
            "settings API returns corrected disabled source using authenticated operator");
        check(typeof(PipApi).GetMethod(nameof(PipApi.Settings))!.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), true).Length == 1,
            "settings API requires antiforgery protection");
        var calendars = new OrganizationCalendarStore(db, access, clock);
        var templates = new TemplateOrganizationStore(db, access, calendars, new AccessPolicyStore(db, access, calendars, store), clock);
        var example = templates.Ensure("administrator", "prepare settings guard fixture");
        var templateSource = store.Register(example.Tenant.Id, "Protected template", "https://example.invalid/template", "example.invalid", null);
        api = new PipApi(store, sync, transport, clock, examples: templates, diagnostics: diagnostics)
            { ControllerContext = new() { HttpContext = new DefaultHttpContext { User = new(new ClaimsIdentity([new("sub", "administrator"), new("permission", "platform.admin")], "test")) } } };
        var before = JsonSerializer.Serialize(store.ReadSource(example.Tenant.Id, templateSource.Id)); var auditCount = db.Outbox().Length;
        check(api.Settings(example.Tenant.Id, templateSource.Id, Command(templateSource.Generation)) is ConflictObjectResult
            && JsonSerializer.Serialize(store.ReadSource(example.Tenant.Id, templateSource.Id)) == before && db.Outbox().Length == auditCount,
            "template organization guard blocks settings changes even for platform administrators");
    }

    private static string? Code(IActionResult result) => result is ConflictObjectResult conflict
        ? JsonSerializer.SerializeToElement(conflict.Value).GetProperty("errorCode").GetString() : null;
    private static void Reject<T>(Action action, Action<bool, string> check, string label) where T : Exception
    {
        try { action(); } catch (T) { check(true, label); return; }
        throw new Exception("Expected rejection: " + label);
    }
    private static void RejectWithoutMutation<T>(Action action, PipStore store, FabricDatabase database, Guid tenant, Guid source,
        Action<bool, string> check, string label, string? code = null) where T : Exception
    {
        var before = JsonSerializer.Serialize(store.ReadSource(tenant, source)); var audits = database.Outbox().Length;
        try { action(); }
        catch (T error)
        {
            if (code is not null) check(error is PipSourceSettingsConflictException conflict && conflict.ErrorCode == code, label + " (code)");
            check(JsonSerializer.Serialize(store.ReadSource(tenant, source)) == before && database.Outbox().Length == audits, label);
            return;
        }
        throw new Exception("Expected rejection: " + label);
    }
    private sealed class Connector(TestClock clock) : IPipExternalConnector
    {
        private int sequence;
        public Task<PipFetchedSnapshot>? PendingResponse { get; set; }
        public Task<PipFetchedSnapshot> FetchAsync(PipSourceDefinition source, string subject, CancellationToken cancellationToken = default)
        {
            if (PendingResponse is not null) return PendingResponse;
            clock.Now = clock.Now.AddSeconds(1);
            var attributes = new Dictionary<string, string> { ["employment.active"] = "true", ["organization.unit"] = "engineering" };
            var version = "settings-" + ++sequence;
            return Task.FromResult(new PipFetchedSnapshot(source.TenantId, subject, source.SchemaVersion, attributes, version, "etag-" + sequence,
                clock.Now, PipStore.HashAttributes(source.TenantId, subject, source.SchemaVersion, attributes, version, clock.Now)));
        }
    }
}
