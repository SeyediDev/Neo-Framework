using System.Globalization;
using System.Text.Json;
using Fanasa.AccessManagement.Web.Persistence;
using Fanasa.ContractPolicy;
using Fanasa.HrReadCandidate;
using Fanasa.HrReadPdp;
using Microsoft.Data.Sqlite;

namespace Fanasa.HrReadHost.Tests;

/// <summary>Isolated synthetic SQLite only. No production source, grants, quota, nonce or publication.</summary>
public static class PublicationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);
    private const string Consumer = "11111111-1111-4111-8111-111111111111";
    private const string Subject = "22222222-2222-4222-8222-222222222222";
    private const string Actor = "33333333-3333-4333-8333-333333333333";
    private static readonly Guid RoleId = Guid.Parse("44444444-4444-4444-8444-444444444444");
    private static readonly Guid ProductId = Guid.Parse("55555555-5555-4555-8555-555555555555");

    public static int Run()
    {
        var count = 0;
        void Check(bool value, string label)
        { count++; if (!value) throw new InvalidOperationException("HR3 publication test failed: " + label); }
        void Rejected(Action<Fixture> change, string label, bool seed = false)
        {
            using var f = new Fixture();
            if (seed) f.Store.Publish(f.Request);
            change(f); var before = f.Fingerprint(); var rejected = false;
            try { f.Store.Publish(f.Request); }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or
                UnauthorizedAccessException or InvalidDataException or JsonException or OperationCanceledException)
            { rejected = true; }
            Check(rejected, label + " rejects");
            Check(f.Fingerprint() == before && !f.Database.IsInTransaction, label + " leaves all SQLite documents/outbox unchanged");
        }

        using (var f = new Fixture())
        {
            Check(f.Module.Projections.Validate(f.Request.Projection).Count == 0, "explicit synthetic HR3 input is schema valid");
            var before = f.Fingerprint(); var failed = false;
            try { new Hr3PublicationStore(f.Database, f.Module.Projections, time: f.Time).Publish(f.Request); }
            catch (UnauthorizedAccessException) { failed = true; }
            Check(failed && f.Fingerprint() == before, "default unavailable authority denies with zero effects");
            var sameTransaction = false;
            f.Authority.BeforeRead = (_, database, _) => sameTransaction |= ReferenceEquals(database, f.Database) && database.IsInTransaction;
            var result = f.Store.Publish(f.Request);
            var selector = f.Selector;
            Check(sameTransaction && f.Authority.Reads == 2 && !f.Database.IsInTransaction,
                "publication independently checks authority twice in its owned existing Fabric transaction");
            Check(result.PublicationSha256 == Hr3PublicationStore.Fingerprint(f.Request) && result.Policy.Revision == 1 && result.Assignment.Revision == 1,
                "returned proof binds exact complete publication and independently starts policy/assignment revisions");
            Check(f.Database.Read<ProjectionState>(HrReadPdpFabricSource.ProjectionBucket, f.Request.Projection.ContractId)!.Projection == result.Projection.Projection
                || Equal(f.Database.Read<ProjectionState>(HrReadPdpFabricSource.ProjectionBucket, f.Request.Projection.ContractId), result.Projection),
                "complete projection and receipt are committed under existing contract key");
            Check(Equal(f.Database.Read<HrReadPdpPublishedPolicy>(HrReadPdpFabricSource.PolicyBucket, HrReadPdpFabricSource.PolicyKey(selector)), result.Policy)
                && Equal(f.Database.Read<HrReadPdpProductRoleAssignment>(HrReadPdpFabricSource.RoleAssignmentBucket,
                    HrReadPdpFabricSource.RoleAssignmentKey(selector, Subject)), result.Assignment), "full four-dimension scoped policy and subject assignment are committed");
            Check(Equal(f.Database.Read<AllocationLimits>(HrReadPdpFabricSource.AllocationBucket, "hr-allocation"), f.Request.Projection.Limits)
                && Equal(f.Database.Read<AllocationLimits>(HrReadPdpFabricSource.AllocationBucket, "hr-allocation:2"), f.Request.Projection.Limits)
                && Equal(f.Database.Read<ContractPolicyBinding>(HrReadPdpFabricSource.PolicyVersionBucket, "hr-policy:3"), f.Request.Policy.Binding),
                "current allocation and exact immutable allocation/policy revisions share existing buckets");
            Check(f.Database.Outbox().Length == 1 && f.Database.Outbox()[0].Kind == "Hr3PublicationCommitted",
                "one atomic attributable audit event is committed");
            Check(f.LegacyFingerprint() == f.OriginalLegacy && f.Database.DocumentPage("fixture.quota").Length == 0
                && f.Database.DocumentPage("fixture.nonce").Length == 0 && f.Database.DocumentPage("access.grant").Length == 0,
                "legacy command aliases and unrelated records remain unchanged; no direct grants/debits/nonces are created");
            var committed = f.Fingerprint(); var replay = f.Store.Publish(f.Request);
            Check(Equal(result, replay) && f.Fingerprint() == committed, "exact authorized delivery replay is idempotent with no extra receipt/audit writes");

            var firstProjection = result.Projection.Projection;
            var nextLimits = firstProjection.Limits with { ConfigRevision = 7, Quota = firstProjection.Limits.Quota with { Requests = 2000 } };
            var nextPolicy = firstProjection.Policy with { PolicyVersion = 9, ExplicitDenyActions = [HrReadProfile.HistoryAction] };
            f.Request = f.Request with
            {
                Projection = firstProjection with { ContractVersion = 5, Limits = nextLimits, Policy = nextPolicy },
                Policy = f.Request.Policy with { Binding = nextPolicy }, ExpectedContractVersion = 1,
                ExpectedAllocationConfigRevision = 2, ExpectedPolicyRevision = 1, ExpectedAssignmentRevision = 1,
                Reason = "independently authorized revision update"
            };
            f.Approve(); var updated = f.Store.Publish(f.Request);
            Check(updated.Projection.Projection.ContractVersion == 5 && updated.Projection.Projection.Limits.ConfigRevision == 7
                && updated.Policy.Binding.PolicyVersion == 9 && updated.Policy.Revision == 2 && updated.Assignment.Revision == 2,
                "contract, allocation config, policy content and publication revisions advance independently");
            Check(Equal(f.Database.Read<AllocationLimits>(HrReadPdpFabricSource.AllocationBucket, "hr-allocation:2"), firstProjection.Limits)
                && Equal(f.Database.Read<ContractPolicyBinding>(HrReadPdpFabricSource.PolicyVersionBucket, "hr-policy:3"), firstProjection.Policy),
                "advancing independent revisions preserves previous immutable content");
        }

        Rejected(f => f.Authority.Value = null, "missing trusted publication proof");
        Rejected(f => f.Authority.Value = f.Authority.Value! with { PublicationSha256 = new string('0', 64) }, "unapproved exact source payload");
        Rejected(f => f.Authority.Value = f.Authority.Value! with { ActorSubjectId = Subject }, "unapproved administrative actor");
        Rejected(f => f.Authority.Value = f.Authority.Value! with { SourceRevision = "" }, "missing independently read source revision");
        Rejected(f => f.Authority.Value = f.Authority.Value! with { ValidUntil = Now }, "exclusive authority expiry");
        Rejected(f => f.Authority.Value = f.Authority.Value! with { Registration = f.Authority.Value!.Registration with { Id = Guid.NewGuid() } }, "registered identity mismatch");
        Rejected(f => f.Authority.Value = f.Authority.Value! with { Registration = f.Authority.Value!.Registration with { TenantId = Guid.Parse(Consumer) } }, "platform product cannot gain synthetic owner tenant");
        Rejected(f => f.Authority.Value = f.Authority.Value! with { Tenant = new(Consumer, false) }, "inactive existing tenant");
        Rejected(f => f.Authority.Value = f.Authority.Value! with { Membership = f.Authority.Value!.Membership with { Active = false } }, "revoked membership");
        Rejected(f => f.Authority.Value = f.Authority.Value! with { Role = f.Authority.Value!.Role with { Permissions = ["organization.write"] } }, "command permission cannot authorize HR read publication");
        Rejected(f => f.Authority.Value = f.Authority.Value! with { Grant = f.Authority.Value!.Grant with { Active = false } }, "existing grant is required");
        Rejected(f => f.Authority.Value = f.Authority.Value! with { Grant = f.Authority.Value!.Grant with { Actions = [HrReadProfile.ChartAction] } }, "grant must cover complete published HR features");
        Rejected(f => { f.Request = f.Request with { Assignment = f.Request.Assignment with { ValidTo = Now.AddDays(2) } }; f.Approve(); }, "assignment cannot outlive existing membership/grant/contract");
        Rejected(f => f.Authority.BeforeRead = (read, _, _) => { if (read == 2) f.Authority.Value = null; }, "post-write authority revocation rolls back all records/audit");
        Rejected(f => f.Authority.BeforeRead = (read, _, _) => { if (read == 2) f.Authority.Value = f.Authority.Value! with { SourceRevision = "changed" }; }, "post-write source revision change rolls back all records/audit");
        Rejected(f => f.Authority.BeforeRead = (read, _, _) => { if (read == 2) f.Authority.Value = f.Authority.Value! with { ValidUntil = Now.AddSeconds(6) }; }, "authority lease cannot extend during publication");
        Rejected(f => f.Authority.BeforeRead = (read, _, _) => { if (read == 2) throw new InvalidOperationException("source unavailable"); }, "post-write authority exception rolls back");

        Rejected(f => { f.Request = f.Request with { ExpectedContractVersion = 4 }; f.Approve(); }, "projection expected CAS");
        Rejected(f => { f.Request = f.Request with { ExpectedAllocationConfigRevision = 4 }; f.Approve(); }, "allocation expected CAS");
        Rejected(f => { f.Request = f.Request with { ExpectedPolicyRevision = 4 }; f.Approve(); }, "policy publication expected CAS");
        Rejected(f => { f.Request = f.Request with { ExpectedAssignmentRevision = 4 }; f.Approve(); }, "assignment expected CAS");
        Rejected(f => f.Put(HrReadPdpFabricSource.PolicyVersionBucket, "hr-policy:3", f.Request.Policy.Binding with { ExplicitDenyActions = [HrReadProfile.ChartAction] }), "immutable policy collision");
        Rejected(f => f.Put(HrReadPdpFabricSource.AllocationBucket, "hr-allocation:2", f.Request.Projection.Limits with { Quota = f.Request.Projection.Limits.Quota with { Requests = 999 } }), "immutable allocation revision collision");
        Rejected(f => f.Put(HrReadPdpFabricSource.ProjectionBucket, f.Request.Projection.ContractId,
            new ProjectionState(f.Request.Projection with { SchemaVersion = "2.0.0" }, [])), "legacy same-contract projection requires explicit migration");
        Rejected(f => { f.Request = f.Request with { Projection = f.Request.Projection with { OwnerTenantId = Consumer } }; f.Approve(); }, "invalid HR ownership preflight");
        Rejected(f => { f.Request = f.Request with { Policy = f.Request.Policy with { Binding = f.Request.Policy.Binding with { PolicyVersion = 8 } } }; f.Approve(); }, "projection policy must match explicit publication policy");
        Rejected(f => { f.Request = f.Request with { Assignment = f.Request.Assignment with { Active = false } }; f.Approve(); }, "revocation cannot manufacture a missing assignment");
        Rejected(f =>
        {
            var key = HrReadPdpFabricSource.PolicyKey(f.Selector);
            var json = f.Database.ReadDocument(HrReadPdpFabricSource.PolicyBucket, key)!.Json;
            f.WriteRaw(HrReadPdpFabricSource.PolicyBucket, key, json[..^1] + ",\"Unexpected\":true}");
        }, "unknown persisted policy property", seed: true);
        Rejected(f =>
        {
            var key = HrReadPdpFabricSource.PolicyKey(f.Selector);
            var json = f.Database.ReadDocument(HrReadPdpFabricSource.PolicyBucket, key)!.Json;
            f.WriteRaw(HrReadPdpFabricSource.PolicyBucket, key,
                json.Replace("\"Active\":true", "\"Active\":true,\"\\u0041ctive\":true", StringComparison.Ordinal));
        }, "escaped duplicate persisted policy property", seed: true);

        foreach (var mutate in new Func<Hr3PublicationRequest, Hr3PublicationRequest>[]
        {
            r => r with { Projection = r.Projection with { ContractVersion = 1, Status = "Suspended" } },
            r => r with { Projection = r.Projection with { ContractVersion = 2, Limits = r.Projection.Limits with { ConfigRevision = 1 } } },
            r => r with { Projection = r.Projection with { ContractVersion = 2, Limits = r.Projection.Limits with { Quota = r.Projection.Limits.Quota with { Requests = 999 } } } },
            r => r with { Projection = r.Projection with { ContractVersion = 2, SubscriptionId = "different-subscription" } }
        }) Rejected(f =>
        {
            f.Request = mutate(f.Request) with { ExpectedContractVersion = 1, ExpectedAllocationConfigRevision = 2,
                ExpectedPolicyRevision = 1, ExpectedAssignmentRevision = 1, Reason = "attempted update" }; f.Approve();
        }, "stale/conflicting revision or aggregate rebinding", seed: true);

        using (var f = new Fixture())
        {
            f.Store.Publish(f.Request);
            f.Request = f.Request with { ExpectedContractVersion = 1, ExpectedAllocationConfigRevision = 2,
                ExpectedPolicyRevision = 1, ExpectedAssignmentRevision = 1,
                Policy = f.Request.Policy with { Active = false }, Assignment = f.Request.Assignment with { Active = false },
                Reason = "explicit retirement and revocation" };
            f.Approve(); f.Authority.Value = f.Authority.Value! with { Membership = f.Authority.Value!.Membership with { Active = false }, Grant = f.Authority.Value!.Grant with { Active = false } };
            var retired = f.Store.Publish(f.Request);
            Check(!retired.Policy.Active && !retired.Assignment.Active && retired.Policy.Revision == 2 && retired.Assignment.Revision == 2,
                "explicit authorized retirement/revocation remains possible after membership and grant offboarding");
            Check(f.LegacyFingerprint() == f.OriginalLegacy, "HR read retirement never alters command policy aliases");
        }
        using (var f = new Fixture())
        {
            f.Authority.BeforeRead = (read, _, _) => { if (read == 2) f.Authority.Value = f.Authority.Value! with { ValidUntil = Now.AddSeconds(4) }; };
            Check(f.Store.Publish(f.Request).Policy.Active && f.Database.Outbox().Length == 1,
                "unchanged authoritative content may shorten its still-current lease");
        }
        using (var f = new Fixture())
        {
            var left = f.Request with { Reason = "concurrent approved writer one" };
            var right = f.Request with { Reason = "concurrent approved writer two" };
            var leftAuthority = new Authority { Value = f.Authority.Value! with { PublicationSha256 = Hr3PublicationStore.Fingerprint(left) } };
            var rightAuthority = new Authority { Value = f.Authority.Value! with { PublicationSha256 = Hr3PublicationStore.Fingerprint(right) } };
            bool Commit(Hr3PublicationRequest r, Authority a)
            {
                try { new Hr3PublicationStore(f.Database, f.Module.Projections, a, f.Time).Publish(r); return true; }
                catch (InvalidOperationException) { return false; }
            }
            var completed = Task.WhenAll(Task.Run(() => Commit(left, leftAuthority)), Task.Run(() => Commit(right, rightAuthority))).GetAwaiter().GetResult();
            Check(completed.Count(x => x) == 1 && f.Database.Outbox().Length == 1,
                "real concurrent SQLite writers with the same CAS have exactly one complete publication/audit winner");
            Check(f.Database.Read<HrReadPdpPublishedPolicy>(HrReadPdpFabricSource.PolicyBucket, HrReadPdpFabricSource.PolicyKey(f.Selector))!.Revision == 1
                && f.Database.Read<HrReadPdpProductRoleAssignment>(HrReadPdpFabricSource.RoleAssignmentBucket,
                    HrReadPdpFabricSource.RoleAssignmentKey(f.Selector, Subject))!.Revision == 1,
                "losing concurrent CAS cannot partially advance policy or assignment revisions");
        }
        using (var f = new Fixture())
        {
            var before = f.Fingerprint(); var denied = false;
            try { f.Database.Transaction(() => f.Store.Publish(f.Request)); }
            catch (InvalidOperationException) { denied = true; }
            Check(denied && f.Fingerprint() == before, "ambient transaction rejected before publication effects");
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); denied = false;
            try { f.Store.Publish(f.Request, cancellation.Token); }
            catch (OperationCanceledException) { denied = true; }
            Check(denied && f.Fingerprint() == before, "pre-cancelled publication has zero effects");
            using var duringWrite = new CancellationTokenSource();
            f.Authority.BeforeRead = (read, _, _) => { if (read == 2) duringWrite.Cancel(); };
            denied = false;
            try { f.Store.Publish(f.Request, duringWrite.Token); }
            catch (OperationCanceledException) { denied = true; }
            Check(denied && f.Fingerprint() == before, "cancellation at final authority check rolls back every write and audit event");
        }
        return count;
    }

    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    private sealed class Authority : IHr3PublicationAuthority
    {
        public Hr3PublicationAuthoritySnapshot? Value { get; set; }
        public int Reads { get; private set; }
        public Action<int, FabricDatabase, Hr3PublicationRequest>? BeforeRead { get; set; }
        public Hr3PublicationAuthoritySnapshot? ReadCurrent(FabricDatabase database, Hr3PublicationRequest request, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Reads++; BeforeRead?.Invoke(Reads, database, request); return Value; }
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "fanasa-hr3-publication-" + Guid.NewGuid().ToString("N"));
        public Fixture()
        {
            DatabasePath = Path.Combine(directory, "fabric.db"); Database = new(DatabasePath);
            var scope = new AllocationScope(Consumer, [HrReadProfile.ProductId], ["development"], [HrReadProfile.OperationGroup]);
            var policy = new ContractPolicyBinding("hr-policy", 3, HrReadProfile.TemplateId, "deny-overrides", []);
            var projection = new ContractAccessProjection
            {
                SchemaVersion = HrReadProfile.SchemaVersion, ProfileId = HrReadProfile.ProfileId, ContractId = "hr-contract", ContractVersion = 1,
                OwnerTenantId = null, Ownership = new("Platform", "platform", ProductId.ToString("D")), ConsumerTenantId = Consumer,
                ProductId = HrReadProfile.ProductId, OfferId = "hr-offer", SubscriptionId = "hr-subscription", Status = "Active",
                ValidFrom = Format(Now.AddHours(-1)), ValidTo = Format(Now.AddHours(1)), EffectiveAt = Format(Now.AddHours(-1)),
                FeatureEntitlements = [HrReadProfile.ChartAction, HrReadProfile.HistoryAction], EligibleRoles = [HrReadProfile.Role],
                Seats = new(25, "active-membership", "consumer-product-subscription"),
                Limits = new() { AllocationId = "hr-allocation", ConfigRevision = 2, Scope = scope,
                    CanonicalScope = ContractProjectionValidator.CanonicalScope(scope), Meter = "api.request.admitted", Unit = "request",
                    Rate = new("token-bucket", 5, 10), Quota = new("fixed-calendar-utc", "month", 1000) },
                ZoneConstraints = ["internal"], PricingReference = new("hr-price", 1), Policy = policy
            };
            Request = new(projection, new(policy, true), new(Subject, RoleId, true, Now.AddMinutes(-1), Now.AddMinutes(5)), 0, 0, 0, 0, Actor, "explicit isolated fixture approval");
            Store = new(Database, Module.Projections, Authority, Time);
            Put(HrReadPdpFabricSource.PolicyBucket, Consumer, new { Legacy = "policy-command" });
            Put(HrReadPdpFabricSource.RoleAssignmentBucket, Consumer + ":" + Subject, new { Legacy = "role-command" });
            Put(HrReadPdpFabricSource.ProjectionBucket, "legacy-command-contract", new { Legacy = "projection-command" });
            Put("fixture.ledger", "preserved", new { Admitted = 123 });
            OriginalLegacy = LegacyFingerprint(); Approve();
        }
        public FabricDatabase Database { get; }
        public string DatabasePath { get; }
        public ContractPolicyModule Module { get; } = ContractPolicyModule.FromOutputDirectory();
        public TimeProvider Time { get; } = new Clock();
        public Authority Authority { get; } = new();
        public Hr3PublicationStore Store { get; }
        public Hr3PublicationRequest Request { get; set; }
        public ProjectionSelector Selector => new(Consumer, HrReadProfile.ProfileId, HrReadProfile.ProductId, Request.Projection.ContractId);
        public string OriginalLegacy { get; }
        public void Approve() => Authority.Value = new(Actor, Hr3PublicationStore.Fingerprint(Request), "source-contract-policy-allocation-revision-1",
            new(ProductId, HrReadProfile.ProductId, HrReadProfile.Audience, "platform", null), new(Consumer, true),
            new(Subject, Consumer, true, Format(Now.AddHours(-1)), Format(Now.AddHours(1))),
            new(RoleId, HrReadProfile.Role, [HrReadDesign.Permission]),
            new("hr-grant", Subject, Consumer, HrReadProfile.ProductId, HrReadProfile.Role, [HrReadProfile.ChartAction, HrReadProfile.HistoryAction], true,
                Format(Now.AddHours(-1)), Format(Now.AddHours(1))), Now.AddSeconds(5));
        public void Put<T>(string kind, string key, T value) => Database.Transaction(() => { Database.Put(kind, key, value); return 0; });
        public void WriteRaw(string kind, string key, string json)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "UPDATE fabric_documents SET json=$json WHERE kind=$kind AND key=$key";
            command.Parameters.AddWithValue("$json", json); command.Parameters.AddWithValue("$kind", kind); command.Parameters.AddWithValue("$key", key);
            command.ExecuteNonQuery();
        }
        public string LegacyFingerprint() => string.Join('\n', new[]
        {
            Database.ReadDocument(HrReadPdpFabricSource.PolicyBucket, Consumer)!.Json,
            Database.ReadDocument(HrReadPdpFabricSource.RoleAssignmentBucket, Consumer + ":" + Subject)!.Json,
            Database.ReadDocument(HrReadPdpFabricSource.ProjectionBucket, "legacy-command-contract")!.Json,
            Database.ReadDocument("fixture.ledger", "preserved")!.Json
        });
        public string Fingerprint()
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "SELECT kind,key,json FROM fabric_documents ORDER BY kind,key";
            using var reader = command.ExecuteReader(); var rows = new List<string>();
            while (reader.Read()) rows.Add(JsonSerializer.Serialize(new[] { reader.GetString(0), reader.GetString(1), reader.GetString(2) }));
            return string.Join('\n', rows) + "\n" + JsonSerializer.Serialize(Database.Outbox());
        }
        public void Dispose()
        {
            Database.Dispose(); var absolute = Path.GetFullPath(directory);
            if (absolute.StartsWith(Path.GetFullPath(Path.GetTempPath()) + "fanasa-hr3-publication-", StringComparison.OrdinalIgnoreCase) && Directory.Exists(absolute))
                Directory.Delete(absolute, recursive: true);
        }
    }
    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    private static bool Equal<T>(T a, T b) => JsonSerializer.Serialize(a, ContractJson.Options) == JsonSerializer.Serialize(b, ContractJson.Options);
}
