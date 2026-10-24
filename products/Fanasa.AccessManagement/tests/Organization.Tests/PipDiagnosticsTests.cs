using System.Security.Claims;
using System.Text.Json;
using Fanasa.AccessManagement.Web.Api;
using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Governance;
using Fanasa.AccessManagement.Web.Persistence;
using Fanasa.AccessManagement.Web.Organization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

static class PipDiagnosticsTests
{
    public static void Run(string directory, Action<bool, string> check)
    {
        using var db = new FabricDatabase(Path.Combine(directory, "pip-diagnostics", "fabric.db"));
        var access = new PersistentAccessManagement(db, new InMemoryAccessManagement());
        var tenant = access.CreateTenant("diagnostics", "Diagnostics").Id;
        var other = access.CreateTenant("diagnostics-other", "Other").Id;
        var clock = new TestClock(new(2026, 10, 23, 10, 0, 0, TimeSpan.Zero));
        var store = new PipStore(db, access);
        var source = store.Register(tenant, "HR", "https://hr.example.test/attributes", "hr.example.test", null,
            maxAgeSeconds: 600, syncSubjects: ["alice", "bob"], pollIntervalSeconds: 60);
        var transport = new PipTransportOptions { Bindings = [new() { TenantId = tenant, Endpoint = source.Endpoint }] };
        var connector = new Connector(clock);
        var service = new PipDiagnostics(store, connector, transport, db, clock);
        var sync = new PipSyncService(store, connector, clock);
        void Reject<T>(Action action, string name) where T : Exception
        { try { action(); } catch (T) { check(true, name); return; } throw new Exception("Expected rejection: " + name); }
        PipTestResult Test() => service.TestAsync(tenant, source.Id, "alice", "admin", "check contract").GetAwaiter().GetResult();
        PipSourceDefinition Enable() => store.EnableAfterTest(tenant, source.Id, true, "admin", "activate tested source", transport, clock.Now);
        void Disable() => store.EnableAfterTest(tenant, source.Id, false, "admin", "disable source", transport, clock.Now);
        check(service.Setup(source, clock.Now).State == "test-required" && !service.Setup(source, clock.Now).CanEnable,
            "untested approved source cannot be enabled");
        Reject<InvalidOperationException>(() => Enable(), "activation requires successful connection test");
        var result = Test();
        check(result.Status == "succeeded" && result.CanEnable && !result.SnapshotStored && result.ExpiresAt == clock.Now.AddMinutes(5),
            "successful connection test is short lived and explicitly not an authorization snapshot");
        check(connector.LastSource is { Enabled: true, LastEtag: null } && !store.ReadSource(tenant, source.Id).Enabled,
            "test temporarily enables transport only and does not mutate disabled source");
        check(db.All<PipAttributeSnapshot>(PipStore.SnapshotDocumentKind).Length == 0 && store.ListRuns(tenant, source.Id).Length == 0,
            "test never writes an operational snapshot or sync checkpoint");
        check(result.Attributes.Single(x => x.Name == "employment.active").Value == "true",
            "connection test returns validated typed values");
        var persisted = JsonSerializer.Serialize(db.All<PipTestReceipt>("pip.test"));
        check(!persisted.Contains("employment.active", StringComparison.Ordinal) && !persisted.Contains("engineering", StringComparison.Ordinal),
            "test receipt persists only metadata and audit information");
        check(new PipDiagnostics(new PipStore(db, access), connector, transport, db, clock).Setup(source, clock.Now).CanEnable,
            "activation receipt is durable across service recreation");
        transport.Bindings[0].AllowedPrivateAddresses = ["10.1.2.3"];
        Reject<InvalidOperationException>(() => Enable(), "deployment binding change invalidates connection test");
        transport.Bindings[0].AllowedPrivateAddresses = [];
        clock.Now = result.ExpiresAt;
        Reject<InvalidOperationException>(() => Enable(), "test expiry uses an exclusive deadline");
        _ = Test();
        connector.Failure = new HttpRequestException("sensitive remote response must never escape");
        result = Test();
        check(result.Status == "failed" && result.ErrorCode == "transport-failed" && result.Attributes.Length == 0 && !result.CanEnable,
            "connection failure returns safe code without response body or attributes");
        Reject<InvalidOperationException>(() => Enable(), "failed retest invalidates earlier successful receipt");
        connector.Failure = null;
        connector.WrongTenant = other;
        check(Test().ErrorCode == "invalid-response", "test rejects cross tenant response before activation");
        connector.WrongTenant = null;
        connector.WrongSubject = "mallory";
        check(Test().ErrorCode == "invalid-response", "test rejects wrong subject response before activation");
        connector.WrongSubject = null;
        connector.InvalidType = true;
        check(Test().ErrorCode == "invalid-response", "test uses operational type validation");
        connector.InvalidType = false;
        connector.BadHash = true;
        check(Test().ErrorCode == "invalid-response", "test uses operational content hash validation");
        connector.BadHash = false;
        connector.ObservationAge = 601;
        check(Test().ErrorCode == "invalid-response", "already stale response cannot pass connection test");
        connector.ObservationAge = 0;
        connector.OnFetch = () => { store.SetEnabled(tenant, source.Id, true); store.SetEnabled(tenant, source.Id, false); };
        check(Test().ErrorCode == "source-changed", "reconfiguration during fetch rejects activation receipt");
        connector.OnFetch = null;
        _ = Test();
        var active = Enable();
        check(active.Enabled && active.Generation > source.Generation && store.Read(tenant, "alice", clock.Now, source.Id).Status == "missing",
            "activation advances generation without converting test data to operational data");
        check(Enable().Generation == active.Generation, "already enabled activation is idempotent");
        var run = sync.SyncAsync(tenant, source.Id, "alice").GetAwaiter().GetResult();
        var status = service.Status(tenant, source.Id, "alice");
        check(run.Status == "succeeded" && status.Usable && status.FetchedAt == run.CompletedAt && status.NextDueAt == run.CompletedAt.AddSeconds(60),
            "subject diagnostics correlate snapshot provenance checkpoint and schedule");
        var requests = connector.Requests;
        var overview = service.Overview(tenant).Sources.Single();
        check(overview.Counts.Fresh == 1 && overview.Counts.Missing == 1 && overview.Subjects.Length == 2,
            "overview counts only explicit subjects and never promotes aggregate source success to every subject");
        check(connector.Requests == requests, "diagnostics reads never fetch external attributes");
        var approvedBindings = transport.Bindings;
        transport.Bindings = [];
        check(service.Status(tenant, source.Id, "alice") is { Usable: true, DeploymentApproved: false },
            "removing fetch approval does not falsely report still fresh PDP evidence as unusable");
        transport.Bindings = approvedBindings;
        check(service.StatusAt(tenant, source.Id, "alice", run.CompletedAt.AddTicks(-1)) is { LastRun: null, FetchedAt: null, NextDueAt: null },
            "historical simulation never attributes a future checkpoint or operational schedule to its evaluation time");
        clock.Now = status.FreshUntil!.Value;
        status = service.Status(tenant, source.Id, "alice");
        check(status.Status == "stale" && !status.Usable && status.Attributes.Any(x => x.Value is not null),
            "stale retained evidence is visibly unusable");
        var saved = db.All<PipAttributeSnapshot>(PipStore.SnapshotDocumentKind).Single();
        var key = $"{tenant:D}:{source.Id:D}:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("alice"))).ToLowerInvariant();
        db.Transaction(() => { db.Put(PipStore.SnapshotDocumentKind, key, saved with { ContentHash = "tampered" }); return true; });
        check(service.Status(tenant, source.Id, "alice") is { Status: "invalid", Usable: false, Attributes.Length: 0 },
            "invalid snapshot cannot expose raw values through diagnostics");
        Disable();
        check(!service.Setup(store.ReadSource(tenant, source.Id), clock.Now).CanEnable,
            "disable invalidates prior activation receipt by generation");
        Reject<KeyNotFoundException>(() => service.Status(other, source.Id, "alice"), "diagnostics source is tenant correlated");
        transport.Bindings = [];
        requests = connector.Requests;
        Reject<InvalidOperationException>(() => Test(), "deployment boundary is checked before test transport");
        check(connector.Requests == requests && service.Overview(tenant).Sources.Single().Setup.State == "deployment-required",
            "unapproved source explains deployment requirement without networking");

        var api = new PipApi(store, sync, transport, clock, diagnostics: service)
        { ControllerContext = new() { HttpContext = new DefaultHttpContext() } };
        check(api.Overview(tenant) is ForbidResult && api.Test(tenant, source.Id, new("alice", "test"), CancellationToken.None).Result is ForbidResult,
            "anonymous diagnostics and test requests are forbidden");
        api.HttpContext.User = new(new ClaimsIdentity([new("sub", "manager"), new("tenant_id", tenant.ToString()), new("permission", "tenancy.manage")], "test"));
        check(api.Overview(tenant) is OkObjectResult && api.Overview(other) is ForbidResult, "diagnostics API enforces tenant manage permission");
        check(api.Register(tenant, new("Bypass", source.Endpoint, source.AllowedHost, null, Enabled: true, Reason: "test bypass")) is BadRequestObjectResult,
            "source registration cannot bypass staged activation");
        check(new[] { nameof(PipApi.Test), nameof(PipApi.Journey) }.All(name => typeof(PipApi).GetMethod(name)!
            .GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), true).Length == 1),
            "connection tests and educational journey require antiforgery");
        access.AddUser(new(tenant, "alice", "Alice"));
        var organization = new OrganizationStore(directory, access, database: db);
        var policies = new AccessPolicyStore(db, access, pip: store);
        policies.Draft(tenant, new(0, "Active employment", "attribute", "organization.read", "Permit", null, null,
            null, null, null, null, "simulation explanation", AttributeName: "employment.active", AttributeValue: "true", PipSourceId: source.Id), "author");
        var governance = new GovernanceApi(policies, new IdentityLifecycle(db, access, organization, clock), access, organization, db, pip: store, diagnostics: service)
        { ControllerContext = new() { HttpContext = new DefaultHttpContext { User = new(new ClaimsIdentity([
            new("sub", "admin"), new("tenant_id", tenant.ToString()), new("permission", "platform.admin"),
            new("permission", "tenancy.read"), new("permission", "organization.read")], "test")) } } };
        var simulation = governance.Simulate(tenant, new("alice", "organization.read", clock.Now, true)) as OkObjectResult;
        check(simulation is not null, "simulation explanation shares one Fabric read snapshot without nested transaction failures");
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(simulation!.Value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var explanation = document.RootElement.GetProperty("explanation");
        check(explanation.GetProperty("policyRevision").GetInt64() == 1 && explanation.GetProperty("configurationOnly").GetBoolean()
            && !explanation.GetProperty("historicalSnapshotsReconstructed").GetBoolean()
            && explanation.GetProperty("sources").GetArrayLength() == 1,
            "simulation distinguishes configured rules from actual decision references and does not claim historical reconstruction");
        check(document.RootElement.GetProperty("result").GetProperty("decision").GetString() == "Indeterminate"
            && connector.Requests == requests, "explained decision preserves fail closed result and never fetches external data");
    }
    private sealed class Connector(TestClock clock) : IPipExternalConnector
    {
        public int Requests { get; private set; }
        public PipSourceDefinition? LastSource { get; private set; }
        public Exception? Failure { get; set; }
        public Guid? WrongTenant { get; set; }
        public string? WrongSubject { get; set; }
        public bool InvalidType { get; set; }
        public bool BadHash { get; set; }
        public int ObservationAge { get; set; }
        public Action? OnFetch { get; set; }
        public Task<PipFetchedSnapshot> FetchAsync(PipSourceDefinition source, string subject, CancellationToken cancellationToken = default)
        {
            Requests++; LastSource = source; OnFetch?.Invoke();
            if (Failure is not null) throw Failure;
            var tenant = WrongTenant ?? source.TenantId; subject = WrongSubject ?? subject;
            var observed = clock.Now.AddSeconds(-ObservationAge);
            var attributes = new Dictionary<string, string> { ["employment.active"] = InvalidType ? "yes" : "true", ["organization.unit"] = "engineering" };
            var version = "diagnostics-" + Requests;
            return Task.FromResult(new PipFetchedSnapshot(tenant, subject, 1, attributes, version, null, observed,
                BadHash ? "invalid" : PipStore.HashAttributes(tenant, subject, 1, attributes, version, observed)));
        }
    }
}
