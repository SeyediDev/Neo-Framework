using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Governance;
using Fanasa.AccessManagement.Web.Persistence;

/// <summary>
/// Focused contract checks for the external PIP boundary. These are intentionally
/// separate from the policy evaluator checks: the PIP is responsible for source
/// correlation, bounded transport, freshness and provenance before PDP consumes it.
/// </summary>
static class PipExternalSourceTests
{
    public static void Run(string directory, Action<bool, string> check)
    {
        var root = Path.Combine(directory, "pip-external");
        Directory.CreateDirectory(root);
        using var database = new FabricDatabase(Path.Combine(root, "fabric.db"));
        var access = new PersistentAccessManagement(database, new InMemoryAccessManagement());
        var tenant = access.CreateTenant("pip-main", "PIP main").Id;
        var other = access.CreateTenant("pip-other", "PIP other").Id;
        var store = new PipStore(database, access);
        var now = DateTimeOffset.UtcNow.AddSeconds(-1);
        var source = store.Register(tenant, "HR directory", "https://hr.example.test/attributes", "hr.example.test", "vault://pip/hr", enabled: true);

        void Reject<T>(Action action, string label) where T : Exception
        {
            try { action(); }
            catch (T) { check(true, label); return; }
            throw new Exception("Expected rejection: " + label);
        }

        Reject<ArgumentException>(() => store.Register(tenant, "insecure", "http://hr.example.test/attributes", "hr.example.test", null), "PIP source requires HTTPS");
        Reject<ArgumentException>(() => store.Register(tenant, "host mismatch", "https://other.example.test/attributes", "hr.example.test", null), "PIP endpoint host is allowlisted");
        Reject<ArgumentException>(() => store.Register(tenant, "embedded credentials", "https://user:pass@hr.example.test/attributes", "hr.example.test", null), "PIP endpoint cannot embed credentials");
        Reject<ArgumentException>(() => store.Register(tenant, "fragment", "https://hr.example.test/attributes#private", "hr.example.test", null), "PIP endpoint cannot contain a fragment");
        Reject<ArgumentException>(() => store.Register(tenant, "bad limits", "https://hr.example.test/attributes", "hr.example.test", null, maxAgeSeconds: 0), "PIP freshness limit is bounded");

        var attributes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["employment.active"] = "true",
            ["organization.unit"] = "engineering"
        };
        var fetched = Fetched(source, "alice", attributes, now, "hr-17");
        var snapshot = store.SaveSnapshot(source, fetched, now);
        check(snapshot.TenantId == tenant && snapshot.SourceId == source.Id && snapshot.Subject == "alice", "PIP snapshot retains source and tenant provenance");
        check(snapshot.Provenance == $"pip://{source.Id:D}/hr-17" && snapshot.ContentHash == fetched.ContentHash, "PIP snapshot exposes versioned provenance and content hash");
        check(store.Read(tenant, "alice", now).Status == "fresh", "fresh PIP snapshot is available to the PDP");
        check(store.Read(other, "alice", now).Status == "missing", "PIP reads never cross tenant boundaries");
        Reject<KeyNotFoundException>(() => store.ReadSource(other, source.Id), "PIP source cannot be read through another tenant");
        check(store.ListSources(other).Length == 0, "PIP source catalog is tenant-scoped");

        var expiredAt = snapshot.FreshUntil.AddTicks(1);
        check(store.Read(tenant, "alice", expiredAt).Status == "stale" && !store.Read(tenant, "alice", expiredAt).IsFresh,
            "expired PIP evidence fails closed as stale");
        Reject<InvalidDataException>(() => store.SaveSnapshot(source, Fetched(source, "alice", attributes, now.AddMinutes(-10), "old"), now),
            "already stale external evidence is rejected before persistence");
        Reject<InvalidDataException>(() => store.SaveSnapshot(source, Fetched(source, "alice", attributes, now, "hash-mismatch", contentHash: "00"), now),
            "tampered external evidence hash is rejected");
        Reject<InvalidDataException>(() => store.SaveSnapshot(source, Fetched(source, "alice", new Dictionary<string, string> { ["secret.token"] = "leak" }, now, "secret"), now),
            "sensitive raw attributes are rejected");
        Reject<InvalidDataException>(() => store.SaveSnapshot(source, Fetched(source, "alice", attributes, now, "wrong-schema", schemaVersion: 2), now),
            "external schema version must match the source contract");
        Reject<InvalidDataException>(() => store.SaveSnapshot(source, Fetched(source, "alice", attributes, DateTimeOffset.UtcNow.AddMinutes(2), "future"), now),
            "future-dated external evidence is rejected");
        Reject<InvalidDataException>(() => store.SaveSnapshot(source, new PipFetchedSnapshot(other, "alice", 1, attributes, "foreign", null, now, Hash(source, other, "alice", attributes, now, "foreign", 1)), now),
            "foreign tenant PIP response is rejected");

        var unchangedOutbox = database.Outbox().Length;
        var same = store.SaveSnapshot(source, fetched, now);
        check(same.ContentHash == snapshot.ContentHash && database.Outbox().Length == unchangedOutbox,
            "same source version is an idempotent snapshot retry");
        var newerAttributes = new Dictionary<string, string>(attributes, StringComparer.Ordinal)
        {
            ["organization.unit"] = "platform"
        };
        var newer = Fetched(source, "alice", newerAttributes, now.AddSeconds(1), "hr-18");
        var updated = store.SaveSnapshot(source, newer, now.AddSeconds(1));
        check(updated.SourceVersion == "hr-18" && store.Read(tenant, "alice", now.AddSeconds(1)).Snapshot?.ContentHash == newer.ContentHash,
            "newer external source version replaces the current snapshot");
        store.SetEnabled(tenant, source.Id, false);
        check(store.Read(tenant, "alice", now).Status == "missing", "disabled PIP source is not consulted");
        store.SetEnabled(tenant, source.Id, true); source = store.ReadSource(tenant, source.Id); store.SaveSnapshot(source, newer, now.AddSeconds(1));

