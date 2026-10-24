using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Governance;
using Fanasa.AccessManagement.Web.Persistence;

static class PipSchema2Tests
{
    public static void Run(string directory, Action<bool, string> check)
    {
        using var db = new FabricDatabase(Path.Combine(directory, "pip-schema2", "fabric.db"));
        var access = new PersistentAccessManagement(db, new InMemoryAccessManagement());
        var tenant = access.CreateTenant("pip-schema2", "Schema 2").Id;
        access.AddUser(new(tenant, "alice", "Alice"));
        var pip = new PipStore(db, access);
        var source = pip.Register(tenant, "bounded facts", "https://schema2.example.test/attributes", "schema2.example.test", null,
            schemaVersion: 2, maxAgeSeconds: 300, enabled: true);
        var at = new DateTimeOffset(2026, 10, 24, 8, 0, 0, TimeSpan.Zero);
        var values = new Dictionary<string, string> { ["employment.active"] = "true", ["organization.unit"] = "engineering" };
        PipFetchedSnapshot Fetched(string subject, string version, DateTimeOffset? until, DateTimeOffset? observed = null, int schema = 2) =>
            new(tenant, subject, schema, values, version, "\"" + version + "\"", observed ?? at,
                PipStore.HashAttributes(tenant, subject, schema, values, version, observed ?? at, until), until);
        var fetched = Fetched("alice", "bounded-1", at.AddSeconds(20));
        var snapshot = pip.SaveSnapshot(source, "alice", fetched, at);
        check(snapshot.ValidUntil == at.AddSeconds(20) && snapshot.FreshUntil == at.AddSeconds(20),
            "schema 2 preserves the source deadline and caps local freshness at it");
        check(pip.Read(tenant, "alice", at.AddSeconds(19), source.Id).IsFresh
            && pip.Read(tenant, "alice", at.AddSeconds(20), source.Id).Status == "stale",
            "schema 2 deadline is exclusive even when local TTL remains open");
        var longLived = pip.SaveSnapshot(source, "long-lived", Fetched("long-lived", "bounded-long", at.AddHours(1)), at);
        check(longLived.FreshUntil == at.AddSeconds(300), "local TTL still caps a longer schema 2 upstream deadline");
        foreach (var invalid in new DateTimeOffset?[] { null, at, at.AddTicks(-1), at.AddSeconds(20).ToOffset(TimeSpan.FromHours(1)) })
            Reject<InvalidDataException>(() => pip.SaveSnapshot(source, "invalid", Fetched("invalid", "invalid", invalid), at), check,
                "schema 2 rejects missing, nonfuture or non-UTC deadline: " + (invalid?.ToString("O") ?? "missing"));
        Reject<InvalidDataException>(() => pip.SaveSnapshot(source, "expired", Fetched("expired", "expired", at.AddSeconds(1)), at.AddSeconds(1)), check,
            "schema 2 already-expired response cannot be persisted");
        var legacy = pip.Register(tenant, "legacy", "https://schema1.example.test/attributes", "schema1.example.test", null, enabled: true);
        Reject<InvalidDataException>(() => pip.SaveSnapshot(legacy, "legacy", Fetched("legacy", "legacy", at.AddSeconds(20), schema: 1), at), check,
            "schema 1 cannot smuggle an uncontracted deadline into stored evidence");
        check(PipStore.HashAttributes(tenant, "alice", 2, values, "bounded-1", at, at.AddSeconds(20))
            != PipStore.HashAttributes(tenant, "alice", 2, values, "bounded-1", at, at.AddSeconds(21)),
            "schema 2 content integrity binds the upstream deadline");
        var outbox = db.Outbox().Length;
        Reject<InvalidDataException>(() => pip.SaveSnapshot(source, "alice", Fetched("alice", "bounded-1", at.AddSeconds(30)), at), check,
            "reusing a source version with an extended deadline is rejected as changed content");
        check(db.Outbox().Length == outbox && pip.Read(tenant, "alice", at, source.Id).Snapshot?.ContentHash == snapshot.ContentHash,
            "rejected deadline replay does not replace evidence or emit snapshot events");

        var key = $"{tenant:D}:{source.Id:D}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("alice"))).ToLowerInvariant()}";
        void Tamper(PipAttributeSnapshot bad, string label)
        {
            db.Transaction(() => { db.Put(PipStore.SnapshotDocumentKind, key, bad); return true; });
            check(pip.Read(tenant, "alice", at, source.Id).Status == "invalid", label);
            db.Transaction(() => { db.Put(PipStore.SnapshotDocumentKind, key, snapshot); return true; });
        }
        Tamper(snapshot with { ValidUntil = at.AddSeconds(40) }, "read rejects a modified deadline with unchanged content hash");
        Tamper(snapshot with { FreshUntil = at.AddSeconds(300) }, "read rejects freshness metadata extended past source deadline");
        Tamper(snapshot with { ValidUntil = at.AddSeconds(40), ContentHash = PipStore.HashAttributes(tenant, "alice", 2, values, "bounded-1", at, at.AddSeconds(40)) },
            "read checks derived freshness even when tampered deadline has a recomputed hash");
        Tamper(snapshot with { ValidUntil = null }, "read rejects persisted schema 2 evidence without a deadline");

        var policies = new AccessPolicyStore(db, access, null, pip);
        var draft = policies.Draft(tenant, new(0, "deadline fixture", "attribute", "organization.read", "Permit", null, null, null, null, null, null,
            "source deadline is mandatory", AttributeName: "employment.active", AttributeValue: "true", PipSourceId: source.Id), "author");
        policies.Transition(tenant, draft.Patterns[^1].Id, draft.Revision, "publish", "reviewer", "independent review");
        access.SetGrant(new(tenant, "alice", "organization.read", null), "admin", "cannot bypass deadline");
        check(policies.Evaluate(tenant, "alice", "organization.read", at.AddSeconds(19)).Decision == "Permit"
            && policies.Evaluate(tenant, "alice", "organization.read", at.AddSeconds(20)).Decision == "Indeterminate",
            "PDP cannot use a direct grant to bypass an expired schema 2 source deadline");

        var clock = new MutableClock(at.AddSeconds(10));
        var seenEtag = "";
        var connector = new NotModifiedConnector(sourceSeen => seenEtag = sourceSeen.LastEtag ?? "");
        var sync = new PipSyncService(pip, connector, clock);
        var notModified = sync.SyncAsync(tenant, source.Id, "alice").GetAwaiter().GetResult();
        check(notModified.Status == "not-modified" && seenEtag == snapshot.Etag
            && pip.Read(tenant, "alice", clock.Now, source.Id).Snapshot?.FreshUntil == snapshot.FreshUntil,
            "schema 2 conditional response preserves the exact original source deadline");
        clock.Now = at.AddSeconds(20);
        var expired304 = sync.SyncAsync(tenant, source.Id, "alice").GetAwaiter().GetResult();
        check(expired304.Status == "failed" && expired304.Error == "invalid-response" && seenEtag == ""
            && pip.Read(tenant, "alice", clock.Now, source.Id).Status == "stale",
            "304 cannot revalidate an expired schema 2 snapshot or extend its deadline");
        TransportContract(source, legacy, at, values, check);
    }

