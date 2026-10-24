using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Fanasa.AccessManagement.Web.Persistence;
using Fanasa.ContractPolicy;
using Fanasa.HrReadCandidate;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;

namespace Fanasa.HrReadPdp.Tests;

/// <summary>Only synthetic isolated SQLite documents; no live registry, grants or publication.</summary>
public static class FabricSourceTests
{
    public static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    public static ContractPolicyModule Module { get; } = ContractPolicyModule.FromOutputDirectory();

    public static ContractAccessProjection ValidProjection(HrReadReceiverSettings binding) => new()
    {
        SchemaVersion = HrReadProfile.SchemaVersion, ProfileId = binding.ProfileId,
        ContractId = binding.ContractId, ContractVersion = binding.ContractVersion,
        OwnerTenantId = binding.OwnerTenantId,
        Ownership = new(binding.Owner == "platform" ? "Platform" : "Tenant", binding.Owner, binding.RegisteredProductId),
        ConsumerTenantId = binding.ConsumerTenantId, ProductId = binding.ProductKey,
        OfferId = "fixture-offer", SubscriptionId = "fixture-subscription", Status = "Active",
        ValidFrom = Format(Now.AddHours(-1)), ValidTo = Format(Now.AddHours(1)), EffectiveAt = Format(Now.AddHours(-1)),
        FeatureEntitlements = [HrReadProfile.ChartAction, HrReadProfile.HistoryAction], EligibleRoles = [HrReadProfile.Role],
        Seats = new(25, "active-membership", "consumer-product-subscription"),
        Limits = new()
        {
            AllocationId = binding.AllocationId, ConfigRevision = binding.ConfigRevision,
            Scope = new(binding.ConsumerTenantId, [binding.ProductKey], [binding.Environment], [HrReadProfile.OperationGroup]),
            CanonicalScope = binding.CanonicalScope, Meter = "api.request.admitted", Unit = "request",
            Rate = new("token-bucket", 5, 10), Quota = new("fixed-calendar-utc", "month", 1000)
        },
        ZoneConstraints = [binding.Zone], PricingReference = new("fixture-price", 1),
        Policy = new(binding.PolicyId, binding.PolicyVersion, HrReadProfile.TemplateId, "deny-overrides", [])
    };

