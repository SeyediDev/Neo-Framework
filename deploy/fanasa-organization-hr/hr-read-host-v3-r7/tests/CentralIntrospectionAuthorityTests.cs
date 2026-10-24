using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Domain.Access;
using Fanasa.HrReadCandidate;
using Fanasa.HrReadPdp;
using Fanasa.HrReadPdp.Tests;

namespace Fanasa.HrReadHost.Tests;

/// <summary>Real isolated native central records; no live introspection or identity provisioning.</summary>
public static class CentralIntrospectionAuthorityTests
{
    public static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool condition, string label)
        { count++; if (!condition) throw new InvalidOperationException("HR3 native introspection authority test failed: " + label); }
        async Task Reject(Action<CentralHostRecordsTests.Fixture> mutate, string label)
        {
            using var fixture = await CentralHostRecordsTests.CreateFixtureAsync();
            mutate(fixture); var before = fixture.Fingerprint();
            Check(Read(fixture) is null, label);
            Check(fixture.Fingerprint() == before, label + " does not write native/registry/outbox state");
        }

        using (var fixture = await CentralHostRecordsTests.CreateFixtureAsync())
        {
            var authority = Create(fixture); var before = fixture.Fingerprint();
            var first = authority.ReadCurrent(fixture.Transport.Request, fixture.Transport.Caller, CancellationToken.None);
            var second = authority.ReadCurrent(fixture.Transport.Request, fixture.Transport.Caller, CancellationToken.None);
            Check(first is not null && first.ConsumerTenantId == fixture.Tenant.Id,
                "unique consumer is independently selected from actual native grant/tenant/membership records");
            Check(first is not null && first.GatewaySubjectId == fixture.Transport.CallerSettings.SubjectId
                && first.GatewayClientId == fixture.GatewayClient.Key
                && first.SubjectId == fixture.Member.KeycloakSubject && first.SubjectClientId == fixture.SubjectClient.Key,
                "both authenticated caller tuples and current registered clients are retained");
            Check(first is not null && first.Revision is { Length: 64 } && first.Revision.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f')
                && first.Revision == second?.Revision,
                "stable revision hashes current native authority content rather than time samples");
            Check(first?.ValidUntil == FabricSourceTests.Now.AddSeconds(5), "authority lease is bounded by the five-second request/native proof");
            Check(!fixture.Access.GetUsers(fixture.Tenant.Id).Any(x => x.KeycloakSubject == fixture.Transport.CallerSettings.SubjectId),
                "Gateway delegation uses its registered native client and does not invent service-account membership");
            Check(!fixture.Transport.Database.IsInTransaction && fixture.Fingerprint() == before,
                "standalone authority read closes its short snapshot and writes nothing");
            var ambient = fixture.Transport.Database.ReadSnapshot(() => authority.ReadCurrent(
                fixture.Transport.Request, fixture.Transport.Caller, CancellationToken.None));
            Check(ambient?.Revision == first?.Revision && fixture.Fingerprint() == before,
                "retained proof can reuse the SAME existing query-only snapshot without nested transaction or writes");

            // A membership alone is not an authenticated consumer mapping, regardless of
            // enumeration order. Only an eligible direct native permission participates.
            AddOtherConsumer(fixture, grant: false);
            before = fixture.Fingerprint();
            Check(Read(fixture)?.ConsumerTenantId == fixture.Tenant.Id,
                "an unrelated second active membership never becomes a first-membership consumer fallback");
            Check(fixture.Fingerprint() == before, "membership-only consumer discovery writes nothing");

            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            var cancelled = false;
            try { authority.ReadCurrent(fixture.Transport.Request, fixture.Transport.Caller, cancellation.Token); }
            catch (OperationCanceledException) { cancelled = true; }
            Check(cancelled, "cancellation propagates before authoritative reads");
        }

        await Reject(f => f.PutClient(f.GatewayClient with { IsActive = false }), "current Gateway registered client revocation denies");
        await Reject(f => f.PutClient(f.SubjectClient with { IsActive = false }), "current subject registered client revocation denies");
        await Reject(f => f.PutClient(f.GatewayClient with { AllowedScopes = ["other.scope"] }), "native Gateway delegation requires the exact configured scope");
        await Reject(f => f.PutClient(f.SubjectClient with { AllowedScopes = ["openid"] }), "current subject native client must allow HR scope");
        await Reject(f => f.PutClient(f.SubjectClient with { AllowedScopes = [HrReadDesign.ReadScope] }), "all retained schema-representable subject scopes remain authorized");
        await Reject(f => f.PutClient(f.GatewayClient with { ProductId = Guid.NewGuid() }), "Gateway client must remain under the registered HR product");
        await Reject(f => f.PutClient(f.SubjectClient with { ProductId = Guid.NewGuid() }), "subject client must remain under the registered HR product");
        await Reject(f => f.Registry.Register(f.Product with { Audience = "unrelated-audience" }, "fixture"), "actual product audience must match central registration intent");
        await Reject(f => f.Registry.Register(f.Product with { Center = "fanasa.dejan" }, "fixture"), "actual product capability center must remain Ayin");
        await Reject(f => f.Transport.Delete("tenant-grant", f.NativeGrantKey), "no native direct read grant means no independently authenticated consumer");
        await Reject(f => f.Access.SetGrant(f.NativeGrant with { ExpiresAt = FabricSourceTests.Now }, "fixture", "expire isolated native grant"), "native consumer grant expiry is exclusive");
        await Reject(f => f.Transport.Put("tenant", f.Tenant.Id.ToString("D"), f.Tenant with { IsActive = false }), "inactive native tenant cannot supply consumer mapping");
        await Reject(f => f.Access.SetMembership(f.Tenant.Id, f.Member.Id, false, "fixture", "revoke isolated native member"), "inactive native membership cannot supply consumer mapping");
        await Reject(f => f.Transport.Delete("membership", f.Member.Id.ToString("D")), "missing actual native membership denies");
        await Reject(f => f.Transport.Put("membership", f.Member.Id.ToString("D"), f.Member with { JoinedAt = FabricSourceTests.Now.AddSeconds(1) }), "future-dated native membership is not asserted current");
        await Reject(f => AddOtherConsumer(f, grant: true), "two eligible native consumer mappings deny instead of selecting the route's tenant");
        await Reject(f =>
        {
            AddOtherConsumer(f, grant: true);
            f.Access.SetGrant(f.NativeGrant with { ExpiresAt = FabricSourceTests.Now }, "fixture", "expire first isolated consumer");
        }, "a unique different native consumer cannot be replaced by the requested consumer");
        await Reject(f => f.Transport.Http.Request.Headers[HrReadPdpAuthentication.SubjectHeader] = "Bearer altered.subject.token",
            "changed retained subject bearer invalidates native authority before snapshot");
        await Reject(f => f.Transport.Http.Request.Headers.Authorization = "Bearer altered.gateway.token",
            "changed retained Gateway bearer invalidates native delegation before snapshot");
        await Reject(f => f.Transport.SetTime(FabricSourceTests.Now.AddSeconds(5)), "native authentication/request deadline is exclusive");

        using (var fixture = await CentralHostRecordsTests.CreateFixtureAsync())
        {
            var authority = Create(fixture);
            var first = authority.ReadCurrent(fixture.Transport.Request, fixture.Transport.Caller, CancellationToken.None)!;
            fixture.Access.SetGrant(fixture.NativeGrant with { ExpiresAt = FabricSourceTests.Now.AddSeconds(2) },
                "fixture", "shorten isolated consumer grant");
            var before = fixture.Fingerprint();
            var current = authority.ReadCurrent(fixture.Transport.Request, fixture.Transport.Caller, CancellationToken.None);
            Check(current is not null && current.ValidUntil == FabricSourceTests.Now.AddSeconds(2)
                && current.Revision != first.Revision,
                "fresh native grant content changes revision and shortens independent authority lease");
            Check(fixture.Fingerprint() == before, "authority revision projection does not persist a revision or lease");
            fixture.Transport.SetTime(FabricSourceTests.Now.AddSeconds(2));
            Check(authority.ReadCurrent(fixture.Transport.Request, fixture.Transport.Caller, CancellationToken.None) is null,
                "a fresh native read observes grant expiry without a cached mapping");
        }
        using (var fixture = await CentralHostRecordsTests.CreateFixtureAsync())
        {
            var original = Read(fixture)!;
            fixture.Transport.SetTime(FabricSourceTests.Now.AddSeconds(1));
            var current = Read(fixture);
            Check(current is not null && current.Revision == original.Revision && current.ValidUntil <= fixture.Transport.Request.ValidUntil,
                "time advances without changing native content revision or extending the request witness");
        }
        using (var fixture = await CentralHostRecordsTests.CreateFixtureAsync())
        {
            AddOtherConsumer(fixture, grant: true, activeMember: false);
            var before = fixture.Fingerprint();
            Check(Read(fixture)?.ConsumerTenantId == fixture.Tenant.Id,
                "an inactive second native member is ineligible despite its direct grant");
            Check(fixture.Fingerprint() == before, "ineligible native candidate filtering writes nothing");
        }
        using (var fixture = await CentralHostRecordsTests.CreateFixtureAsync())
        {
            AddOtherConsumer(fixture, grant: true, expiresAt: FabricSourceTests.Now);
            Check(Read(fixture)?.ConsumerTenantId == fixture.Tenant.Id,
                "an expired second native grant cannot make the current consumer ambiguous");
        }
        using (var fixture = await CentralHostRecordsTests.CreateFixtureAsync())
        {
            var badGateway = fixture.Transport.CallerSettings with { SubjectId = "77777777-7777-4777-8777-777777777777" };
            Check(new Hr3CentralIntrospectionAuthority(fixture.Transport.Database, fixture.Registry, fixture.Access,
                fixture.Transport.Binding, badGateway, fixture.Transport.Time).ReadCurrent(
                    fixture.Transport.Request, fixture.Transport.Caller, CancellationToken.None) is null,
                "configured exact Gateway identity must match independently retained native authentication");
            Check(new Hr3CentralIntrospectionAuthority(fixture.Transport.Database, fixture.Registry, fixture.Access,
                fixture.Transport.Binding with { Enabled = false }, fixture.Transport.CallerSettings, fixture.Transport.Time).ReadCurrent(
                    fixture.Transport.Request, fixture.Transport.Caller, CancellationToken.None) is null,
                "disabled configuration cannot enable a current authority source");
            var request = fixture.Transport.Request;
            var anotherRequest = new HrReadPdpRequest(fixture.Transport.Binding, request.Target, request.RawTarget,
                request.RequestId, request.RequestedAt, request.TokenSha256, request.TokenExpiresAt);
            Check(Create(fixture).ReadCurrent(anotherRequest, fixture.Transport.Caller, CancellationToken.None) is null,
                "proof cannot be reused for another parsed request instance even with identical fields");
        }
        using (var fixture = await CentralHostRecordsTests.CreateFixtureAsync())
        {
            fixture.Transport.Delete("access-role", fixture.Role.Id.ToString("D"));
            var before = fixture.Fingerprint();
            Check(Read(fixture) is not null && await fixture.ReadAsync() is null,
                "introspection consumer/native-client authority does not fabricate the separately required PDP role");
            Check(fixture.Fingerprint() == before, "separate native mapping and PDP role checks are read-only");
        }
        return count;
    }

    public static Hr3CentralIntrospectionAuthority Create(CentralHostRecordsTests.Fixture fixture)
        => new(fixture.Transport.Database, fixture.Registry, fixture.Access, fixture.Transport.Binding,
            fixture.Transport.CallerSettings, fixture.Transport.Time);
    public static Hr3IntrospectionAuthoritySnapshot? Read(CentralHostRecordsTests.Fixture fixture)
        => Create(fixture).ReadCurrent(fixture.Transport.Request, fixture.Transport.Caller, CancellationToken.None);

    public static void AddOtherConsumer(CentralHostRecordsTests.Fixture fixture, bool grant,
        bool activeMember = true, DateTimeOffset? expiresAt = null)
    {
        var tenant = new Tenant(Guid.Parse("99999999-9999-4999-8999-999999999999"), "isolated-other-consumer",
            "Synthetic isolated other consumer", true, FabricSourceTests.Now.AddHours(-2));
        var member = new TenantUser(Guid.Parse("abababab-abab-4bab-8bab-abababababab"), tenant.Id,
            fixture.Transport.Request.SubjectId, "Synthetic isolated other member", activeMember, FabricSourceTests.Now.AddHours(-1));
        fixture.Transport.Put("tenant", tenant.Id.ToString("D"), tenant);
        fixture.Transport.Put("membership", member.Id.ToString("D"), member);
        // SetGrant correctly rejects inactive members. The explicit typed row is used only
        // for the inactive-member negative fixture, never as a production publisher.
        if (grant)
        {
            var value = new TenantGrant(tenant.Id, fixture.Transport.Request.SubjectId,
                HrReadDesign.Permission, expiresAt ?? FabricSourceTests.Now.AddHours(1));
            if (activeMember) fixture.Access.SetGrant(value, "fixture", "explicit isolated second native grant");
            else fixture.Transport.Put("tenant-grant", $"{tenant.Id:D}:{value.Subject}:{value.Permission}", value);
        }
    }
}