    private static void TransportContract(PipSourceDefinition source, PipSourceDefinition legacy, DateTimeOffset at,
        IReadOnlyDictionary<string, string> values, Action<bool, string> check)
    {
        Dictionary<string, object?> Envelope() => new()
        {
            ["schemaVersion"] = 2, ["tenantId"] = source.TenantId.ToString("D"), ["subject"] = "wire",
            ["sourceVersion"] = "wire-1", ["observedAt"] = at.ToString("O"), ["validUntil"] = at.AddSeconds(20).ToString("O"), ["attributes"] = values
        };
        PipFetchedSnapshot Fetch(Dictionary<string, object?> body, PipSourceDefinition selected)
        {
            using var http = new HttpClient(new JsonHandler(JsonSerializer.Serialize(body)));
            return new HttpPipExternalConnector(http, new MutableClock(at),
                new PipTransportOptions { Bindings = [new() { TenantId = selected.TenantId, Endpoint = selected.Endpoint }] })
                .FetchAsync(selected, "wire").GetAwaiter().GetResult();
        }
        check(Fetch(Envelope(), source).ValidUntil == at.AddSeconds(20), "HTTP schema 2 parser retains a strictly typed UTC source deadline");
        foreach (var invalid in new object?[] { null, 42, "2026-10-24T08:00:20", "2026-10-24T11:30:20+03:30", at.ToString("O") })
        {
            var body = Envelope(); body["validUntil"] = invalid;
            Reject<InvalidDataException>(() => Fetch(body, source), check, "HTTP schema 2 rejects invalid deadline JSON: " + (invalid ?? "null"));
        }
        var missing = Envelope(); missing.Remove("validUntil");
        Reject<InvalidDataException>(() => Fetch(missing, source), check, "HTTP schema 2 requires the deadline field");
        var wrongSubject = Envelope(); wrongSubject["subject"] = "other";
        Reject<InvalidDataException>(() => Fetch(wrongSubject, source), check, "HTTP schema 2 retains exact subject correlation");
        var wrongTenant = Envelope(); wrongTenant["tenantId"] = Guid.NewGuid().ToString("D");
        Reject<InvalidDataException>(() => Fetch(wrongTenant, source), check, "HTTP schema 2 retains exact tenant correlation");
        var legacyBody = Envelope(); legacyBody["schemaVersion"] = 1;
        Reject<InvalidDataException>(() => Fetch(legacyBody, legacy), check, "HTTP schema 1 rejects the schema 2-only deadline field");
    }

    private static void Reject<T>(Action action, Action<bool, string> check, string label) where T : Exception
    { try { action(); } catch (T) { check(true, label); return; } throw new Exception("Expected rejection: " + label); }
    private sealed class MutableClock(DateTimeOffset now) : TimeProvider
    { public DateTimeOffset Now { get; set; } = now; public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class NotModifiedConnector(Action<PipSourceDefinition> seen) : IPipExternalConnector
    {
        public Task<PipFetchedSnapshot> FetchAsync(PipSourceDefinition source, string subject, CancellationToken cancellationToken = default)
        { seen(source); return Task.FromException<PipFetchedSnapshot>(new PipNotModifiedException()); }
    }
    private sealed class JsonHandler(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
    }
}
