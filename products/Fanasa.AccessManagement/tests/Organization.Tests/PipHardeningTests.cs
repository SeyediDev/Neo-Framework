using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Governance;
using Fanasa.AccessManagement.Web.Persistence;

static class PipHardeningTests
{
    public static void Run(string directory, Action<bool, string> check)
    {
        var root = Path.Combine(directory, "pip-hardening");
        Directory.CreateDirectory(root);
        using var database = new FabricDatabase(Path.Combine(root, "fabric.db"));
        var access = new PersistentAccessManagement(database, new InMemoryAccessManagement());
        var tenant = access.CreateTenant("pip-hardening", "PIP hardening").Id;
        var store = new PipStore(database, access);
        var source = store.Register(tenant, "HR", "https://hr.example.test/attributes", "hr.example.test", "pip-hr", enabled: true);
        var now = DateTimeOffset.UtcNow;
        var attributes = new Dictionary<string, string> { ["employment.active"] = "true", ["organization.unit"] = "engineering" };
        var first = Fetched(source, "alice", attributes, now, "v1");
        var second = Fetched(source, "alice", attributes, now.AddSeconds(1), "v2");
        store.SaveSnapshot(source, first, now);
        store.SaveSnapshot(source, second, now.AddSeconds(1));
        Reject<InvalidDataException>(() => store.SaveSnapshot(source, Fetched(source, "alice", attributes, now.AddSeconds(2), "v1"), now.AddSeconds(2)), check,
            "PIP version history rejects old versions disguised by newer timestamps");
        Reject<InvalidDataException>(() => store.SaveSnapshot(source, Fetched(source, "alice", attributes, now.AddSeconds(2), "v2"), now.AddSeconds(2)), check,
            "PIP versions are immutable even when observation time changes");
        Reject<InvalidDataException>(() => store.SaveSnapshot(source, "bob", first, now), check,
            "trusted adapter writes must correlate the expected subject");
        store.SetEnabled(tenant, source.Id, false);
        Reject<InvalidOperationException>(() => store.SaveSnapshot(source, second, now.AddSeconds(1)), check,
            "in-flight synchronization cannot publish after source disable");
        store.RecordFailure(source, now.AddSeconds(1), "transport-unavailable");
        check(!store.ReadSource(tenant, source.Id).Enabled, "late failure cannot restore an obsolete enabled source");
        store.SetEnabled(tenant, source.Id, true);
        check(!store.Read(tenant, "alice", now.AddSeconds(1), source.Id).IsFresh, "re-enabled source requires evidence fetched for its new generation");
        source = store.ReadSource(tenant, source.Id);
        var valid = store.SaveSnapshot(source, second, now.AddSeconds(1));

        var key = $"{tenant:D}:{source.Id:D}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("alice"))).ToLowerInvariant()}";
        void Corrupt(PipAttributeSnapshot corrupt, string label)
        {
            database.Transaction(() => { database.Put(PipStore.SnapshotDocumentKind, key, corrupt); return true; });
            check(!store.Read(tenant, "alice", now.AddSeconds(1), source.Id).IsFresh, label);
            database.Transaction(() => { database.Put(PipStore.SnapshotDocumentKind, key, valid); return true; });
        }
        Corrupt(valid with { Attributes = new Dictionary<string, string> { ["employment.active"] = "false" } }, "persisted attribute tampering fails closed");
        Corrupt(valid with { ContentHash = new string('0', 64) }, "persisted hash corruption fails closed");
        Corrupt(valid with { Provenance = "pip://foreign-source/version" }, "persisted provenance corruption fails closed");
        Corrupt(valid with { FreshUntil = valid.FreshUntil.AddDays(1) }, "persisted expiry inflation fails closed");
        Corrupt(valid with { SchemaVersion = 2 }, "persisted schema corruption fails closed");
        Corrupt(valid with { Generation = source.Generation - 1 }, "persisted evidence from another source generation fails closed");
        var validSource = store.ReadSource(tenant, source.Id);
        void CorruptSource(PipSourceDefinition changedSource, PipAttributeSnapshot changedSnapshot, string label)
        {
            database.Transaction(() => { database.Put(PipStore.SourceDocumentKind, source.Id.ToString("D"), changedSource); database.Put(PipStore.SnapshotDocumentKind, key, changedSnapshot); return true; });
            check(!store.Read(tenant, "alice", now.AddSeconds(1), source.Id).IsFresh, label);
            database.Transaction(() => { database.Put(PipStore.SourceDocumentKind, source.Id.ToString("D"), validSource); database.Put(PipStore.SnapshotDocumentKind, key, valid); return true; });
        }
        CorruptSource(validSource with { MaxAgeSeconds = 86401 }, valid with { FreshUntil = valid.ObservedAt.AddSeconds(86401) },
            "corrupted source and matching snapshot cannot extend freshness beyond the permitted limit");
        CorruptSource(validSource with { SchemaVersion = 2 }, valid with { SchemaVersion = 2, ContentHash = PipStore.HashAttributes(tenant, "alice", 2, valid.Attributes, valid.SourceVersion, valid.ObservedAt) },
            "corrupted source and matching snapshot cannot introduce an unsupported schema version");

        var pending = store.Register(tenant, "Pending", "https://pending.example.test/attributes", "pending.example.test", null);
        check(!pending.Enabled, "newly registered PIP sources start disabled");
        Reject<ArgumentException>(() => store.Register(tenant, "query", "https://hr.example.test/attributes?token=secret", "hr.example.test", null), check,
            "source endpoint cannot hide credentials in query parameters");
        Reject<InvalidDataException>(() => store.SaveSnapshot(source, Fetched(source, "alice", new Dictionary<string, string> { ["employment.active"] = "yes" }, now.AddSeconds(3), "invalid-boolean"), now.AddSeconds(3)), check,
            "persisted PIP attributes must match their declared data types");
        Reject<InvalidDataException>(() => store.SaveSnapshot(source, Fetched(source, "alice", new Dictionary<string, string> { ["organization.unit"] = "engineering" }, now.AddSeconds(3), "missing-required"), now.AddSeconds(3)), check,
            "persisted PIP evidence must contain all required attributes");

        var one = new Dictionary<string, string> { ["a"] = "b", ["c"] = "d" };
        var reordered = new Dictionary<string, string> { ["c"] = "d", ["a"] = "b" };
        string Hash(IReadOnlyDictionary<string, string> values) => PipStore.HashAttributes(tenant, "alice", 1, values, "v", now);
        check(Hash(one) == Hash(reordered), "canonical PIP hashes are stable across dictionary insertion orders");
        check(Hash(one) != Hash(new Dictionary<string, string> { ["a"] = "b\nc=d" }), "canonical PIP hashes distinguish delimiter-like data");

        var failure = new PipSyncService(store, new StubConnector(new InvalidDataException("secret-do-not-store")), new FixedClock(now.AddSeconds(2)))
            .SyncAsync(tenant, source.Id, "alice").GetAwaiter().GetResult();
        check(failure.Status == "failed" && failure.Error?.Contains("secret-do-not-store", StringComparison.Ordinal) != true
            && store.ReadSource(tenant, source.Id).LastError?.Contains("secret-do-not-store", StringComparison.Ordinal) != true,
            "synchronization errors persist safe codes rather than raw connector exception text");
        check(store.Read(tenant, "alice", now.AddSeconds(2), source.Id).IsFresh, "transient fetch failures preserve last good evidence until original expiry");
        var observedEtag = "";
        var conditional = new CallbackConnector((seen, subject) =>
        {
            observedEtag = seen.LastEtag ?? "";
            throw new PipNotModifiedException();
        });
        var unchanged = new PipSyncService(store, conditional, new FixedClock(now.AddSeconds(2)))
            .SyncAsync(tenant, source.Id, "alice").GetAwaiter().GetResult();
        check(unchanged.Status == "not-modified" && observedEtag == second.Etag, "conditional fetch uses the exact subject snapshot ETag");
        check(store.Read(tenant, "alice", valid.FreshUntil, source.Id).Status == "stale", "HTTP not-modified cannot extend attribute freshness");
        var foreignCache = new PipSyncService(store, conditional, new FixedClock(now.AddSeconds(2)))
            .SyncAsync(tenant, source.Id, "bob").GetAwaiter().GetResult();
        check(foreignCache.Status == "failed" && observedEtag == "", "another subject cannot inherit the source's most recent ETag");
        var staleCache = new PipSyncService(store, new StubConnector(new PipNotModifiedException()), new FixedClock(valid.FreshUntil))
            .SyncAsync(tenant, source.Id, "alice").GetAwaiter().GetResult();
        check(staleCache.Status == "failed", "not-modified without fresh matching evidence is a failed synchronization");

        TransportChecks(source, now, attributes, check);
    }

