using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Fanasa.AccessManagement.Web.Persistence;
using Fanasa.HrReadCandidate.Fabric;
using Microsoft.Data.Sqlite;

namespace Fanasa.HrReadCandidate;

/// <summary>Temporary real SQLite databases and explicit fixture guards; no runtime authority or deployment acceptance.</summary>
internal static class FabricTests
{
    private static readonly Guid Tenant = Guid.Parse("7398590f-2d87-41ce-beda-19fb8e2a45fd");
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 0, 0, 0, TimeSpan.Zero);
    private const string RequestId = "g-0123456789abcdef0123456789abcdef";
    private static readonly HrReadTarget Chart = new(HrReadEndpointKind.Chart, Tenant, null);
    private static readonly string ChartPath = "/api/organization-hr/v1/tenants/" + Tenant + "/chart";

    public static async Task RunAsync()
    {
        AdmissionValidation();
        DefaultGuardDenies();
        NoncePersistsAcrossReopenAndKeyChange();
        LateVetoRetainsClaim();
        ReadPreservesActualWireAndEmptySemantics();
        HistoryMatchesCurrentAndExactLongRevision();
        CorruptOrMismatchedStateDenies();
        SourceUtf8CapAndQueryOnlySnapshot();
        await IndependentClaimersAreSerializedAsync();
        await SecondConnectionRevocationVetoesFreshGateAsync();
        await NotFoundPassesFreshGateAsync();
    }

    private static HrReadFabricAdmission Admission(HrReadTarget? target = null, string keyId = "fixture-key-1",
        string requestId = RequestId, int sourceBytes = 1048576, string tier = "edge")
        => new(target ?? Chart, 1048576, Now.AddMinutes(1), tier, requestId, keyId, Epoch(Now.AddSeconds(30)), sourceBytes);
    private static decimal Epoch(DateTimeOffset value) => (value.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / 10000000m;
    private static HrReadFabricAdapter Adapter(FabricDatabase database, FixtureGuard? guard = null)
        => new(database, guard ?? new FixtureGuard(), new FixtureTime());

    private static void AdmissionValidation()
    {
        Check.Throws<ArgumentException>(() => Admission(requestId: "invented-business-id"), "Fabric admission rejects malformed gateway request id");
        Check.Throws<ArgumentException>(() => Admission(requestId: RequestId.ToUpperInvariant()), "Fabric admission requires canonical lowercase gateway request id");
        Check.Throws<ArgumentException>(() => Admission(keyId: "fixture key"), "Fabric admission key slug rejects whitespace");
        Check.Throws<ArgumentException>(() => Admission(keyId: "fixture:key"), "Fabric admission key slug rejects delimiter characters");
        Check.Throws<ArgumentException>(() => Admission(tier: "public"), "Fabric admission rejects unknown tier");
        Check.Throws<ArgumentOutOfRangeException>(() => Admission(sourceBytes: 0), "Fabric source cap must be explicit positive bound");
        Check.Throws<ArgumentOutOfRangeException>(() => Admission(sourceBytes: HrReadFabricAdmission.AbsoluteMaximumSourceDocumentBytes + 1), "Fabric source cap has local absolute allocation ceiling");
        Check.Throws<ArgumentException>(() => Admission(Chart with { Revision = 1 }), "chart admission cannot smuggle revision");
        Check.Throws<ArgumentException>(() => Admission(new(HrReadEndpointKind.History, Tenant, 0)), "history admission requires positive long revision");
        Check.Throws<ArgumentOutOfRangeException>(() => new HrReadFabricAdmission(Chart, 100, Now.AddMinutes(1), "edge", RequestId, "key", 0, 100), "Fabric witness expiry must be positive");
    }

    private static void DefaultGuardDenies()
    {
        using var temporary = new TemporaryDatabase();
        using var database = temporary.Open();
        var adapter = new HrReadFabricAdapter(database, time: new FixtureTime());
        Check.True(!adapter.TryClaimNonceInTransaction(Admission(), default), "constructed evidence cannot authorize default Fabric guard");
        Check.True(database.ReadDocument(HrReadFabricAdapter.ReplayBucket, "edge:" + RequestId) is null, "default denial writes no replay record");
        Check.Throws<UnauthorizedAccessException>(() => adapter.ReadInSnapshot(Admission(), default), "default Fabric read guard denies");
        Check.True(!adapter.RevalidateInNewSnapshot(Admission(), default), "default fresh gate denies");
        Check.True(database.Outbox().Length == 0, "default denial emits no outbox event");
        var currentGuard = new FixtureGuard();
        var guarded = Adapter(database, currentGuard);
        var expiredWitness = new HrReadFabricAdmission(Chart, 100, Now.AddMinutes(1), "edge", RequestId, "key", Epoch(Now), 100);
        var expiredAdmission = new HrReadFabricAdmission(Chart, 100, Now, "edge", RequestId, "key", Epoch(Now.AddSeconds(1)), 100);
        Check.True(!guarded.TryClaimNonceInTransaction(expiredWitness, default)
            && !guarded.TryClaimNonceInTransaction(expiredAdmission, default) && currentGuard.Calls == 0,
            "exact exclusive witness and admission expiry deny before current guard or nonce claim");
    }

    private static void NoncePersistsAcrossReopenAndKeyChange()
    {
        using var temporary = new TemporaryDatabase();
        using (var database = temporary.Open())
        {
            var adapter = Adapter(database);
            Check.True(adapter.TryClaimNonceInTransaction(Admission(), default), "first real SQLite nonce claim commits");
            Check.True(!database.IsInTransaction, "nonce transaction has ended before claim method returns");
            var receipt = database.ReadDocument(HrReadFabricAdapter.ReplayBucket, "edge:" + RequestId)!;
            using var json = JsonDocument.Parse(receipt.Json);
            Check.True(json.RootElement.GetProperty("Tier").GetString() == "edge"
                && json.RootElement.GetProperty("RequestId").GetString() == RequestId
                && json.RootElement.GetProperty("KeyId").GetString() == "fixture-key-1"
                && json.RootElement.GetProperty("ExpiresAt").GetDecimal() == Admission().ExpiresAt,
                "replay record has exact existing PascalCase receipt schema");
            Check.True(database.Outbox().Length == 0, "read nonce creates no ledger or outbox event");
        }
        using (var reopened = temporary.Open())
        {
            Check.True(!Adapter(reopened).TryClaimNonceInTransaction(Admission(keyId: "rotated-key-2"), default),
                "consumed tier:requestId survives database disposal/reopen and signing key rotation");
            Check.True(Adapter(reopened).TryClaimNonceInTransaction(Admission(tier: "internal"), default),
                "existing replay namespace remains tier scoped");
            Check.True(reopened.DocumentPage(HrReadFabricAdapter.ReplayBucket).Length == 2, "only the two existing bucket keys are stored");
            using var connection = temporary.Connect();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
            using var reader = command.ExecuteReader();
            var tables = new List<string>(); while (reader.Read()) tables.Add(reader.GetString(0));
            Check.True(string.Join(",", tables) == "fabric_documents,fabric_outbox", "adapter creates no parallel store/registry/ledger table");
        }
    }

    private static void LateVetoRetainsClaim()
    {
        using var temporary = new TemporaryDatabase();
        using var database = temporary.Open();
        var guard = new FixtureGuard { Decision = (_, _, call) => call == 1 };
        Check.True(!Adapter(database, guard).TryClaimNonceInTransaction(Admission(), default), "post-Put guard veto returns false");
        Check.True(database.ReadDocument(HrReadFabricAdapter.ReplayBucket, "edge:" + RequestId) is not null,
            "post-Put veto commits consumed nonce");
        Check.True(!Adapter(database).TryClaimNonceInTransaction(Admission(), default), "late veto cannot refund nonce");

        var secondRequest = "g-1123456789abcdef0123456789abcdef";
        var throwing = new FixtureGuard { Decision = (_, _, call) => call == 1 ? true : throw new InvalidOperationException("fixture late veto") };
        Check.True(!Adapter(database, throwing).TryClaimNonceInTransaction(Admission(requestId: secondRequest), default)
            && database.ReadDocument(HrReadFabricAdapter.ReplayBucket, "edge:" + secondRequest) is not null,
            "exception in post-Put guard also retains committed claim");

        var thirdRequest = "g-2123456789abcdef0123456789abcdef";
        using var cancellation = new CancellationTokenSource();
        var cancelling = new FixtureGuard { Decision = (_, _, call) => { if (call == 2) cancellation.Cancel(); return true; } };
        Check.Throws<OperationCanceledException>(() => Adapter(database, cancelling).TryClaimNonceInTransaction(Admission(requestId: thirdRequest), cancellation.Token),
            "cancellation during post-Put guard propagates after commit");
        Check.True(database.ReadDocument(HrReadFabricAdapter.ReplayBucket, "edge:" + thirdRequest) is not null,
            "cancellation during post-Put guard retains committed claim");

        var corruptKey = "g-3123456789abcdef0123456789abcdef";
        temporary.PutRaw(HrReadFabricAdapter.ReplayBucket, "edge:" + corruptKey, "not-json");
        Check.True(!Adapter(database).TryClaimNonceInTransaction(Admission(requestId: corruptKey), default), "even corrupt existing replay evidence cannot be claimed again");
    }

    private static void ReadPreservesActualWireAndEmptySemantics()
    {
        using var temporary = new TemporaryDatabase();
        MaterializedHrRead result;
        using (var database = temporary.Open())
        {
            PutState(database, 4);
            var guard = new FixtureGuard { Decision = (db, _, _) => { Check.True(db.IsInTransaction, "read guard executes synchronously in actual snapshot"); return true; } };
            result = Adapter(database, guard).ReadInSnapshot(Admission(), default);
            Check.True(guard.Calls == 2 && !database.IsInTransaction, "read guard runs before and after materialization; snapshot then closes");
            Check.True(result.Found && result.Value.GetProperty("tenantId").GetGuid() == Tenant && result.Value.GetProperty("revision").GetInt64() == 4,
                "chart result retains tenant/revision with actual API camelCase");
            Check.True(!result.Value.TryGetProperty("TenantId", out _) && result.Value.GetProperty("roles").ValueKind == JsonValueKind.Null,
                "storage PascalCase is not leaked; optional Roles default remains null");
            Check.True(result.Value.GetProperty("units")[0].GetProperty("name").GetString() == "واحد نمونه"
                && result.Value.GetProperty("positions")[0].GetProperty("capacity").GetInt32() == 2,
                "nested typed arrays preserve actual organization data");
        }
        Check.True(result.Value.GetProperty("units").GetArrayLength() == 1, "returned JSON remains detached after Fabric disposal");

        using var emptyDatabase = new TemporaryDatabase();
        using var empty = emptyDatabase.Open();
        var emptyResult = Adapter(empty).ReadInSnapshot(Admission(), default);
        Check.True(emptyResult.Found && emptyResult.Value.GetProperty("revision").GetInt64() == 0
            && emptyResult.Value.GetProperty("units").GetArrayLength() == 0,
            "active fixture tenant without organization returns empty chart revision0 as OrganizationStore.Read");
    }

    private static void HistoryMatchesCurrentAndExactLongRevision()
    {
        using var temporary = new TemporaryDatabase();
        using var database = temporary.Open();
        PutState(database, long.MaxValue);
        PutState(database, long.MaxValue, history: true);
        var target = new HrReadTarget(HrReadEndpointKind.History, Tenant, long.MaxValue);
        var result = Adapter(database).ReadInSnapshot(Admission(target), default);
        Check.True(result.Found && result.Value.GetProperty("revision").GetInt64() == long.MaxValue, "history preserves backend Int64 maximum without narrowing");
        Check.True(!Adapter(database).ReadInSnapshot(Admission(target with { Revision = 1 }), default).Found, "missing exact historical revision returns not-found");

        PutState(database, 2);
        PutState(database, 3, history: true);
        Check.True(!Adapter(database).ReadInSnapshot(Admission(target with { Revision = 3 }), default).Found, "stray history newer than current revision cannot be returned");
    }

    private static void CorruptOrMismatchedStateDenies()
    {
        using var temporary = new TemporaryDatabase();
        using var database = temporary.Open();
        var adapter = Adapter(database);
        temporary.PutRaw("organization", Tenant.ToString(), StateJson(1, Guid.NewGuid()));
        Check.Throws<InvalidDataException>(() => adapter.ReadInSnapshot(Admission(), default), "chart row with another TenantId is rejected");
        PutState(database, 5);
        temporary.PutRaw("organization.history", Tenant + ":2", StateJson(3));
        Check.Throws<InvalidDataException>(() => adapter.ReadInSnapshot(Admission(new(HrReadEndpointKind.History, Tenant, 2)), default), "history document must match requested revision exactly");
        temporary.PutRaw("organization.history", Tenant + ":2", StateJson(2, Guid.NewGuid()));
        Check.Throws<InvalidDataException>(() => adapter.ReadInSnapshot(Admission(new(HrReadEndpointKind.History, Tenant, 2)), default), "history row with another TenantId is rejected");
        temporary.PutRaw("organization", Tenant.ToString(), "{\"TenantId\":\"" + Tenant + "\",\"Revision\":1,\"Revision\":2,\"Units\":[],\"Positions\":[],\"Appointments\":[],\"Changes\":[]}");
        Check.Throws<InvalidDataException>(() => adapter.ReadInSnapshot(Admission(), default), "duplicate stored state fields are not silently accepted");
        temporary.PutRaw("organization", Tenant.ToString(), "{}");
        Check.Throws<JsonException>(() => adapter.ReadInSnapshot(Admission(), default), "incomplete persisted organization does not become empty valid state");
        temporary.PutRaw("organization", Tenant.ToString(), StateJson(1).Replace("\"Name\":\"واحد نمونه\"", "\"Name\":\"first\",\"Name\":\"second\"", StringComparison.Ordinal));
        Check.Throws<InvalidDataException>(() => adapter.ReadInSnapshot(Admission(), default), "nested duplicate stored fields are rejected recursively");
        temporary.PutRaw("organization", Tenant.ToString(), StateJson(1)[..^1] + ",\"Unexpected\":true}");
        Check.Throws<JsonException>(() => adapter.ReadInSnapshot(Admission(), default), "unknown root stored member fails closed instead of disappearing");
        temporary.PutRaw("organization", Tenant.ToString(), StateJson(1).Replace("\"Capacity\":2", "\"Capacity\":2,\"Unexpected\":true", StringComparison.Ordinal));
        Check.Throws<JsonException>(() => adapter.ReadInSnapshot(Admission(), default), "unknown nested stored member fails closed instead of disappearing");
        temporary.PutRaw("organization", Tenant.ToString(), StateJson(1).Replace("\"Name\":\"واحد نمونه\"", "\"Name\":null", StringComparison.Ordinal));
        Check.Throws<JsonException>(() => adapter.ReadInSnapshot(Admission(), default), "required nested name cannot silently deserialize as null");
        temporary.PutRaw("organization", Tenant.ToString(), StateJson(1).Replace("\"Code\":\"sample\",", "", StringComparison.Ordinal));
        Check.Throws<JsonException>(() => adapter.ReadInSnapshot(Admission(), default), "required nested member cannot silently deserialize as a default");
        temporary.PutRaw("organization", Tenant.ToString(), "not-json");
        Check.Throws<JsonException>(() => adapter.ReadInSnapshot(Admission(), default), "malformed persisted JSON does not yield payload");
    }

    private static void SourceUtf8CapAndQueryOnlySnapshot()
    {
        using var temporary = new TemporaryDatabase();
        using var database = temporary.Open();
        var raw = StateJson(1);
        temporary.PutRaw("organization", Tenant.ToString(), raw);
        var byteCount = Encoding.UTF8.GetByteCount(raw);
        Check.True(byteCount > raw.Length, "source cap fixture contains actual multibyte UTF8");
        Check.Throws<InvalidDataException>(() => Adapter(database).ReadInSnapshot(Admission(sourceBytes: byteCount - 1), default),
            "source document cap counts UTF8 bytes before parse/materialization");
        Check.True(Adapter(database).ReadInSnapshot(Admission(sourceBytes: byteCount), default).Found, "source document exactly at UTF8 byte cap can materialize");
        var guard = new FixtureGuard
        {
            Decision = (db, _, _) =>
            {
                Check.Throws<SqliteException>(() => db.Put("fixture.forbidden-write", "one", new { Value = true }),
                    "actual Fabric query_only snapshot prevents guard writes");
                return true;
            }
        };
        Check.True(Adapter(database, guard).ReadInSnapshot(Admission(), default).Found && guard.Calls == 2,
            "both before/after read guards run in query-only snapshots");
        Check.True(database.ReadDocument("fixture.forbidden-write", "one") is null, "attempted snapshot write made no persistent change");

        var afterReadVeto = new FixtureGuard { Decision = (_, _, call) => call == 1 };
        Check.Throws<UnauthorizedAccessException>(() => Adapter(database, afterReadVeto).ReadInSnapshot(Admission(), default),
            "after-read guard veto discards materialized result");
        Check.True(!database.IsInTransaction, "vetoed snapshot is closed before returning exception");
    }

    private static async Task IndependentClaimersAreSerializedAsync()
    {
        using var temporary = new TemporaryDatabase();
        using var first = temporary.Open();
        using var second = temporary.Open();
        var attempts = await Task.WhenAll(Task.Run(() => Adapter(first).TryClaimNonceInTransaction(Admission(), default)),
            Task.Run(() => Adapter(second).TryClaimNonceInTransaction(Admission(), default)));
        Check.True(attempts.Count(value => value) == 1, "two independent real SQLite claimers commit exactly one matching nonce");
        Check.True(first.DocumentPage(HrReadFabricAdapter.ReplayBucket).Length == 1, "concurrent claim test leaves one existing-bucket replay row");
    }

    private static async Task SecondConnectionRevocationVetoesFreshGateAsync()
    {
        using var temporary = new TemporaryDatabase();
        using var database = temporary.Open();
        using var second = temporary.Open();
        PutState(database, 1);
        database.Transaction(() => { database.Put("fixture.authority", "current", new FixtureGrant(true)); return true; });
        var guard = new FixtureGuard { Decision = (db, _, _) => db.Read<FixtureGrant>("fixture.authority", "current")?.Active == true };
        var actual = Adapter(database, guard);
        var wrapper = new AfterReadAdapter(actual, () =>
        {
            Check.True(!database.IsInTransaction, "original read snapshot has ended before external revocation commit");
            second.Transaction(() => { second.Put("fixture.authority", "current", new FixtureGrant(false)); return true; });
        });
        var writer = new FixtureWriter();
        var coordinator = new HrReadCoordinator(wrapper, new FixtureAuthority(Admission()), new FixtureTime());
        Check.True(await coordinator.ExecuteAsync("GET", ChartPath, Stream.Null, writer) == HrReadOutcome.Denied,
            "second SQLite connection revocation after original snapshot vetoes new final snapshot");
        Check.True(guard.Calls == 5 && writer.Calls == 0, "nonce2/read2/final1 guard calls complete before any payload");
        Check.True(database.ReadDocument(HrReadFabricAdapter.ReplayBucket, "edge:" + RequestId) is not null,
            "fresh gate revocation retains independently committed nonce");
    }

    private static async Task NotFoundPassesFreshGateAsync()
    {
        using var temporary = new TemporaryDatabase();
        using var database = temporary.Open();
        PutState(database, 1);
        var target = new HrReadTarget(HrReadEndpointKind.History, Tenant, 1);
        var writer = new FixtureWriter();
        var guard = new FixtureGuard();
        var coordinator = new HrReadCoordinator(Adapter(database, guard), new FixtureAuthority(Admission(target)), new FixtureTime());
        Check.True(await coordinator.ExecuteAsync("GET", ChartPath.Replace("/chart", "/history/1"), Stream.Null, writer) == HrReadOutcome.Sent,
            "missing real history document can return authorized not-found");
        Check.True(guard.Calls == 5 && writer.Calls == 1 && writer.Status == 404 && writer.Bytes.Length == 0 && writer.CacheControl == "no-store",
            "real Fabric 404 executes same fresh gate before empty no-store send");
    }

    private static void PutState(FabricDatabase database, long revision, bool history = false)
        => database.Transaction(() =>
        {
            using var value = JsonDocument.Parse(StateJson(revision));
            database.Put(history ? "organization.history" : "organization", history ? Tenant + ":" + revision : Tenant.ToString(), value.RootElement);
            return true;
        });
    private static string StateJson(long revision, Guid? tenant = null)
    {
        var unitId = Guid.Parse("49d6e03a-a86f-46be-b645-5a5ad539af38");
        return JsonSerializer.Serialize(new
        {
            TenantId = tenant ?? Tenant, Revision = revision,
            Units = new[] { new { Id = unitId, Name = "واحد نمونه", Code = "sample", ParentId = (Guid?)null, Kind = "department", Active = true } },
            Positions = new[] { new { Id = Guid.Parse("f0ebdc07-529f-44f7-8ac9-d707096da7c6"), UnitId = unitId, Name = "نمونه", Code = "position", Capacity = 2, Active = true, RoleId = (Guid?)null } },
            Appointments = Array.Empty<object>(), Changes = Array.Empty<object>()
        }, new JsonSerializerOptions { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    private sealed record FixtureGrant(bool Active);
    private sealed class FixtureTime : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class FixtureGuard : IHrReadCurrentGuard
    {
        public int Calls { get; private set; }
        public Func<FabricDatabase, HrReadFabricAdmission, int, bool> Decision { get; set; } = (_, _, _) => true;
        public bool Allows(FabricDatabase database, HrReadFabricAdmission admission, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Decision(database, admission, ++Calls); }
    }
    private sealed class FixtureAuthority(HrReadAdmission admission) : IHrReadAuthority
    {
        public ValueTask<HrReadAdmission?> AuthenticateAndResolveAsync(HrReadTarget target, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult<HrReadAdmission?>(admission); }
    }
    private sealed class AfterReadAdapter(IHrReadFabricAdapter inner, Action afterRead) : IHrReadFabricAdapter
    {
        public bool TryClaimNonceInTransaction(HrReadAdmission admission, CancellationToken cancellationToken) => inner.TryClaimNonceInTransaction(admission, cancellationToken);
        public MaterializedHrRead ReadInSnapshot(HrReadAdmission admission, CancellationToken cancellationToken)
        { var result = inner.ReadInSnapshot(admission, cancellationToken); afterRead(); return result; }
        public bool RevalidateInNewSnapshot(HrReadAdmission admission, CancellationToken cancellationToken) => inner.RevalidateInNewSnapshot(admission, cancellationToken);
    }
    private sealed class FixtureWriter : IHrReadResponseWriter
    {
        public int Calls { get; private set; }
        public int Status { get; private set; }
        public byte[] Bytes { get; private set; } = [];
        public string? CacheControl { get; private set; }
        public ValueTask SendAsync(int statusCode, ReadOnlyMemory<byte> bytes, string cacheControl, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Calls++; Status = statusCode; Bytes = bytes.ToArray(); CacheControl = cacheControl; return ValueTask.CompletedTask; }
    }
    private sealed class TemporaryDatabase : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "fanasa-hr-fabric-fixture-" + Guid.NewGuid().ToString("N"));
        public string PathName => Path.Combine(directory, "fabric.sqlite");
        public FabricDatabase Open() => new(PathName);
        public SqliteConnection Connect()
        { var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = PathName, Pooling = false }.ToString()); connection.Open(); return connection; }
        public void PutRaw(string kind, string key, string json)
        {
            using var connection = Connect(); using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO fabric_documents(kind,key,json) VALUES($kind,$key,$json) ON CONFLICT(kind,key) DO UPDATE SET json=excluded.json";
            command.Parameters.AddWithValue("$kind", kind); command.Parameters.AddWithValue("$key", key); command.Parameters.AddWithValue("$json", json); command.ExecuteNonQuery();
        }
        public void Dispose()
        {
            var full = Path.GetFullPath(directory);
            var prefix = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "fanasa-hr-fabric-fixture-"));
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Invalid fixture cleanup path.");
            if (Directory.Exists(full)) Directory.Delete(full, true);
        }
    }
}
