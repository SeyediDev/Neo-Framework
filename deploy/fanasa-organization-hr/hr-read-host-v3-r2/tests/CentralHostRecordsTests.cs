using System.Text.Json;
using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Domain.Access;
using Fanasa.AccessManagement.Web.Governance;
using Fanasa.AccessManagement.Web.Platform;
using Fanasa.ContractPolicy;
using Fanasa.HrReadCandidate;
using Fanasa.HrReadPdp;
using Fanasa.HrReadPdp.Tests;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace Fanasa.HrReadHost.Tests;

/// <summary>
/// Real central-service implementations over explicit synthetic isolated SQLite data.
/// Authentication uses the linked PDP transport fixture; independent current-authentication
/// proofs are supplied by a mutable typed test source. No live identity/grant is provisioned.
/// </summary>
public static class CentralHostRecordsTests
{
    public static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool condition, string label)
        { count++; if (!condition) throw new InvalidOperationException("HR3 central host test failed: " + label); }
        async Task Reject(Action<Fixture> mutate, string label)
        {
            using var fixture = await CreateFixtureAsync(); mutate(fixture);
            var before = fixture.Fingerprint();
            Check(await fixture.ReadAsync() is null, label);
            Check(fixture.Fingerprint() == before, label + " performs no Fabric/registry/outbox writes");
        }

        using (var fixture = await CreateFixtureAsync())
        {
            var before = fixture.Fingerprint();
            var data = await fixture.ReadAsync();
            Check(data is not null, "existing registry/access/policy records produce a real HR3 source snapshot");
            Check(data is not null && data.Membership.SubjectId == fixture.Transport.Request.SubjectId
                && data.Membership.TenantId == fixture.Transport.Binding.ConsumerTenantId,
                "actual native membership is correlated to the independently authenticated consumer");
            Check(data is not null && data.Grant.GrantId is { Length: 64 } && data.Grant.GrantId.StartsWith("native-", StringComparison.Ordinal)
                && data.Grant.Role == HrReadDesign.Role
                && data.Grant.Actions.SequenceEqual(new[] { HrReadProfile.ChartAction, HrReadProfile.HistoryAction }),
                "native direct grant has a stable schema-safe natural-key view and fixed read actions");
            Check(fixture.ProofSource.Reads == 4, "source's before/after host samples each revalidate independent proof twice");
            Check(fixture.Fingerprint() == before, "successful reads leave Fabric documents, registry, ledger and outbox unchanged");
            var ready = await new TrustedAccessSnapshotProvider(fixture.Transport.CreateSource(fixture.Adapter),
                fixture.Transport.Module.Projections, fixture.Transport.Time, TimeSpan.FromSeconds(5)).ReadAsync(fixture.Transport.Query);
            Check(ready.Succeeded && fixture.Transport.Module.Evaluator.Evaluate(ready.Projection, ready.Context).Decision == "Permit",
                "real central records flow through the existing trusted provider and real HR3 evaluator");
            Check(fixture.Fingerprint() == before, "provider/evaluator do not debit allocation or fulfill obligations");

            var noProof = new Hr3CentralHostRecords(fixture.Transport.Database, fixture.Registry, fixture.Access,
                fixture.Legacy, time: fixture.Transport.Time);
            Check(await fixture.Transport.CreateSource(noProof).ReadAsync(fixture.Transport.Query) is null,
                "default unavailable authentication source denies without a body or JWT tenant fallback");
            Check(fixture.Adapter.ReadCurrent(fixture.Transport.Database, fixture.Transport.Query,
                fixture.Transport.Caller, CancellationToken.None) is null,
                "adapter cannot read outside the source's existing Fabric snapshot");
            Check(fixture.Fingerprint() == before, "denial paths leave both stores and outbox unchanged");
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            var cancelled = false;
            try { await fixture.ReadAsync(cancellation.Token); } catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "caller cancellation propagates without an authority fallback");
        }

        await Reject(f => f.PutClient(f.SubjectClient with { IsActive = false }), "revoked current subject client denies");
        await Reject(f => f.PutClient(f.GatewayClient with { IsActive = false }), "revoked current Gateway client denies");
        await Reject(f => f.PutClient(f.SubjectClient with { AllowedScopes = ["openid"] }), "subject client losing HR scope denies");
        await Reject(f => f.PutClient(f.SubjectClient with { AllowedScopes = [HrReadDesign.ReadScope] }), "subject client losing another requested representable scope denies");
        await Reject(f => f.PutClient(f.GatewayClient with { AllowedScopes = ["different.scope"] }), "Gateway client losing its configured evaluation scope denies");
        await Reject(f => f.PutClient(f.SubjectClient with { ProductId = Guid.NewGuid() }), "subject client cannot move to another product");
        await Reject(f => f.PutClient(f.GatewayClient with { ProductId = Guid.NewGuid() }), "Gateway client cannot move to another product");
        await Reject(f => f.Transport.Put("tenant", f.Tenant.Id.ToString("D"), f.Tenant with { IsActive = false }), "inactive actual tenant denies");
        await Reject(f => f.Access.SetMembership(f.Tenant.Id, f.Member.Id, false, "fixture", "revoke synthetic membership"), "native membership revocation is observed");
        await Reject(f => f.Transport.Delete("membership", f.Member.Id.ToString("D")), "missing actual membership denies");
        await Reject(f => f.Transport.Put("membership", f.Member.Id.ToString("D"), f.Member with { JoinedAt = FabricSourceTests.Now.AddSeconds(1) }), "future native membership is not fabricated as current");
        await Reject(f => f.Transport.Delete("tenant-grant", f.NativeGrantKey), "missing native direct grant denies despite valid HR3 projection");
        await Reject(f => f.Access.SetGrant(f.NativeGrant with { ExpiresAt = FabricSourceTests.Now }, "fixture", "expire synthetic native grant"), "native grant expiry is exclusive");
        await Reject(f => f.Transport.Put("access-role", f.Role.Id.ToString("D"), f.Role with { Permissions = ["organization.write"] }), "existing role losing native read permission denies");
        await Reject(f => f.Transport.Put("access-role", f.Role.Id.ToString("D"), f.Role with { Key = "ApiInvoker" }), "a command role cannot replace HrReader");
        await Reject(f => f.Transport.Delete("access-role", f.Role.Id.ToString("D")), "missing native role denies");
        await Reject(f => f.Transport.Put(HrReadPdpFabricSource.RoleAssignmentBucket,
            HrReadPdpFabricSource.RoleAssignmentKey(f.Transport.Query.Selector!, f.Transport.Request.SubjectId),
            f.Transport.Assignment with { Active = false }), "profile-scoped role assignment revocation denies");
        await Reject(f => f.Registry.Register(f.Product with { Audience = "different-audience" }, "fixture"), "central registered audience changes deny");
        await Reject(f => f.PublishLegacy("Deny"), "current native legacy deny vetoes the otherwise valid read");
        await Reject(f => f.PublishLegacy("Permit"), "native audit obligation cannot be silently fulfilled by a read-only adapter");
        await Reject(f => f.ProofSource.Value = null, "unavailable current introspection/delegation proof denies");
        await Reject(f => f.ProofSource.Value = f.NewProof(consumerTenantId: Guid.NewGuid()), "authenticated consumer proof cannot be replaced by body tenant");
        await Reject(f => f.ProofSource.Value = f.NewProof(subjectId: "77777777-7777-4777-8777-777777777777"), "introspection subject must match retained native authentication");
        await Reject(f => f.ProofSource.Value = f.NewProof(subjectClientId: "other-client"), "introspection client must match retained subject authentication");
        await Reject(f => f.ProofSource.Value = f.NewProof(tokenHash: new string('a', 64)), "subject token bytes must match independent proof");
        await Reject(f => f.ProofSource.Value = f.NewProof(requestId: "g-" + new string('a', 32)), "independent proof cannot be replayed against another request ID");
        await Reject(f => f.ProofSource.Value = f.NewProof(validUntil: FabricSourceTests.Now), "proof expiry is exclusive");
        await Reject(f => f.ProofSource.Value = f.NewProof(validUntil: FabricSourceTests.Now.AddSeconds(6)), "proof cannot exceed the five-second observation ceiling");
        await Reject(f => f.ProofSource.Value = f.NewProof(authorityRevision: ""), "proof must retain an authoritative revocation/delegation revision");
        await Reject(f => f.ProofSource.BeforeRead = (_, _) => throw new IOException("isolated authority read failure"), "authority source failure denies");
        await Reject(f => f.ProofSource.BeforeRead = (read, source) =>
        { if (read == 2) source.Value = f.NewProof(authorityRevision: "changed-revision"); },
            "revocation/delegation revision change during central sampling denies");

        using (var fixture = await CreateFixtureAsync())
        {
            var source = fixture.Transport.CreateSource(fixture.Adapter);
            Check(await source.ReadAsync(fixture.Transport.Query) is not null, "initial actual central snapshot succeeds");
            fixture.PutClient(fixture.GatewayClient with { IsActive = false });
            var before = fixture.Fingerprint();
            Check(await source.ReadAsync(fixture.Transport.Query) is null, "next source snapshot observes client revocation without cached Permit");
            Check(fixture.Fingerprint() == before, "fresh revoked-client denial writes nothing");
        }
        using (var fixture = await CreateFixtureAsync())
        {
            fixture.Access.SetGrant(fixture.NativeGrant with { ExpiresAt = FabricSourceTests.Now.AddSeconds(2) },
                "fixture", "shorten synthetic native grant");
            var before = fixture.Fingerprint();
            var data = await fixture.ReadAsync();
            Check(data?.Evidence.ValidUntil == FabricSourceTests.Format(FabricSourceTests.Now.AddSeconds(2)),
                "actual native grant expiry shortens HR3 evidence before request deadline");
            Check(fixture.Fingerprint() == before, "short grant lease is projected without writing");
        }
        using (var fixture = await CreateFixtureAsync())
        {
            fixture.PublishLegacy("Deny", FabricSourceTests.Now.AddSeconds(2));
            var before = fixture.Fingerprint();
            var data = await fixture.ReadAsync();
            Check(data?.Evidence.ValidUntil == FabricSourceTests.Format(FabricSourceTests.Now.AddSeconds(2)),
                "future published native deny boundary caps the current manual-grant witness");
            Check(fixture.Fingerprint() == before, "scheduled-veto projection writes nothing");
            fixture.Transport.SetTime(FabricSourceTests.Now.AddSeconds(2));
            Check(await fixture.ReadAsync() is null, "fresh native evaluation observes scheduled deny at its exclusive witness boundary");
        }
        return count;
    }

    public static async Task<Fixture> CreateFixtureAsync()
    {
        var transport = await FabricSourceTests.CreateFixtureAsync();
        try { return new Fixture(transport); }
        catch { SqliteConnection.ClearAllPools(); transport.Dispose(); throw; }
    }

    public sealed class MutableAuthenticationSource : IHr3RetainedAuthenticationSource
    {
        public Hr3RetainedAuthenticationProof? Value { get; set; }
        public int Reads { get; private set; }
        public Action<int, MutableAuthenticationSource>? BeforeRead { get; set; }
        public Hr3RetainedAuthenticationProof? ReadCurrent(TrustedAccessSnapshotQuery query,
            AuthenticatedHrReadPdpCaller caller, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); Reads++; BeforeRead?.Invoke(Reads, this);
            return Value;
        }
    }

    public sealed class Fixture : IDisposable
    {
        internal Fixture(FabricSourceTests.Fixture transport)
        {
            Transport = transport;
            RegistryPath = Path.Combine(Path.GetDirectoryName(transport.DatabasePath)!, "central-registry.db");
            Registry = new PlatformRegistry(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { ["Platform:RegistryPath"] = RegistryPath }).Build());
            Access = new PersistentAccessManagement(transport.Database, new InMemoryAccessManagement(Registry));
            Registry.SetTenantSource(Access.GetTenants); Registry.SetMembershipSource(Access.GetUsers);
            Legacy = new AccessPolicyStore(transport.Database, Access);
            Product = Registry.Create(new(Guid.Parse(transport.Binding.RegisteredProductId), HrReadDesign.ProductKey,
                "Synthetic isolated HR", HrReadDesign.Audience, "fanasa.ayin", null, null, null, "platform"), "fixture");
            Tenant = new(Guid.Parse(transport.Binding.ConsumerTenantId), "isolated-hr", "Synthetic isolated consumer",
                true, FabricSourceTests.Now.AddHours(-2));
            Member = new(Guid.Parse("88888888-8888-4888-8888-888888888888"), Tenant.Id,
                transport.Request.SubjectId, "Synthetic isolated member", true, FabricSourceTests.Now.AddHours(-1));
            Role = new(transport.Assignment.RoleId, Tenant.Id, HrReadDesign.ProductKey, HrReadDesign.Role,
                "Synthetic isolated read role", [HrReadDesign.Permission]);
            transport.Put("tenant", Tenant.Id.ToString("D"), Tenant);
            transport.Put("membership", Member.Id.ToString("D"), Member);
            transport.Put("access-role", Role.Id.ToString("D"), Role);
            GatewayClient = Access.RegisterClient(Product.Id, new(transport.CallerSettings.ClientId,
                "Synthetic isolated Gateway", "confidential", [], [transport.CallerSettings.RequiredScope]));
            SubjectClient = Access.RegisterClient(Product.Id, new(transport.Request.ClientId,
                "Synthetic isolated HR caller", "confidential", [], transport.Query.AuthenticatedSubject.Scopes.ToArray()));
            NativeGrant = new(Tenant.Id, transport.Request.SubjectId, HrReadDesign.Permission, FabricSourceTests.Now.AddHours(1));
            Access.SetGrant(NativeGrant, "fixture", "explicit isolated native grant");
            ProofSource.Value = NewProof();
            Adapter = new(transport.Database, Registry, Access, Legacy, ProofSource, transport.Time);
        }
        public FabricSourceTests.Fixture Transport { get; }
        public PlatformRegistry Registry { get; }
        public PersistentAccessManagement Access { get; }
        public AccessPolicyStore Legacy { get; }
        public PlatformProduct Product { get; }
        public Tenant Tenant { get; }
        public TenantUser Member { get; }
        public AccessRole Role { get; }
        public ProductClient GatewayClient { get; }
        public ProductClient SubjectClient { get; }
        public TenantGrant NativeGrant { get; }
        public string NativeGrantKey => $"{Tenant.Id:D}:{NativeGrant.Subject}:{HrReadDesign.Permission}";
        public string RegistryPath { get; }
        public MutableAuthenticationSource ProofSource { get; } = new();
        public Hr3CentralHostRecords Adapter { get; }
        public ValueTask<AccessAttributeSnapshotData?> ReadAsync(CancellationToken cancellationToken = default)
            => Transport.CreateSource(Adapter).ReadAsync(Transport.Query, cancellationToken);
        public void PutClient(ProductClient client) => Transport.Put("client", client.Id.ToString("D"), client);
        public Hr3RetainedAuthenticationProof NewProof(Guid? consumerTenantId = null, string? subjectId = null,
            string? subjectClientId = null, string? tokenHash = null, string? requestId = null,
            string? authorityRevision = null, DateTimeOffset? validUntil = null)
            => new(Transport.CallerSettings.SubjectId, Transport.CallerSettings.ClientId, Transport.Caller.HeaderSha256,
                subjectId ?? Transport.Request.SubjectId, subjectClientId ?? Transport.Request.ClientId,
                tokenHash ?? Transport.Caller.SubjectTokenSha256, Transport.Caller.SubjectValidUntil,
                consumerTenantId ?? Tenant.Id, Transport.Query.Selector!, Transport.Query.Action,
                Transport.Query.AuthenticatedSubject.Scopes, authorityRevision ?? "isolated-current-revision-1",
                FabricSourceTests.Now, validUntil ?? FabricSourceTests.Now.AddSeconds(5),
                Transport.CallerSettings.RequiredScope, requestId ?? Transport.Request.RequestId);
        public void PublishLegacy(string effect, DateTimeOffset? startsAt = null)
        {
            var state = Legacy.Read(Tenant.Id);
            var draft = Legacy.Draft(Tenant.Id, new(state.Revision, "Synthetic isolated native policy", "member-window",
                HrReadDesign.Permission, effect, null, null, startsAt ?? FabricSourceTests.Now.AddHours(-1),
                FabricSourceTests.Now.AddHours(1), null, null, "explicit isolated policy"), "isolated-author");
            Legacy.Transition(Tenant.Id, draft.Patterns[^1].Id, draft.Revision, "publish", "isolated-publisher", "independent isolated publish");
        }
        public string Fingerprint()
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
                { DataSource = RegistryPath, Pooling = false }.ToString());
            connection.Open();
            var rows = new List<string>();
            foreach (var (table, order) in new[] { ("platform_tenants", "id"), ("platform_grants", "subject,tenant,permission"),
                ("platform_products", "key"), ("platform_audit", "id"), ("platform_meta", "key") })
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT * FROM " + table + " ORDER BY " + order;
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var values = new object[reader.FieldCount]; reader.GetValues(values);
                    rows.Add(table + ":" + JsonSerializer.Serialize(values.Select(x => x is DBNull ? null : x).ToArray()));
                }
            }
            return Transport.DataFingerprint() + "\n" + string.Join('\n', rows);
        }
        public void Dispose()
        { SqliteConnection.ClearAllPools(); Transport.Dispose(); }
    }
}
