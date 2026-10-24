using System.Net;
using System.Net.Http.Json;
using Fanasa.AccessManagement.Web.Api;
using Fanasa.AccessManagement.Web.Governance;

static class PipHrBoundaryTests
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        await using var fixture = await SyntheticHrHost.Start(mode: "attributes");
        var token = fixture.AttributeToken();
        using (var valid = await fixture.Attributes(token))
        {
            var envelope = await valid.Content.ReadFromJsonAsync<OrganizationHrAttributeEnvelope>();
            check(valid.StatusCode == HttpStatusCode.OK && envelope?.SchemaVersion == 2 && envelope.TenantId == fixture.Tenant
                && envelope.Subject == fixture.HumanSubject && envelope.Attributes["org.member.active"] == "true"
                && envelope.Attributes["org.assignment.count"] == "0" && valid.Headers.CacheControl?.NoStore == true,
                "HR attributes real mTLS and signed JWT return correlated schema2 facts with no-store");
        }
        await Expect(fixture.AttributeToken(issuer: "https://other.invalid/realms/fanasa"), HttpStatusCode.Unauthorized, "HR attributes reject wrong issuer");
        await Expect(fixture.AttributeToken(audience: "another-audience"), HttpStatusCode.Unauthorized, "HR attributes reject wrong audience");
        await Expect(fixture.AttributeToken(badKey: true), HttpStatusCode.Unauthorized, "HR attributes reject forged token signature");
        await Expect(fixture.AttributeToken(expired: true), HttpStatusCode.Unauthorized, "HR attributes reject expired JWT");
        await Expect(fixture.AttributeToken(client: "another-client"), HttpStatusCode.Forbidden, "HR attributes require exact registered reader client");
        await Expect(fixture.AttributeToken(subject: fixture.HumanSubject), HttpStatusCode.Forbidden, "HR attributes require exact reader service subject");
        await Expect(fixture.AttributeToken(scope: SyntheticHrHost.ReadScope), HttpStatusCode.Forbidden, "General HR scope does not authorize attribute ingestion");
        await Expect(token, HttpStatusCode.Forbidden, "HR attributes require actual gateway certificate", gateway: false);
        await Expect(token, HttpStatusCode.Forbidden, "Forwarded certificate and permission headers cannot impersonate gateway", gateway: false, forged: true);
        await Expect(token, HttpStatusCode.Forbidden, "HR reader tenant allowlist rejects another tenant", tenant: fixture.OtherTenant);
        await Expect(token, HttpStatusCode.BadRequest, "HR attributes reject duplicate subject query fields", query: $"?subject={fixture.HumanSubject}&subject={fixture.HumanSubject}");
        await Expect(token, HttpStatusCode.BadRequest, "HR attributes reject query tenant mismatch", query: $"?subject={fixture.HumanSubject}&tenantId={fixture.OtherTenant}");
        await Expect(token, HttpStatusCode.BadRequest, "HR attributes reject undeclared query fields", query: $"?subject={fixture.HumanSubject}&admin=true");
        await Expect(token, HttpStatusCode.NotFound, "HR attributes return404 for unknown canonical subject", query: "?subject=" + Guid.NewGuid().ToString("D"));
        await fixture.IssueCookie();
        await Expect(null, HttpStatusCode.Unauthorized, "Browser cookie cannot authenticate HR attributes");

        var member = fixture.Access.GetUsers(fixture.Tenant).Single(x => x.KeycloakSubject == fixture.HumanSubject);
        fixture.Access.SetMembership(fixture.Tenant, member.Id, false, "test", "synthetic inactive human");
        using (var inactive = await fixture.Attributes(token))
        {
            var envelope = await inactive.Content.ReadFromJsonAsync<OrganizationHrAttributeEnvelope>();
            check(inactive.StatusCode == HttpStatusCode.OK && envelope?.Attributes["org.member.active"] == "false"
                && envelope.Attributes["org.assignment.count"] == "0", "HR endpoint returns explicit inactive member facts");
        }
        fixture.Access.SetMembership(fixture.Tenant, member.Id, true, "test", "restore synthetic human");
        fixture.PutClient(fixture.AttributeRegisteredClient! with { IsActive = false });
        await Expect(token, HttpStatusCode.Forbidden, "Inactive registered reader client loses attribute access");
        fixture.PutClient(fixture.AttributeRegisteredClient!);
        fixture.PutClient(fixture.AttributeRegisteredClient! with { AllowedScopes = [] });
        await Expect(token, HttpStatusCode.Forbidden, "Removed registered scope loses attribute access despite token claim");
        fixture.PutClient(fixture.AttributeRegisteredClient!);
        fixture.Access.SetGrant(new(fixture.Tenant, fixture.AttributeServiceSubject, "organization.read", DateTimeOffset.UtcNow.AddMinutes(-1)), "test", "synthetic expiry");
        await Expect(token, HttpStatusCode.Forbidden, "Expired tenant reader grant denies HR attributes");
        RestoreReaderGrant();
        fixture.Access.SetMembership(fixture.Tenant, fixture.AttributeServiceMember!.Id, false, "test", "synthetic service revocation");
        await Expect(token, HttpStatusCode.Forbidden, "Revoked service membership denies HR attributes");
        fixture.Access.SetMembership(fixture.Tenant, fixture.AttributeServiceMember.Id, true, "test", "restore synthetic reader"); RestoreReaderGrant();
        var subscription = fixture.Access.GetSubscriptions(fixture.Tenant).Single();
        fixture.Access.SetSubscription(fixture.Tenant, subscription.Id, "suspended", null, "test", "synthetic suspension");
        await Expect(token, HttpStatusCode.Forbidden, "Attribute bootstrap preserves live organization subscription gate");
        fixture.Access.SetSubscription(fixture.Tenant, subscription.Id, "active", DateTimeOffset.UtcNow.AddDays(1), "test", "restore synthetic subscription");
        var longLivedToken = fixture.AttributeToken(expiresAt: DateTime.UtcNow.AddDays(1));
        fixture.Access.SetSubscription(fixture.Tenant, subscription.Id, "active", DateTimeOffset.UtcNow.AddMinutes(1), "test", "short synthetic subscription");
        await CrossBoundary(longLivedToken, TimeSpan.FromMinutes(2), "Subscription expiry during projection is rechecked when general development subscription checks are disabled");
        fixture.Access.SetSubscription(fixture.Tenant, subscription.Id, "active", DateTimeOffset.UtcNow.AddDays(1), "test", "restore synthetic subscription");
        fixture.Access.SetGrant(new(fixture.Tenant, fixture.AttributeServiceSubject, "organization.read", DateTimeOffset.UtcNow.AddMinutes(1)), "test", "short synthetic grant");
        await CrossBoundary(longLivedToken, TimeSpan.FromMinutes(2), "Reader grant expiry during projection is rechecked before returning facts");
        RestoreReaderGrant();
        fixture.StoreBarrier.Arm();
        var delayed = fixture.Attributes(token);
        try
        {
            await fixture.StoreBarrier.Entered.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.AttributeClock.Offset = TimeSpan.FromMinutes(10);
        }
        finally { fixture.StoreBarrier.Release(); }
        using (var expiredWhileReading = await delayed)
            check(expiredWhileReading.StatusCode == HttpStatusCode.Forbidden && fixture.StoreBarrier.EnteredInTransaction,
                "Reader JWT is rechecked after projection inside the same Fabric read snapshot");
        fixture.AttributeClock.Offset = TimeSpan.Zero;

        // Synthetic loopback TLS client is injected only by the fixture; production still uses its DNS/IP boundary.
        var pip = fixture.AttributePip!;
        var source = pip.Register(fixture.Tenant, "Synthetic real HR", fixture.AttributeEndpoint, "127.0.0.1", "test-reader", schemaVersion: 2,
            maxAgeSeconds: 60, enabled: true, definitions: OrganizationHrAttributeProjector.Definitions);
        var policies = fixture.AttributePolicies!;
        var draft = policies.Draft(fixture.Tenant, new PatternCommand(0, "Current organization member", "attribute", "organization.read", "Permit",
            null, null, null, null, null, null, "authoritative membership", null, "working-time", true, "org.member.active", "true", source.Id), "test");
        policies.Transition(fixture.Tenant, draft.Patterns[^1].Id, draft.Revision, "publish", "reviewer", "synthetic policy");
        using (var chart = await fixture.Chart(fixture.Token(scope: SyntheticHrHost.ReadScope)))
            check(chart.StatusCode == HttpStatusCode.Forbidden, "Business chart remains gated while required PIP facts are missing");
        using (var bootstrap = await fixture.Attributes(token, accessOnly: true))
            check(bootstrap.StatusCode == HttpStatusCode.NoContent, "Separate reader bootstrap does not depend on missing business PIP evidence");
        var transport = new PipTransportOptions { Bindings = [new() { TenantId = fixture.Tenant, Endpoint = fixture.AttributeEndpoint, SecretReference = "test-reader" }] };
        var connector = fixture.AttributeConnector(transport, token);
        var fetched = await connector.FetchAsync(source, fixture.HumanSubject);
        var snapshot = pip.SaveSnapshot(source, fixture.HumanSubject, fetched, fixture.AttributeClock.GetUtcNow());
        check(snapshot.ValidUntil == fetched.ValidUntil && snapshot.FreshUntil <= fetched.ValidUntil
            && policies.Evaluate(fixture.Tenant, fixture.HumanSubject, "organization.read", fixture.AttributeClock.GetUtcNow()).Decision == "Permit",
            "Real TLS HR response passes actual PIP parsing into Fabric snapshot and a permit decision");
        check(policies.Evaluate(fixture.Tenant, fixture.HumanSubject, "organization.read", snapshot.FreshUntil).Decision == "Indeterminate",
            "Actual HR snapshot fails closed in PDP at its exact validity deadline");
        var serviceFacts = await connector.FetchAsync(source, fixture.ServiceSubject);
        pip.SaveSnapshot(source, fixture.ServiceSubject, serviceFacts, fixture.AttributeClock.GetUtcNow());
        using (var chart = await fixture.Chart(fixture.Token(scope: SyntheticHrHost.ReadScope)))
            check(chart.StatusCode == HttpStatusCode.OK, "Business chart permits only after fresh authoritative service facts are ingested");

        void RestoreReaderGrant() => fixture.Access.SetGrant(new(fixture.Tenant, fixture.AttributeServiceSubject, "organization.read", null), "test", "restore synthetic grant");
        async Task CrossBoundary(string bearer, TimeSpan offset, string label)
        {
            fixture.StoreBarrier.Arm();
            var pending = fixture.Attributes(bearer);
            try { await fixture.StoreBarrier.Entered.WaitAsync(TimeSpan.FromSeconds(5)); fixture.AttributeClock.Offset = offset; }
            finally { fixture.StoreBarrier.Release(); }
            try { using var response = await pending; check(response.StatusCode == HttpStatusCode.Forbidden && fixture.StoreBarrier.EnteredInTransaction, label); }
            finally { fixture.AttributeClock.Offset = TimeSpan.Zero; }
        }
        async Task Expect(string? bearer, HttpStatusCode status, string label, string? query = null, Guid? tenant = null, bool gateway = true, bool forged = false)
        { using var response = await fixture.Attributes(bearer, query, tenant, gateway, forged); check(response.StatusCode == status, label + $" (got {(int)response.StatusCode})"); }
    }
}