    private static void TransportChecks(PipSourceDefinition source, DateTimeOffset now, IReadOnlyDictionary<string, string> attributes, Action<bool, string> check)
    {
        source = source with { LastEtag = null };
        var payload = JsonSerializer.Serialize(new { schemaVersion = 1, tenantId = source.TenantId, subject = "alice", sourceVersion = "http-v1", observedAt = now, attributes });
        using var handler = PipHttpTransport.CreateHandler();
        check(!handler.AllowAutoRedirect && !handler.UseProxy && !handler.UseCookies && handler.ConnectCallback is not null
            && handler.PooledConnectionLifetime == TimeSpan.Zero, "production PIP HTTP transport pins connections and disables redirects, proxies, cookies and pooled reuse");
        string[] denied = ["127.0.0.1", "10.1.2.3", "172.16.1.2", "192.168.1.1", "169.254.169.254", "100.64.0.1", "0.0.0.0", "224.0.0.1", "::1", "::", "fe80::1", "fc00::1", "::ffff:127.0.0.1", "2001:db8::1", "2002:7f00:1::1", "3fff::1"];
        check(denied.All(x => !PipAddressPolicy.IsPublic(IPAddress.Parse(x))), "PIP network boundary blocks private, metadata, loopback, shared, documentation and transition addresses");
        check(PipAddressPolicy.IsPublic(IPAddress.Parse("8.8.8.8")) && PipAddressPolicy.IsPublic(IPAddress.Parse("2606:4700:4700::1111")), "PIP network policy supports public IPv4 and IPv6");
        var privateBinding = new PipEndpointBinding { TenantId = source.TenantId, Endpoint = source.Endpoint, AllowedPrivateAddresses = ["10.1.2.3"] };
        check(PipAddressPolicy.IsApproved(IPAddress.Parse("10.1.2.3"), privateBinding)
            && !PipAddressPolicy.IsApproved(IPAddress.Parse("10.1.2.4"), privateBinding)
            && !PipAddressPolicy.IsApproved(IPAddress.Parse("127.0.0.1"), privateBinding), "private PIP exceptions permit only an exact approved address");
        var requests = 0;
        using var client = new HttpClient(new StubHandler((_, _) => { requests++; return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload, Encoding.UTF8, "application/json") }; }));
        Reject<InvalidOperationException>(() => new HttpPipExternalConnector(client, new FixedClock(now), new PipTransportOptions(), new SecretResolver()).FetchAsync(source, "alice").GetAwaiter().GetResult(), check, "tenant registration cannot authorize arbitrary outbound endpoints");
        Reject<InvalidOperationException>(() => new HttpPipExternalConnector(client, new FixedClock(now), Options(source)).FetchAsync(source, "alice").GetAwaiter().GetResult(), check, "configured credentials cannot silently downgrade to anonymous HTTP");
        Reject<InvalidOperationException>(() => new HttpPipExternalConnector(client, new FixedClock(now), Options(source), new SecretResolver(null)).FetchAsync(source, "alice").GetAwaiter().GetResult(), check, "unresolved credentials fail before network dispatch");
        Reject<InvalidOperationException>(() => Options(source).Authorize(source with { TenantId = Guid.NewGuid() }), check, "endpoint approval cannot be borrowed by another tenant");
        Reject<InvalidOperationException>(() => Options(source).Authorize(source with { SecretReference = "other-secret" }), check, "source registration cannot select another approved secret");
        check(requests == 0, "unauthorized PIP endpoints and unresolved credentials never dispatch HTTP");

        var mapped = source with { Definitions = [new PipAttributeDefinition("employment.active", "boolean", true, "active")] };
        var mappedPayload = JsonSerializer.Serialize(new { schemaVersion = 1, tenantId = source.TenantId, subject = "alice", sourceVersion = "http-mapped", observedAt = now, attributes = new { active = "true" } });
        using var mappedClient = new HttpClient(new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(mappedPayload, Encoding.UTF8, "application/json") }));
        var mappedResult = new HttpPipExternalConnector(mappedClient, new FixedClock(now), Options(mapped), new SecretResolver()).FetchAsync(mapped, "alice").GetAwaiter().GetResult();
        check(mappedResult.Attributes.Count == 1 && mappedResult.Attributes["employment.active"] == "true", "declared external attribute names are mapped to canonical policy names");

        void InvalidBody(string value, string label)
        {
            using var bodyClient = new HttpClient(new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(value, Encoding.UTF8, "application/json") }));
            Reject<InvalidDataException>(() => new HttpPipExternalConnector(bodyClient, new FixedClock(now), Options(source), new SecretResolver()).FetchAsync(source, "alice").GetAwaiter().GetResult(), check, label);
        }
        InvalidBody("{", "malformed JSON becomes a controlled PIP data error");
        InvalidBody(payload.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal), "duplicate PIP envelope fields are rejected");
        InvalidBody(payload.Replace("\"schemaVersion\":1", "\"schemaVersion\":\"1\"", StringComparison.Ordinal), "PIP envelope JSON types must match the contract");
        InvalidBody(payload.Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"unexpected\":true", StringComparison.Ordinal), "undeclared PIP envelope fields are rejected");
        InvalidBody(payload.Replace("\"employment.active\":\"true\"", "\"employment.active\":\"true\",\"employment.active\":\"false\"", StringComparison.Ordinal), "duplicate PIP attribute names are rejected");
        InvalidBody(payload.Replace("\"employment.active\":\"true\"", "\"employment.active\":\"yes\"", StringComparison.Ordinal), "PIP boolean attributes use valid boolean values");
        InvalidBody(payload.Replace("\"employment.active\":\"true\",", "", StringComparison.Ordinal), "missing required PIP attributes are rejected");
        InvalidBody(payload.Replace("\"subject\":\"alice\"", "\"subject\":\"bob\"", StringComparison.Ordinal), "external PIP cannot substitute another subject");
        InvalidBody(payload.Replace("+00:00", "", StringComparison.Ordinal), "external PIP timestamps must explicitly identify UTC");
        using var redirectClient = new HttpClient(new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Redirect)));
        Reject<HttpRequestException>(() => new HttpPipExternalConnector(redirectClient, new FixedClock(now), Options(source), new SecretResolver()).FetchAsync(source, "alice").GetAwaiter().GetResult(), check, "external PIP redirects fail closed");
        using var nonJsonClient = new HttpClient(new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) }));
        Reject<InvalidDataException>(() => new HttpPipExternalConnector(nonJsonClient, new FixedClock(now), Options(source), new SecretResolver()).FetchAsync(source, "alice").GetAwaiter().GetResult(), check, "external PIP requires application/json media type");
        using var notModifiedClient = new HttpClient(new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.NotModified)));
        Reject<InvalidDataException>(() => new HttpPipExternalConnector(notModifiedClient, new FixedClock(now), Options(source), new SecretResolver()).FetchAsync(source, "alice").GetAwaiter().GetResult(), check, "external PIP rejects unsolicited 304 responses");
    }

    private static PipFetchedSnapshot Fetched(PipSourceDefinition source, string subject, IReadOnlyDictionary<string, string> attributes, DateTimeOffset observed, string version) =>
        new(source.TenantId, subject, source.SchemaVersion, attributes, version, "\"" + version + "\"", observed,
            PipStore.HashAttributes(source.TenantId, subject, source.SchemaVersion, attributes, version, observed));
    private static PipTransportOptions Options(PipSourceDefinition source) => new() { Bindings = [new PipEndpointBinding { TenantId = source.TenantId, Endpoint = source.Endpoint, SecretReference = source.SecretReference }] };
    private static void Reject<T>(Action action, Action<bool, string> check, string label) where T : Exception
    {
        try { action(); }
        catch (T) { check(true, label); return; }
        throw new Exception("Expected rejection: " + label);
    }
    private sealed class FixedClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    private sealed class StubConnector(Exception error) : IPipExternalConnector
    {
        public Task<PipFetchedSnapshot> FetchAsync(PipSourceDefinition source, string subject, CancellationToken cancellationToken = default) => Task.FromException<PipFetchedSnapshot>(error);
    }
    private sealed class CallbackConnector(Func<PipSourceDefinition, string, PipFetchedSnapshot> callback) : IPipExternalConnector
    {
        public Task<PipFetchedSnapshot> FetchAsync(PipSourceDefinition source, string subject, CancellationToken cancellationToken = default)
        {
            try { return Task.FromResult(callback(source, subject)); }
            catch (Exception error) { return Task.FromException<PipFetchedSnapshot>(error); }
        }
    }
    private sealed class SecretResolver(string? value = "synthetic-secret") : IPipSecretResolver
    {
        public ValueTask<string?> ResolveAsync(string? reference, CancellationToken cancellationToken = default) => ValueTask.FromResult(value);
    }
    private sealed class StubHandler(Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> callback) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(callback(request, cancellationToken));
    }
}