        var connectorPayload = JsonSerializer.Serialize(new
        {
            schemaVersion = 1,
            tenantId = tenant,
            subject = "alice",
            sourceVersion = "hr-http-1",
            observedAt = now,
            attributes
        });
        var handler = new StubHandler((request, _) =>
        {
            check(request.RequestUri?.Query.Contains($"tenantId={tenant:D}", StringComparison.OrdinalIgnoreCase) == true
                && request.RequestUri?.Query.Contains("subject=alice", StringComparison.OrdinalIgnoreCase) == true,
                "HTTP PIP connector binds tenant and subject to the request");
            check(request.Headers.Accept.Any(x => x.MediaType == "application/json"), "HTTP PIP connector requests JSON");
            check(request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization.Parameter == "synthetic-secret",
                "HTTP PIP connector resolves the secret reference without persisting the secret");
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(connectorPayload, Encoding.UTF8, "application/json") };
            response.Headers.ETag = new EntityTagHeaderValue("\"hr-http-1\"");
            return response;
        });
        using var client = new HttpClient(handler, disposeHandler: false);
        var connector = new HttpPipExternalConnector(client, new FixedClock(now), Options(source), new StubSecretResolver());
        var fetchedHttp = connector.FetchAsync(source, "alice").GetAwaiter().GetResult();
        check(fetchedHttp.TenantId == tenant && fetchedHttp.Subject == "alice" && fetchedHttp.Etag == "\"hr-http-1\"",
            "HTTP PIP connector parses and retains response correlation metadata");

        var tooLarge = source with { MaxResponseBytes = 1024 };
        using var largeClient = new HttpClient(new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new string('x', 2048), Encoding.UTF8, "application/json")
        }), disposeHandler: true);
        Reject<InvalidDataException>(() => new HttpPipExternalConnector(largeClient, new FixedClock(now), Options(source), new StubSecretResolver()).FetchAsync(tooLarge, "alice").GetAwaiter().GetResult(),
            "HTTP PIP connector enforces response-size bound");

        var notModifiedClient = new HttpClient(new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.NotModified)), disposeHandler: true);
        Reject<PipNotModifiedException>(() => new HttpPipExternalConnector(notModifiedClient, new FixedClock(now), Options(source), new StubSecretResolver()).FetchAsync(source with { LastEtag = "\"hr-18\"" }, "alice").GetAwaiter().GetResult(),
            "HTTP PIP connector exposes ETag not-modified outcome");

        var sync = new PipSyncService(store, new StubConnector(newer), new FixedClock(now.AddSeconds(1)));
        var run = sync.SyncAsync(tenant, source.Id, "alice").GetAwaiter().GetResult();
        check(run.Status == "succeeded" && run.SnapshotHash == newer.ContentHash, "PIP sync records a successful run and snapshot hash");
        var failed = new PipSyncService(store, new StubConnector(new InvalidDataException("bad envelope")), new FixedClock(now))
            .SyncAsync(tenant, source.Id, "alice").GetAwaiter().GetResult();
        check(failed.Status == "failed" && store.ReadSource(tenant, source.Id).Status == "error", "PIP sync records bounded source failure state");
        var notModified = new PipSyncService(store, new StubConnector(new PipNotModifiedException()), new FixedClock(now.AddSeconds(1)))
            .SyncAsync(tenant, source.Id, "alice").GetAwaiter().GetResult();
        check(notModified.Status == "not-modified", "PIP sync preserves a not-modified result without replacing evidence");
        PipHardeningTests.Run(directory, check);
    }

    private static PipTransportOptions Options(PipSourceDefinition source) => new() { Bindings = [new PipEndpointBinding { TenantId = source.TenantId, Endpoint = source.Endpoint, SecretReference = source.SecretReference }] };

    private static PipFetchedSnapshot Fetched(PipSourceDefinition source, string subject, IReadOnlyDictionary<string, string> attributes,
        DateTimeOffset observedAt, string version, string? contentHash = null, int schemaVersion = 1) =>
        new(source.TenantId, subject, schemaVersion, attributes, version, "\"" + version + "\"", observedAt,
            contentHash ?? Hash(source, source.TenantId, subject, attributes, observedAt, version, schemaVersion));

    private static string Hash(PipSourceDefinition source, Guid tenant, string subject, IReadOnlyDictionary<string, string> attributes,
        DateTimeOffset observedAt, string version, int schemaVersion)
    {
        return PipStore.HashAttributes(tenant, subject, schemaVersion, attributes, version, observedAt);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class StubConnector : IPipExternalConnector
    {
        private readonly PipFetchedSnapshot? snapshot;
        private readonly Exception? failure;
        public StubConnector(PipFetchedSnapshot snapshot) => this.snapshot = snapshot;
        public StubConnector(Exception failure) => this.failure = failure;
        public Task<PipFetchedSnapshot> FetchAsync(PipSourceDefinition source, string subject, CancellationToken cancellationToken = default) =>
            failure is not null ? Task.FromException<PipFetchedSnapshot>(failure) : Task.FromResult(snapshot!);
    }

    private sealed class StubSecretResolver : IPipSecretResolver
    {
        public ValueTask<string?> ResolveAsync(string? reference, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(reference == "vault://pip/hr" ? "synthetic-secret" : null);
    }

    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request, cancellationToken));
    }
}