    public static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool value, string label)
        { count++; if (!value) throw new InvalidOperationException("PDP Fabric source test failed: " + label); }
        async Task Reject(Action<Fixture> mutate, string label)
        {
            using var fixture = await CreateFixtureAsync(); mutate(fixture);
            Check(await fixture.CreateSource().ReadAsync(fixture.Query) is null, label);
        }

        using (var fixture = await CreateFixtureAsync())
        {
            Check(Module.Projections.Validate(fixture.Projection).Count == 0, "synthetic HR3 projection is schema valid");
            Check(fixture.Query.AuthenticatedSubject.Scopes.SequenceEqual(new[] { "openid", HrReadDesign.ReadScope }),
                "trusted query derives the ordered schema-representable scopes from the retained exact raw proof");
            var before = fixture.DataFingerprint(); var source = fixture.CreateSource();
            var sameDatabase = false; var queryOnlyPut = false; var queryOnlyOutbox = false;
            fixture.Records.BeforeRead = (_, database, query, caller) =>
            {
                sameDatabase = ReferenceEquals(database, fixture.Database) && database.IsInTransaction
                    && ReferenceEquals(caller, fixture.Caller)
                    && JsonSerializer.Serialize(query, ContractJson.Options) == JsonSerializer.Serialize(fixture.Query, ContractJson.Options);
                try { database.Put("fixture-ledger", "unexpected", new { Debits = 1 }); }
                catch (SqliteException error) when (error.SqliteErrorCode == 8) { queryOnlyPut = true; }
                try { database.Emit("unexpected", "FixtureDebit", new { Debits = 1 }, Now); }
                catch (SqliteException error) when (error.SqliteErrorCode == 8) { queryOnlyOutbox = true; }
            };
            var data = await source.ReadAsync(fixture.Query);
            Check(data is not null && sameDatabase && queryOnlyPut && queryOnlyOutbox && fixture.Records.Reads == 2,
                "each correlated read uses the same existing query-only Fabric snapshot and rejects document/outbox writes");
            fixture.Records.BeforeRead = null;
            Check(data is not null && data.ObservedAt == Now && data.Evidence.ValidUntil == Format(Now.AddSeconds(5)),
                "fresh evidence is capped by the exclusive five-second request deadline");
            var provider = new TrustedAccessSnapshotProvider(source, Module.Projections, fixture.Time, TimeSpan.FromSeconds(5));
            var ready = await provider.ReadAsync(fixture.Query);
            Check(ready.Succeeded, "real SQLite snapshot passes the trusted provider");
            var decision = Module.Evaluator.Evaluate(ready.Projection, ready.Context);
            Check(decision.Decision == "Permit" && decision.Obligations.Count == 2
                && decision.Obligations.Select(x => x.Id).Order(StringComparer.Ordinal).SequenceEqual(new[]
                    { ContractPolicyEvaluator.AdmissionObligation, ContractPolicyEvaluator.ResourceObligation }.Order(StringComparer.Ordinal)),
                "PDP Permit carries both allocation and resource obligations without fulfillment");
            Check(fixture.DataFingerprint() == before && !fixture.Database.IsInTransaction,
                "source/provider/evaluator leave all SQLite documents, ledger, replay and outbox unchanged");
            Check(await new HrReadPdpFabricSource(fixture.Database, Module.Projections, fixture.Request,
                fixture.Query, fixture.Caller, time: fixture.Time).ReadAsync(fixture.Query) is null,
                "default current-host adapter is unavailable and never manufactures records");
            var readCount = fixture.Records.Reads;
            var ambient = fixture.Database.Transaction(() => source.RevalidateCurrent(fixture.Query));
            Check(ambient is null && fixture.Records.Reads == readCount, "ambient writer transaction is rejected before host reads");
            var changedQuery = fixture.Query with { Action = HrReadProfile.HistoryAction };
            Check(await source.ReadAsync(changedQuery) is null && fixture.Records.Reads == readCount,
                "source rejects a changed route/query rather than widening its retained request");
            var changedSelector = fixture.Query with { Selector = fixture.Query.Selector! with { ContractId = "other-contract" } };
            Check(await source.ReadAsync(changedSelector) is null, "complete selector is bound to the expected request");
            var changedSubject = fixture.Query with { AuthenticatedSubject = fixture.Query.AuthenticatedSubject with { SubjectId = "77777777-7777-4777-8777-777777777777" } };
            Check(await source.ReadAsync(changedSubject) is null, "authenticated subject cannot be replaced");
            var reparsed = new HrReadPdpRequest(fixture.Binding, fixture.Request.Target, fixture.Request.RawTarget,
                fixture.Request.RequestId, fixture.Request.RequestedAt, fixture.Request.TokenSha256, fixture.Request.TokenExpiresAt);
            Check(await new HrReadPdpFabricSource(fixture.Database, Module.Projections, reparsed, fixture.Query,
                fixture.Caller, fixture.Records, fixture.Time).ReadAsync(fixture.Query) is null,
                "direct source cannot reuse authentication proof for a different parsed request instance");
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            var cancelled = false;
            try { await source.ReadAsync(fixture.Query, cancellation.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "caller cancellation propagates");
        }

        await Reject(f => f.Delete(HrReadPdpFabricSource.ProjectionBucket, f.Projection.ContractId), "missing projection denies");
        await Reject(f => f.Delete(HrReadPdpFabricSource.AllocationBucket, f.Projection.Limits.AllocationId), "missing allocation denies");
        await Reject(f =>
        {
            f.Delete(HrReadPdpFabricSource.PolicyBucket, HrReadPdpFabricSource.PolicyKey(f.Query.Selector!));
            f.Put(HrReadPdpFabricSource.PolicyBucket, f.Binding.ConsumerTenantId, f.Published);
        }, "legacy consumer-only policy is never a fallback for a missing scoped publication");
        await Reject(f =>
        {
            f.Delete(HrReadPdpFabricSource.RoleAssignmentBucket, HrReadPdpFabricSource.RoleAssignmentKey(f.Query.Selector!, f.Request.SubjectId));
            f.Put(HrReadPdpFabricSource.RoleAssignmentBucket, f.Binding.ConsumerTenantId + ":" + f.Request.SubjectId, f.Assignment);
        }, "legacy consumer-subject assignment is never a fallback for a missing profile/product/contract assignment");
        await Reject(f => f.Delete(HrReadPdpFabricSource.PolicyVersionBucket,
            HrReadPdpFabricSource.PolicyVersionKey(f.Projection.Policy.PolicyId, f.Projection.Policy.PolicyVersion)), "missing immutable policy version denies");

        foreach (var (changed, label) in new (Func<AllocationLimits, AllocationLimits>, string)[]
        {
            (a => a with { ConfigRevision = 2 }, "independent allocation config revision divergence"),
            (a => a with { Rate = a.Rate with { BurstCapacity = 11 } }, "allocation rate content divergence"),
            (a => a with { Quota = a.Quota with { Requests = 1001 } }, "allocation quota content divergence"),
            (a => a with { Scope = a.Scope with { Environments = ["production"] } }, "allocation scope divergence")
        }) await Reject(f => f.Put(HrReadPdpFabricSource.AllocationBucket, f.Projection.Limits.AllocationId, changed(f.Projection.Limits)), label);

        foreach (var (changed, label) in new (Func<HrReadPdpPublishedPolicy, HrReadPdpPublishedPolicy>, string)[]
        {
            (p => p with { Active = false }, "inactive scoped publication"),
            (p => p with { Revision = 0 }, "invalid scoped publication revision"),
            (p => p with { ConsumerTenantId = Guid.Parse("77777777-7777-4777-8777-777777777777") }, "scoped publication consumer mismatch"),
            (p => p with { Binding = p.Binding with { PolicyVersion = 2 } }, "scoped policy version divergence"),
            (p => p with { Binding = p.Binding with { ExplicitDenyActions = [HrReadProfile.ChartAction] } }, "scoped policy content divergence"),
            (p => p with { RecordedAt = Now.AddSeconds(1) }, "future-dated scoped publication"),
            (p => p with { Actor = "" }, "unattributed scoped publication")
        }) await Reject(f => f.Put(HrReadPdpFabricSource.PolicyBucket, HrReadPdpFabricSource.PolicyKey(f.Query.Selector!), changed(f.Published)), label);
        await Reject(f => f.Put(HrReadPdpFabricSource.PolicyVersionBucket,
            HrReadPdpFabricSource.PolicyVersionKey(f.Projection.Policy.PolicyId, f.Projection.Policy.PolicyVersion),
            f.Projection.Policy with { ExplicitDenyActions = [HrReadProfile.ChartAction] }), "immutable policy content divergence");

        foreach (var (changed, label) in new (Func<HrReadPdpProductRoleAssignment, HrReadPdpProductRoleAssignment>, string)[]
        {
            (a => a with { Active = false }, "revoked profile-scoped role assignment"),
            (a => a with { RoleId = Guid.NewGuid() }, "role assignment ID differs from current host role"),
            (a => a with { Subject = "77777777-7777-4777-8777-777777777777" }, "role assignment subject mismatch"),
            (a => a with { TenantId = Guid.NewGuid() }, "role assignment tenant mismatch"),
            (a => a with { Revision = 0 }, "role assignment requires positive revision"),
            (a => a with { ValidTo = Now }, "role assignment expiry is exclusive")
        }) await Reject(f => f.Put(HrReadPdpFabricSource.RoleAssignmentBucket,
            HrReadPdpFabricSource.RoleAssignmentKey(f.Query.Selector!, f.Request.SubjectId), changed(f.Assignment)), label);

        foreach (var (changed, label) in new (Func<HrReadPdpHostRecordSnapshot, HrReadPdpHostRecordSnapshot>, string)[]
        {
            (h => h with { CallerDelegationAllowed = false }, "gateway delegation revoked"),
            (h => h with { SubjectAuthenticated = false }, "subject proof unavailable"),
            (h => h with { UniqueConsumerMapping = false }, "ambiguous authenticated tenant mapping"),
            (h => h with { CurrentClientAuthorized = false }, "current product client revoked"),
            (h => h with { NativePermissionAllowed = false }, "native permission denied"),
            (h => h with { LegacyVetoClear = false }, "legacy veto still applies"),
            (h => h with { CallerSubjectId = "77777777-7777-4777-8777-777777777777" }, "delegation caller subject mismatch"),
            (h => h with { CallerClientId = "other-client" }, "delegation caller client mismatch"),
            (h => h with { Tenant = h.Tenant with { Active = false } }, "inactive consumer tenant"),
            (h => h with { Membership = h.Membership with { Active = false } }, "revoked current membership"),
            (h => h with { Grant = h.Grant with { Active = false } }, "revoked current direct grant"),
            (h => h with { Grant = h.Grant with { Actions = [HrReadProfile.HistoryAction] } }, "grant excludes current action"),
            (h => h with { Role = h.Role with { Key = "ApiInvoker" } }, "command role cannot authorize HR read"),
            (h => h with { Role = h.Role with { Permissions = ["organization.write"] } }, "role lacks native read permission"),
            (h => h with { Registration = h.Registration with { Id = Guid.NewGuid() } }, "fresh central registration ID mismatch"),
            (h => h with { Registration = h.Registration with { Audience = "other-audience" } }, "fresh central registration audience mismatch"),
            (h => h with { Registration = h.Registration with { TenantId = Guid.Parse(h.Tenant.TenantId) } }, "platform ownership never manufactures an owning tenant"),
            (h => h with { ValidUntil = Now }, "host authority lease expiry is exclusive")
        }) await Reject(f => f.Records.Value = changed(f.Records.Value!), label);
        await Reject(f => f.Records.Value = null, "unavailable current host records deny");
        await Reject(f => f.Records.BeforeRead = (read, _, _, _) =>
        { if (read == 2) f.Records.Value = f.Records.Value! with { Role = f.Records.Value!.Role with { Permissions = [HrReadDesign.Permission, "other.permission"] } }; },
            "authority content change during correlation denies even if both reads independently allow");

        using (var fixture = await CreateFixtureAsync())
        {
            var source = fixture.CreateSource(); Check(await source.ReadAsync(fixture.Query) is not null, "initial fresh read succeeds");
            fixture.Put(HrReadPdpFabricSource.RoleAssignmentBucket,
                HrReadPdpFabricSource.RoleAssignmentKey(fixture.Query.Selector!, fixture.Request.SubjectId), fixture.Assignment with { Active = false });
            Check(await source.ReadAsync(fixture.Query) is null, "second call opens a new SQLite snapshot and observes role revocation");
            fixture.Seed(); fixture.Records.Value = fixture.Records.Value! with { Grant = fixture.Records.Value!.Grant with { Active = false } };
            Check(await source.ReadAsync(fixture.Query) is null, "new current-host grant revocation is observed without cached Permit");
        }
        using (var fixture = await CreateFixtureAsync())
        {
            fixture.Records.BeforeRead = (read, _, _, _) =>
            { if (read == 2) fixture.Records.Value = fixture.Records.Value! with { ValidUntil = Now.AddSeconds(2) }; };
            var data = await fixture.CreateSource().ReadAsync(fixture.Query);
            Check(data?.Evidence.ValidUntil == Format(Now.AddSeconds(2)), "second host read may only shorten its authority lease");
        }
        using (var fixture = await CreateFixtureAsync())
        {
            fixture.Records.Value = fixture.Records.Value! with
            { Membership = fixture.Records.Value!.Membership with { ValidTo = Format(Now.AddSeconds(1)) } };
            Check((await fixture.CreateSource().ReadAsync(fixture.Query))?.Evidence.ValidUntil == Format(Now.AddSeconds(1)),
                "current membership expiry shortens decision evidence");
        }

        await Reject(f => f.WriteRaw(HrReadPdpFabricSource.PolicyBucket, HrReadPdpFabricSource.PolicyKey(f.Query.Selector!), "{not-json"), "malformed persisted JSON");
        foreach (var (change, label) in new (Func<string, string>, string)[]
        {
            (j => j[..^1] + ",\"Unknown\":true}", "unknown persisted field"),
            (j => j.Replace("\"Active\":true", "\"Active\":true,\"Active\":true", StringComparison.Ordinal), "duplicate persisted field"),
            (j => j.Replace("\"Active\":true", "\"Active\":true,\"\\u0041ctive\":true", StringComparison.Ordinal), "escaped duplicate persisted field"),
            (j => j.Replace("\"Active\":", "\"active\":", StringComparison.Ordinal), "wrong-case persisted field"),
            (j => j.Replace("\"Binding\":" + JsonSerializer.Serialize(new ContractPolicyBinding("fixture-policy", 1, HrReadProfile.TemplateId, "deny-overrides", [])), "\"Binding\":null", StringComparison.Ordinal), "null required persisted binding")
        }) await Reject(f => f.WriteRaw(HrReadPdpFabricSource.PolicyBucket,
            HrReadPdpFabricSource.PolicyKey(f.Query.Selector!), change(JsonSerializer.Serialize(f.Published))), label);
        await Reject(f => f.WriteRaw(HrReadPdpFabricSource.AllocationBucket, f.Projection.Limits.AllocationId,
            new string(' ', HrReadPdpFabricSource.MaximumSourceDocumentBytes) + "{}"), "persisted document technical parsing bound");
        await Reject(f => f.Put(HrReadPdpFabricSource.ProjectionBucket, f.Projection.ContractId,
            new ProjectionState(f.Projection, [new("fixture-event", "wrong")])), "invalid projection receipt fingerprint");
        await Reject(f => f.Put(HrReadPdpFabricSource.ProjectionBucket, "shadow-contract",
            new ProjectionState(f.Projection, [])), "duplicate projection under an alias key cannot create an ambiguous accepted selector");
        await Reject(f => f.Put(HrReadPdpFabricSource.ProjectionBucket, f.Projection.ContractId,
            new ProjectionState(f.Projection with { OwnerTenantId = f.Binding.ConsumerTenantId }, [])), "projection platform ownership cannot carry an owner tenant");
        await Reject(f => f.SetTime(Now.AddSeconds(5)), "request deadline expiry is exclusive");
        await Reject(f => f.Http.Request.Headers[HrReadPdpAuthentication.SubjectHeader] = "Bearer changed.subject.token", "subject proof must remain current at source read");
        return count;
    }

    public static async Task<Fixture> CreateFixtureAsync(bool seed = true)
    {
        var authentication = new HttpTests.Fixture();
        try
        {
            // Preserve all raw verified scopes in the proof. The old policy schema
            // represents the ordered dotted-identifier subset only; HR remains required.
            var identity = (ClaimsIdentity)authentication.SubjectPrincipal.Identity!;
            identity.RemoveClaim(identity.FindFirst("scope")!);
            identity.AddClaim(new("scope", "openid offline_access " + HrReadDesign.ReadScope + " api:read"));
            var caller = await authentication.Authenticate()
                ?? throw new InvalidOperationException("Synthetic Fabric fixture authentication failed.");
            var fixture = new Fixture(authentication, caller);
            if (seed) fixture.Seed();
            return fixture;
        }
        catch { authentication.Dispose(); throw; }
    }

    public sealed class MutableRecords(HrReadPdpHostRecordSnapshot value) : IHrReadPdpHostRecords
    {
        public HrReadPdpHostRecordSnapshot? Value { get; set; } = value;
        public Action<int, FabricDatabase, TrustedAccessSnapshotQuery, AuthenticatedHrReadPdpCaller>? BeforeRead { get; set; }
        public int Reads { get; private set; }
        public HrReadPdpHostRecordSnapshot? ReadCurrent(FabricDatabase database, TrustedAccessSnapshotQuery query,
            AuthenticatedHrReadPdpCaller caller, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Reads++;
            BeforeRead?.Invoke(Reads, database, query, caller);
            return Value;
        }
    }

    public sealed class Fixture : IDisposable
    {
        private readonly HttpTests.Fixture authentication;
        private readonly string directory;
        internal Fixture(HttpTests.Fixture authentication, AuthenticatedHrReadPdpCaller caller)
        {
            this.authentication = authentication; Caller = caller;
            Query = HrReadPdpService.Query(Request, Caller);
            Projection = ValidProjection(Binding);
            var roleId = Guid.Parse("66666666-6666-4666-8666-666666666666");
            Published = new(Guid.Parse(Binding.ConsumerTenantId), Projection.Policy, true, 1, "fixture", "isolated test", Now);
            Assignment = new(Guid.Parse(Binding.ConsumerTenantId), Request.SubjectId, roleId, true,
                Now.AddHours(-1), Now.AddHours(1), 1, "fixture", "isolated test");
            Records = new(new(CallerSettings.SubjectId, CallerSettings.ClientId, Query.AuthenticatedSubject, Query.Resource,
                new(Guid.Parse(Binding.RegisteredProductId), Binding.ProductKey, Binding.Audience, Binding.Owner, null),
                new(Binding.ConsumerTenantId, true),
                new(Binding.SubjectId, Binding.ConsumerTenantId, true, Format(Now.AddHours(-1)), Format(Now.AddHours(1))),
                new(roleId, HrReadProfile.Role, [HrReadDesign.Permission]),
                new("fixture-grant", Binding.SubjectId, Binding.ConsumerTenantId, Binding.ProductKey, HrReadProfile.Role,
                    [HrReadProfile.ChartAction, HrReadProfile.HistoryAction], true, Format(Now.AddHours(-1)), Format(Now.AddHours(1))),
                Now.AddSeconds(5), true, true, true, true, true, true));
            directory = Path.Combine(Path.GetTempPath(), "fanasa-hr-pdp-fabric-" + Guid.NewGuid().ToString("N"));
            DatabasePath = Path.Combine(directory, "fabric.db"); Database = new(DatabasePath);
        }
        public FabricDatabase Database { get; }
        public string DatabasePath { get; }
        public ContractPolicyModule Module => FabricSourceTests.Module;
        public ContractAccessProjection Projection { get; set; }
        public HrReadPdpPublishedPolicy Published { get; set; }
        public HrReadPdpProductRoleAssignment Assignment { get; set; }
        public MutableRecords Records { get; }
        public HrReadReceiverSettings Binding => authentication.Binding;
        public HrReadPdpCallerSettings CallerSettings => authentication.CallerSettings;
        public HrReadPdpRequest Request => authentication.Request;
        public TrustedAccessSnapshotQuery Query { get; }
        public AuthenticatedHrReadPdpCaller Caller { get; }
        public DefaultHttpContext Http => authentication.Http;
        internal HttpTests.CertificateValidator Certificates => authentication.Certificates;
        public TimeProvider Time => authentication.Time;
        public void SetTime(DateTimeOffset value) => authentication.Time.Current = value;
        public HrReadPdpFabricSource CreateSource(IHrReadPdpHostRecords? records = null) =>
            new(Database, Module.Projections, Request, Query, Caller, records ?? Records, Time);
        public void Seed() => Database.Transaction(() =>
        {
            Database.Put(HrReadPdpFabricSource.ProjectionBucket, Projection.ContractId, new ProjectionState(Projection, []));
            Database.Put(HrReadPdpFabricSource.AllocationBucket, Projection.Limits.AllocationId, Projection.Limits);
            Database.Put(HrReadPdpFabricSource.PolicyBucket, HrReadPdpFabricSource.PolicyKey(Query.Selector!), Published);
            Database.Put(HrReadPdpFabricSource.PolicyVersionBucket,
                HrReadPdpFabricSource.PolicyVersionKey(Projection.Policy.PolicyId, Projection.Policy.PolicyVersion), Projection.Policy);
            Database.Put(HrReadPdpFabricSource.RoleAssignmentBucket,
                HrReadPdpFabricSource.RoleAssignmentKey(Query.Selector!, Request.SubjectId), Assignment);
            Database.Put("fixture-ledger", "unchanged", new { Debits = 0 });
            Database.Put("fixture-replay", "unchanged", new { Used = false });
            return 0;
        });
        public void Put<T>(string bucket, string key, T value) => Database.Transaction(() => { Database.Put(bucket, key, value); return 0; });
        public void WriteRaw(string bucket, string key, string json)
        {
            using var connection = Connection(); using var command = connection.CreateCommand();
            command.CommandText = "INSERT INTO fabric_documents(kind,key,json) VALUES($kind,$key,$json) ON CONFLICT(kind,key) DO UPDATE SET json=excluded.json";
            command.Parameters.AddWithValue("$kind", bucket); command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$json", json); command.ExecuteNonQuery();
        }
        public void Delete(string bucket, string key)
        {
            using var connection = Connection(); using var command = connection.CreateCommand();
            command.CommandText = "DELETE FROM fabric_documents WHERE kind=$kind AND key=$key";
            command.Parameters.AddWithValue("$kind", bucket); command.Parameters.AddWithValue("$key", key); command.ExecuteNonQuery();
        }
        public string DataFingerprint()
        {
            using var connection = Connection(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT kind,key,json FROM fabric_documents ORDER BY kind,key";
            using var reader = command.ExecuteReader(); var rows = new List<string>();
            while (reader.Read()) rows.Add(JsonSerializer.Serialize(new[] { reader.GetString(0), reader.GetString(1), reader.GetString(2) }));
            return string.Join('\n', rows) + "\n" + JsonSerializer.Serialize(Database.Outbox());
        }
        private SqliteConnection Connection()
        {
            var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
            connection.Open(); return connection;
        }
        public void Dispose()
        {
            Database.Dispose(); authentication.Dispose();
            var absolute = Path.GetFullPath(directory);
            var expected = Path.GetFullPath(Path.GetTempPath()) + "fanasa-hr-pdp-fabric-";
            if (absolute.StartsWith(expected, StringComparison.OrdinalIgnoreCase) && Directory.Exists(absolute)) Directory.Delete(absolute, recursive: true);
        }
    }

    public static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
