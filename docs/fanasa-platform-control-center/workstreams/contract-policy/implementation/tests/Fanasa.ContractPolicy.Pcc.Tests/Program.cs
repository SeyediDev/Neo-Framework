using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fanasa.ContractPolicy;
using Fanasa.AccessManagement.Web;
using Fanasa.AccessManagement.Web.Api;
using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.ContractPolicies;
using Fanasa.AccessManagement.Web.Domain.Access;
using Fanasa.AccessManagement.Web.Governance;
using Fanasa.AccessManagement.Web.Organization;
using Fanasa.AccessManagement.Web.Persistence;
using Fanasa.AccessManagement.Web.Platform;
using Fanasa.AccessManagement.Web.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

// Local integration evidence: real PCC services, real JWT validation, real Kestrel
// and temporary SQLite. All identities, contracts and RSA keys are synthetic.
var passed = 0;
var failed = 0;
async Task Test(string name, Func<Task> body)
{
    try { await body(); passed++; Console.WriteLine("PASS PCC " + name); }
    catch (Exception error) { failed++; Console.Error.WriteLine("FAIL PCC " + name + ": " + error.Message); }
}

await using (var host = await PccFixture.Start())
{
    foreach (var (name, token, cookie) in new (string, string?, bool)[]
    {
        ("anonymous producer", null, false), ("cookie cannot publish contracts", null, true),
        ("bad producer signature", host.Token(producer: true, badKey: true), false),
        ("wrong producer issuer", host.Token(producer: true, issuer: "https://forged.invalid/realms/fanasa"), false),
        ("wrong producer audience", host.Token(producer: true, audience: "another-api"), false),
        ("expired producer JWT", host.Token(producer: true, expiresAt: DateTime.UtcNow.AddMinutes(-1)), false),
        ("wrong producer client", host.Token(producer: true, client: "another-client"), false),
        ("missing producer scope", host.Token(producer: true, scope: "contracts.read"), false)
    })
        await Test(name, async () =>
        {
            var before = host.PersistenceSnapshot();
            using var response = await host.Ingest(host.Event, token, cookie);
            Check(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, "denied HTTP status: " + response.StatusCode);
            Equal(before, host.PersistenceSnapshot());
        });

    foreach (var claim in new[] { "iss", "azp", "sub", "scope", "aud", "exp", "nbf" })
        await Test("signed producer duplicate " + claim + " is denied", async () =>
        {
            var token = host.RawToken(payload => payload.Insert(1, JsonSerializer.Serialize(claim) + ":" + JsonNode.Parse(payload)![claim]!.ToJsonString() + ","));
            var before = host.PersistenceSnapshot(); using var response = await host.Ingest(host.Event, token);
            Equal(HttpStatusCode.Unauthorized, response.StatusCode); Equal(before, host.PersistenceSnapshot());
        });
    await Test("escaped duplicate producer issuer has the same decoded claim name", async () =>
    {
        var token = host.RawToken(payload => payload.Insert(1, "\"\\u0069ss\":" + JsonSerializer.Serialize(PccFixture.Issuer) + ","));
        var before = host.PersistenceSnapshot(); using var response = await host.Ingest(host.Event, token);
        Equal(HttpStatusCode.Unauthorized, response.StatusCode); Equal(before, host.PersistenceSnapshot());
    });
    foreach (var mode in new[] { "missing-exp", "quoted-exp", "fractional-exp", "exponent-exp", "quoted-nbf", "negative-nbf", "future-nbf", "array-scope", "tab-scope", "mixed-audience" })
        await Test("signed malformed producer " + mode + " is denied", async () =>
        {
            var token = host.MalformedToken(mode); var before = host.PersistenceSnapshot();
            using var response = await host.Ingest(host.Event, token);
            Equal(HttpStatusCode.Unauthorized, response.StatusCode); Equal(before, host.PersistenceSnapshot());
        });

    await Test("central producer registration is checked on each request", async () =>
    {
        var original = host.ProducerClient;
        host.Database.Transaction(() => { host.Database.Put("client", original.Id.ToString(), original with { IsActive = false }); return true; });
        try
        {
            var before = host.PersistenceSnapshot(); using var response = await host.Ingest(host.Event, host.Token(producer: true));
            Check(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, "inactive producer client rejected"); Equal(before, host.PersistenceSnapshot());
        }
        finally { host.Database.Transaction(() => { host.Database.Put("client", original.Id.ToString(), original); return true; }); }
    });
    await Test("central producer allowed scope cannot be forged by JWT claims", async () =>
    {
        var original = host.ProducerClient;
        host.Database.Transaction(() => { host.Database.Put("client", original.Id.ToString(), original with { AllowedScopes = [] }); return true; });
        try
        {
            var before = host.PersistenceSnapshot(); using var response = await host.Ingest(host.Event, host.Token(producer: true));
            Check(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, "revoked producer scope rejected"); Equal(before, host.PersistenceSnapshot());
        }
        finally { host.Database.Transaction(() => { host.Database.Put("client", original.Id.ToString(), original); return true; }); }
    });
    await Test("registered signed producer persists the normalized complete projection", async () =>
    {
        using var response = await host.Ingest(host.Event, host.Token(producer: true));
        Equal(HttpStatusCode.Accepted, response.StatusCode); Equal("applied", await Status(response));
        EqualJson(ContractJson.ToNode(host.Event.Projection), ContractJson.ToNode(host.Store.Read(host.ContractId)!.Projection));
        Equal(1, host.Store.Read(host.ContractId)!.Receipts.Count);
    });
    await Test("exact HTTP replay returns duplicate and adds no durable mutation", async () =>
    {
        var before = host.PersistenceSnapshot(); using var response = await host.Ingest(host.Event, host.Token(producer: true));
        Equal(HttpStatusCode.Accepted, response.StatusCode); Equal("duplicate", await Status(response)); Equal(before, host.PersistenceSnapshot());
    });
    await Test("signed producer expiring during streamed body read cannot commit", async () =>
    {
        var before = host.PersistenceSnapshot();
        var expires = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.AddSeconds(3).ToUnixTimeSeconds());
        using var body = new SlowProjectionContent(Json(host.Amend(p => p with { Status = "Suspended" })), expires.AddMilliseconds(150));
        using var response = await host.IngestContent(body, host.Token(producer: true, expiresAt: expires.UtcDateTime));
        Check(body.FirstWriteAt < expires, "first event fragment sent while signed JWT still live");
        Check(body.LastWriteAt > expires, "remaining event fragment sent after signed JWT expiry");
        Equal(HttpStatusCode.Forbidden, response.StatusCode); Equal(before, host.PersistenceSnapshot());
    });
    await Test("signed audience array retains exact registered producer authority", async () =>
    {
        var token = host.RawToken(payload => { var node = JsonNode.Parse(payload)!; node["aud"] = new JsonArray("synthetic-secondary-api", PccFixture.ProducerAudience); return node.ToJsonString(); });
        var before = host.PersistenceSnapshot(); using var response = await host.Ingest(host.Event, token);
        Equal(HttpStatusCode.Accepted, response.StatusCode); Equal("duplicate", await Status(response)); Equal(before, host.PersistenceSnapshot());
    });
    await Test("body producer cannot rename the signed central producer", async () =>
    {
        var before = host.PersistenceSnapshot();
        using var response = await host.Ingest(host.Event with { Producer = "untrusted-producer" }, host.Token(producer: true));
        Check(!response.IsSuccessStatusCode, "body producer rejected"); Equal(before, host.PersistenceSnapshot());
    });
    await Test("strict HTTP event parser rejects duplicate fields", async () =>
    {
        var before = host.PersistenceSnapshot(); var json = Json(host.Event).Insert(1, "\"schemaVersion\":\"2.0.0\",");
        using var response = await host.IngestJson(json, host.Token(producer: true));
        Check(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Conflict, "duplicate event rejected"); Equal(before, host.PersistenceSnapshot());
    });

    await Test("real registry, membership, grant, role and published policy produce pure Permit", async () =>
    {
        var before = host.BusinessSnapshot(); using var response = await host.Evaluate(host.Token());
        Equal(HttpStatusCode.OK, response.StatusCode); var decision = await Decision(response);
        Equal("Permit", decision.Decision); Equal(2, decision.Obligations.Count);
        Equal(host.ContractId, decision.ProjectionReference!.ContractId);
        Equal(host.Tenant.ToString("D"), decision.Obligations.Single(o => o.Id.EndsWith("resource-tenant:1", StringComparison.Ordinal)).Parameters["consumerTenantId"]!.GetValue<string>());
        Equal("production", decision.Obligations.Single(o => o.Id.EndsWith("resource-tenant:1", StringComparison.Ordinal)).Parameters["environment"]!.GetValue<string>());
        Check(DateTimeOffset.Parse(decision.ValidUntil!) <= DateTimeOffset.Parse(decision.EvaluatedAt!).AddSeconds(5), "server freshness ceiling retained");
        Equal(0, host.Module.Schemas.Validate("decision.v2.schema.json", ContractJson.ToNode(decision)).Count);
        Equal(before, host.BusinessSnapshot());
    });
    await Test("caller body attributes and headers cannot replace server tenant, grant or environment", async () =>
    {
        var before = host.BusinessSnapshot();
        using var response = await host.Evaluate(host.Token(), body: "{\"tenantId\":\"forged\",\"environment\":\"development\",\"platformAdmin\":true,\"grant\":{\"active\":true}}", forgedHeaders: true);
        Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Equal(before, host.BusinessSnapshot());
    });
    await Test("forged headers cannot replace server tenant or deployment on empty-body PDP", async () =>
    {
        var before = host.BusinessSnapshot(); using var response = await host.Evaluate(host.Token(), forgedHeaders: true);
        Equal(HttpStatusCode.OK, response.StatusCode); var decision = await Decision(response); Equal("Permit", decision.Decision);
        var binding = decision.Obligations.Single(o => o.Id.EndsWith("resource-tenant:1", StringComparison.Ordinal));
        Equal(host.Tenant.ToString("D"), binding.Parameters["consumerTenantId"]!.GetValue<string>()); Equal("production", binding.Parameters["environment"]!.GetValue<string>());
        Equal(before, host.BusinessSnapshot());
    });

    foreach (var (name, token, tenant, cookie) in new (string, string?, Guid?, bool)[]
    {
        ("anonymous PDP", null, null, false), ("cookie cannot evaluate bearer operation", null, null, true),
        ("bad consumer signature", host.Token(badKey: true), null, false),
        ("wrong consumer issuer", host.Token(issuer: "https://forged.invalid/realms/fanasa"), null, false),
        ("wrong consumer audience", host.Token(audience: "another-api"), null, false),
        ("expired consumer JWT", host.Token(expiresAt: DateTime.UtcNow.AddMinutes(-1)), null, false),
        ("wrong consumer client", host.Token(client: "another-client"), null, false),
        ("missing consumer scope", host.Token(scope: "openid"), null, false),
        ("unknown member cannot use forged permission claims", host.Token(actor: host.UnknownActor, extra: [new("permission", "organization.write"), new("role", "ApiInvoker")]), null, false),
        ("route tenant prevents cross-tenant access", host.Token(), host.OtherTenant, false)
    })
        await Test(name, async () => await host.NoPermit(token, tenant, cookie: cookie));

    foreach (var claim in new[] { "iss", "azp", "sub", "scope", "aud", "exp", "nbf" })
        await Test("signed consumer duplicate " + claim + " is denied", async () =>
        {
            var token = host.RawToken(payload => payload.Insert(1, JsonSerializer.Serialize(claim) + ":" + JsonNode.Parse(payload)![claim]!.ToJsonString() + ","), producer: false);
            var before = host.BusinessSnapshot(); using var response = await host.Evaluate(token);
            Equal(HttpStatusCode.Unauthorized, response.StatusCode); Equal(before, host.BusinessSnapshot());
        });
    foreach (var mode in new[] { "missing-exp", "quoted-exp", "fractional-exp", "exponent-exp", "quoted-nbf", "negative-nbf", "future-nbf" })
        await Test("signed malformed consumer " + mode + " is denied", async () =>
        {
            var before = host.BusinessSnapshot(); using var response = await host.Evaluate(host.MalformedToken(mode, producer: false));
            Equal(HttpStatusCode.Unauthorized, response.StatusCode); Equal(before, host.BusinessSnapshot());
        });

    foreach (var (name, cookie, token) in new (string, bool?, string?)[]
    {
        ("anonymous administration", null, null), ("ordinary member cookie administration", false, null),
        ("producer bearer cannot administer", null, host.Token(producer: true)),
        ("consumer bearer permission claim cannot administer", null, host.Token(extra: [new("permission", "platform.admin")]))
    })
        await Test(name, async () =>
        {
            var before = host.AdminSnapshot(); using var response = await host.Admin("policy", adminCookie: cookie, token: token);
            Check(response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.Redirect, "cookie PlatformAdmin boundary denied: " + response.StatusCode);
            Equal(before, host.AdminSnapshot());
        });
    await Test("PlatformAdmin cookie can read authoritative policy and role assignment", async () =>
    {
        var before = host.AdminSnapshot(); using var policy = await host.Admin("policy", adminCookie: true);
        Equal(HttpStatusCode.OK, policy.StatusCode); Equal(host.Tenant, (await policy.Content.ReadFromJsonAsync<PublishedContractPolicy>())!.ConsumerTenantId);
        using var role = await host.Admin("role-assignments/" + host.Actor, adminCookie: true);
        Equal(HttpStatusCode.OK, role.StatusCode); Equal(host.Role.Id, (await role.Content.ReadFromJsonAsync<ProductRoleAssignment>())!.RoleId);
        Equal(before, host.AdminSnapshot());
    });
    foreach (var route in new[] { "policy", "policy/retire", "role-assignments" })
        foreach (var forged in new[] { false, true })
            await Test("administrative " + route + (forged ? " forged" : " missing") + " CSRF is denied", async () =>
            {
                var current = host.Store.ReadPolicy(host.Tenant)!; var assignment = host.Store.ReadRoleAssignment(host.Tenant, host.Actor)!;
                object command = route switch
                {
                    "policy" => new PublishContractPolicyCommand(current.Revision, current.Binding with { PolicyVersion = current.Binding.PolicyVersion + 1 }, "synthetic CSRF refusal"),
                    "policy/retire" => new RetireContractPolicyCommand(current.Revision, "synthetic CSRF refusal"),
                    _ => new AssignContractProductRoleCommand(host.Actor, host.Role.Id, false, assignment.ValidFrom, assignment.ValidTo, assignment.Revision, "synthetic CSRF refusal")
                };
                var before = host.AdminSnapshot(); using var response = await host.Admin(route, command, adminCookie: true, forgedCsrf: forged);
                Equal(HttpStatusCode.BadRequest, response.StatusCode); Equal(before, host.AdminSnapshot());
            });
    await Test("cookie admin publishes versioned policy with CSRF without creating grants", async () =>
    {
        var current = host.Store.ReadPolicy(host.Tenant)!; var next = current.Binding with { PolicyVersion = current.Binding.PolicyVersion + 1 };
        var grants = Json(host.Access.GetGrants(host.Actor));
        using var response = await host.Admin("policy", new PublishContractPolicyCommand(current.Revision, next, "synthetic HTTP publication"), adminCookie: true, csrf: true);
        Equal(HttpStatusCode.OK, response.StatusCode); Equal(current.Revision + 1, host.Store.ReadPolicy(host.Tenant)!.Revision);
        Equal(grants, Json(host.Access.GetGrants(host.Actor))); await host.NoPermit(host.Token());
        var before = host.AdminSnapshot(); using var stale = await host.Admin("policy", new PublishContractPolicyCommand(current.Revision, next, "synthetic stale publication"), adminCookie: true, csrf: true);
        Equal(HttpStatusCode.Conflict, stale.StatusCode); Equal(before, host.AdminSnapshot());
        using var amend = await host.Ingest(host.Amend(p => p with { Policy = next }), host.Token(producer: true)); Equal(HttpStatusCode.Accepted, amend.StatusCode);
        using var restored = await host.Evaluate(host.Token()); Equal("Permit", (await Decision(restored)).Decision);
    });
    await Test("cookie admin role revocation uses CSRF and preserves independent grants", async () =>
    {
        var assignment = host.Store.ReadRoleAssignment(host.Tenant, host.Actor)!; var grants = Json(host.Access.GetGrants(host.Actor));
        using var response = await host.Admin("role-assignments", new AssignContractProductRoleCommand(host.Actor, host.Role.Id, false, DateTimeOffset.MaxValue, DateTimeOffset.MinValue, assignment.Revision, "synthetic HTTP role revocation"), adminCookie: true, csrf: true);
        Equal(HttpStatusCode.OK, response.StatusCode); var revoked = host.Store.ReadRoleAssignment(host.Tenant, host.Actor)!;
        Check(!revoked.Active, "assignment deactivated"); Equal(assignment.ValidFrom, revoked.ValidFrom); Equal(assignment.ValidTo, revoked.ValidTo);
        Equal(grants, Json(host.Access.GetGrants(host.Actor))); await host.NoPermit(host.Token()); host.AssignRole(true);
    });
    await Test("separately published legacy Deny vetoes the native contract Permit", async () =>
    {
        var legacy = host.LegacyPolicies; var current = legacy.Read(host.Tenant); var now = DateTimeOffset.UtcNow;
        var draft = legacy.Draft(host.Tenant, new(current.Revision, "Synthetic veto", "member-window", "organization.write", "Deny", null, null, now.AddMinutes(-1), now.AddMinutes(5), null, null, "synthetic veto draft"), "synthetic-first-admin");
        var pattern = draft.Patterns.Last(); var published = legacy.Transition(host.Tenant, pattern.Id, draft.Revision, "publish", "synthetic-second-admin", "synthetic second-person publication");
        try { Equal("Deny", legacy.Evaluate(host.Tenant, host.Actor, "organization.write", DateTimeOffset.UtcNow).Decision); await host.NoPermit(host.Token()); }
        finally { legacy.Transition(host.Tenant, pattern.Id, published.Revision, "retire", "synthetic-second-admin", "synthetic restore after veto test"); }
        using var restored = await host.Evaluate(host.Token()); Equal("Permit", (await Decision(restored)).Decision);
    });
    foreach (var field in new[] { "tenant", "subject", "revision", "window", "role", "reason" })
        await Test("corrupted durable role assignment " + field + " fails closed", async () =>
        {
            var original = host.Store.ReadRoleAssignment(host.Tenant, host.Actor)!;
            var corrupt = field switch
            {
                "tenant" => original with { TenantId = host.OtherTenant }, "subject" => original with { Subject = host.UnknownActor },
                "revision" => original with { Revision = 0 }, "window" => original with { ValidTo = original.ValidFrom },
                "role" => original with { RoleId = Guid.Empty }, _ => original with { Reason = "" }
            };
            host.PutAssignment(corrupt);
            try { await host.NoPermit(host.Token()); }
            finally { host.PutAssignment(original); }
        });
    foreach (var field in new[] { "tenant", "revision", "immutable-version", "reason" })
        await Test("corrupted durable published policy " + field + " fails closed", async () =>
        {
            var original = host.Store.ReadPolicy(host.Tenant)!;
            var corrupt = field switch
            {
                "tenant" => original with { ConsumerTenantId = host.OtherTenant }, "revision" => original with { Revision = 0 },
                "immutable-version" => original with { Binding = original.Binding with { PolicyVersion = original.Binding.PolicyVersion + 100 } },
                _ => original with { Reason = "" }
            };
            host.PutPolicy(corrupt);
            try { await host.NoPermit(host.Token()); }
            finally { host.PutPolicy(original); }
        });
    await Test("offboarded member role can still be explicitly revoked", () =>
    {
        var original = host.Store.ReadRoleAssignment(host.Tenant, host.Actor)!;
        host.Access.SetMembership(host.Tenant, host.Member.Id, false, "synthetic-test", "offboard before revocation");
        try
        {
            var revoked = host.Store.SaveRoleAssignment(host.Tenant, host.Actor, host.Role.Id, false, DateTimeOffset.MaxValue, DateTimeOffset.MinValue, original.Revision, "synthetic-test", "revoke after offboarding");
            Check(!revoked.Active, "offboarded assignment revoked"); Equal(original.ValidFrom, revoked.ValidFrom); Equal(original.ValidTo, revoked.ValidTo);
        }
        finally { host.Access.SetMembership(host.Tenant, host.Member.Id, true, "synthetic-test", "restore member"); host.RestoreGrant(); host.AssignRole(true); }
        return Task.CompletedTask;
    });

    await Test("current member grant revocation prevents Permit", async () =>
    {
        host.Access.SetGrant(new(host.Tenant, host.Actor, "organization.write", DateTimeOffset.UtcNow.AddMinutes(-1)), "synthetic-test", "revoke for test");
        try { await host.NoPermit(host.Token()); }
        finally { host.RestoreGrant(); }
    });
    await Test("current member offboarding prevents Permit", async () =>
    {
        host.Access.SetMembership(host.Tenant, host.Member.Id, false, "synthetic-test", "offboard for test");
        try { await host.NoPermit(host.Token()); }
        finally { host.Access.SetMembership(host.Tenant, host.Member.Id, true, "synthetic-test", "restore for test"); host.RestoreGrant(); }
    });
    await Test("inactive authoritative tenant prevents Permit", async () =>
    {
        var original = host.Access.GetTenants().Single(t => t.Id == host.Tenant);
        host.Database.Transaction(() => { host.Database.Put("tenant", original.Id.ToString(), original with { IsActive = false }); return true; });
        try { await host.NoPermit(host.Token()); }
        finally { host.Database.Transaction(() => { host.Database.Put("tenant", original.Id.ToString(), original); return true; }); }
    });
    await Test("inactive central consumer client prevents Permit", async () =>
    {
        var original = host.ConsumerClient;
        host.Database.Transaction(() => { host.Database.Put("client", original.Id.ToString(), original with { IsActive = false }); return true; });
        try { await host.NoPermit(host.Token()); }
        finally { host.Database.Transaction(() => { host.Database.Put("client", original.Id.ToString(), original); return true; }); }
    });
    await Test("revoked central consumer scope prevents Permit", async () =>
    {
        var original = host.ConsumerClient;
        host.Database.Transaction(() => { host.Database.Put("client", original.Id.ToString(), original with { AllowedScopes = [] }); return true; });
        try { await host.NoPermit(host.Token()); }
        finally { host.Database.Transaction(() => { host.Database.Put("client", original.Id.ToString(), original); return true; }); }
    });
    await Test("explicit product role assignment is independent of direct permission grant", async () =>
    {
        host.AssignRole(false);
        try { await host.NoPermit(host.Token()); }
        finally { host.AssignRole(true); }
    });
    await Test("product role must retain the actual organization.write permission", async () =>
    {
        var role = host.Role;
        host.Database.Transaction(() => { host.Database.Put("access-role", role.Id.ToString(), role with { Permissions = [] }); return true; });
        try { await host.NoPermit(host.Token()); }
        finally { host.Database.Transaction(() => { host.Database.Put("access-role", role.Id.ToString(), role); return true; }); }
    });
    await Test("server published policy mismatch cannot be resolved by projection or client claims", async () =>
    {
        var current = host.Store.Read(host.ContractId)!.Projection;
        var nextPolicy = current.Policy with { PolicyVersion = current.Policy.PolicyVersion + 1 };
        host.Store.PublishPolicy(host.Tenant, nextPolicy, Revision(host.Store.ReadPolicy(host.Tenant)!), "synthetic-publisher", "publish mismatch test");
        await host.NoPermit(host.Token());
        using var amend = await host.Ingest(host.Amend(p => p with { Policy = nextPolicy }), host.Token(producer: true));
        Equal(HttpStatusCode.Accepted, amend.StatusCode); Equal("applied", await Status(amend));
        using var restored = await host.Evaluate(host.Token()); Equal("Permit", (await Decision(restored)).Decision);
    });
    await Test("server policy retirement vetoes purchased eligibility", async () =>
    {
        using var retire = await host.Admin("policy/retire", new RetireContractPolicyCommand(Revision(host.Store.ReadPolicy(host.Tenant)!), "synthetic HTTP retirement"), adminCookie: true, csrf: true);
        Equal(HttpStatusCode.OK, retire.StatusCode);
        await host.NoPermit(host.Token());
        var current = host.Store.Read(host.ContractId)!.Projection;
        var nextPolicy = current.Policy with { PolicyVersion = current.Policy.PolicyVersion + 1 };
        host.Store.PublishPolicy(host.Tenant, nextPolicy, Revision(host.Store.ReadPolicy(host.Tenant)!), "synthetic-publisher", "restore published policy");
        using var amend = await host.Ingest(host.Amend(p => p with { Policy = nextPolicy }), host.Token(producer: true));
        Equal(HttpStatusCode.Accepted, amend.StatusCode);
    });
    await Test("pure Permit does not satisfy mandatory execution obligations", async () =>
    {
        using var evaluate = await host.Evaluate(host.Token()); Equal("Permit", (await Decision(evaluate)).Decision);
        var before = host.BusinessSnapshot(); using var execute = await host.UnitSave(host.Token());
        Equal(HttpStatusCode.Forbidden, execute.StatusCode); Equal(before, host.BusinessSnapshot());
    });

    foreach (var mode in new[] { "complete", "partial", "unknown", "unknown-superset", "duplicate", "null", "throws", "changed-role", "changed-projection", "expired" })
        await Test("native admission requires definitive fresh obligations: " + mode, async () =>
        {
            var assignment = host.Store.ReadRoleAssignment(host.Tenant, host.Actor)!;
            var projection = host.Store.Read(host.ContractId)!; var clock = new SyntheticClock();
            var before = host.BusinessSnapshot();
            var executor = new SyntheticExecutor(async (decision, cancellation) =>
            {
                await Task.Yield(); cancellation.ThrowIfCancellationRequested();
                var ids = decision.Obligations.Select(o => o.Id).ToArray();
                switch (mode)
                {
                    case "partial": return ids.Take(1).ToArray();
                    case "unknown": return ["unknown-obligation"];
                    case "unknown-superset": return ids.Concat(["unknown-obligation"]).ToArray();
                    case "duplicate": return ids.Concat([ids[0]]).ToArray();
                    case "null": return null;
                    case "throws": throw new InvalidOperationException("synthetic Gateway dependency unavailable");
                    case "changed-role": host.PutAssignment(assignment with { Active = false }); break;
                    case "changed-projection": host.PutProjection(projection with { Projection = projection.Projection with { Status = "Suspended" } }); break;
                    case "expired": clock.Advance(TimeSpan.FromSeconds(31)); break;
                }
                return ids;
            });
            var admission = host.Admission(executor, clock);
            try
            {
                Check(admission.IsConfigured, "synthetic executor configured only for local evidence");
                Equal(mode == "complete", await admission.AdmitAsync(host.Tenant, host.Actor, host.Command(), host.ConsumerPrincipal(), CancellationToken.None));
            }
            finally { host.PutAssignment(assignment); host.PutProjection(projection); }
            Equal(before, host.BusinessSnapshot());
        });
    await Test("native admission preserves cancellation before invoking executor", async () =>
    {
        var called = false; var executor = new SyntheticExecutor((_, _) => { called = true; return ValueTask.FromResult<IReadOnlyCollection<string>?>([]); });
        var before = host.BusinessSnapshot(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        try { await host.Admission(executor).AdmitAsync(host.Tenant, host.Actor, host.Command(), host.ConsumerPrincipal(), cancellation.Token); throw new InvalidOperationException("Expected cancellation"); }
        catch (OperationCanceledException) { }
        Check(!called, "cancelled operation did not invoke Gateway"); Equal(before, host.BusinessSnapshot());
    });

    await Test("same aggregate version with changed data remains a durable conflict", async () =>
    {
        var state = host.Store.Read(host.ContractId)!; var before = host.PersistenceSnapshot();
        var conflict = host.Event with { EventId = "http-same-version-conflict", AggregateVersion = state.Projection.ContractVersion, Projection = state.Projection with { Status = "Suspended" } };
        using var response = await host.Ingest(conflict, host.Token(producer: true));
        Equal(HttpStatusCode.Conflict, response.StatusCode); Equal(before, host.PersistenceSnapshot());
    });
    await Test("shared allocation rejects changed limits under an equal config revision", async () =>
    {
        var before = host.PersistenceSnapshot();
        using var response = await host.Ingest(host.Amend(p => p with { Limits = p.Limits with { Rate = p.Limits.Rate with { RequestsPerSecond = 7 } } }), host.Token(producer: true));
        Equal(HttpStatusCode.Conflict, response.StatusCode); Equal(before, host.PersistenceSnapshot());
    });
    await Test("two independent Fabric writers serialize projection versions and receipt commits", () =>
    {
        using var secondDb = new FabricDatabase(host.DatabasePath);
        var secondAccess = new PersistentAccessManagement(secondDb, new InMemoryAccessManagement(host.Registry));
        var secondStore = new ContractPolicyStore(secondDb, secondAccess, host.Registry, host.Module, TimeProvider.System);
        var previous = host.Store.Read(host.ContractId)!.Projection;
        var next = host.Event with { EventId = "race-next", AggregateVersion = previous.ContractVersion + 1, Projection = previous with { ContractVersion = previous.ContractVersion + 1 } };
        var later = next with { EventId = "race-later", AggregateVersion = previous.ContractVersion + 2, Projection = next.Projection with { ContractVersion = previous.ContractVersion + 2 } };
        var results = new System.Collections.Concurrent.ConcurrentBag<string>();
        Parallel.Invoke(
            () => results.Add(host.Store.Apply(Json(next), host.ProducerPrincipal(), host.Binding, () => true).Status),
            () => results.Add(secondStore.Apply(Json(later), host.ProducerPrincipal(), host.Binding, () => true).Status));
        Check(results.All(s => s is "applied" or "stale"), "serialized writers produce applied/stale outcomes");
        var final = host.Store.Read(host.ContractId)!;
        Equal(later.AggregateVersion, final.Projection.ContractVersion);
        Check(final.Receipts.Any(r => r.EventId == next.EventId) && final.Receipts.Any(r => r.EventId == later.EventId), "both durable receipts retained");
        EqualJson(ContractJson.ToNode(final), ContractJson.ToNode(secondStore.Read(host.ContractId)));
        return Task.CompletedTask;
    });
    await Test("outer Fabric rollback removes state and receipt changes together", () =>
    {
        var before = host.PersistenceSnapshot(); var e = host.Amend(p => p with { Status = "Suspended" });
        try
        {
            host.Database.Transaction<bool>(() =>
            {
                Equal("applied", host.Store.Apply(Json(e), host.ProducerPrincipal(), host.Binding, () => true).Status);
                throw new SyntheticRollback();
            });
        }
        catch (SyntheticRollback) { }
        Equal(before, host.PersistenceSnapshot()); return Task.CompletedTask;
    });
    await Test("producer revocation at final commit rolls back projection receipts and outbox", () =>
    {
        var before = host.PersistenceSnapshot(); var calls = 0;
        var result = host.Store.Apply(Json(host.Amend(p => p with { Status = "Suspended" })), host.ProducerPrincipal(), host.Binding, () => ++calls < 3);
        Equal("rejected", result.Status); Check(result.Errors.Contains("producer-not-authorized", StringComparer.Ordinal), "final authorization failure retained");
        Equal(3, calls); Equal(before, host.PersistenceSnapshot()); return Task.CompletedTask;
    });
    await Test("newer suspension survives stale activation and receipt replay", async () =>
    {
        var previous = host.Store.Read(host.ContractId)!.Projection;
        var suspended = host.Amend(p => p with { Status = "Suspended", Limits = p.Limits with { ConfigRevision = p.Limits.ConfigRevision + 1 } });
        using var apply = await host.Ingest(suspended, host.Token(producer: true)); Equal(HttpStatusCode.Accepted, apply.StatusCode);
        var older = host.Event with { EventId = "older-active-snapshot", AggregateVersion = previous.ContractVersion - 1, Projection = previous with { ContractVersion = previous.ContractVersion - 1 } };
        using var stale = await host.Ingest(older, host.Token(producer: true)); Equal(HttpStatusCode.Accepted, stale.StatusCode); Equal("stale", await Status(stale));
        Equal("Suspended", host.Store.Read(host.ContractId)!.Projection.Status); await host.NoPermit(host.Token());
        using var replay = await host.Ingest(older, host.Token(producer: true)); Equal("duplicate", await Status(replay));
    });
    await Test("reopening actual SQLite restores projection and replay fingerprints", () =>
    {
        var before = host.Store.Read(host.ContractId)!;
        using var restartedDb = new FabricDatabase(host.DatabasePath);
        var restartedAccess = new PersistentAccessManagement(restartedDb, new InMemoryAccessManagement(host.Registry));
        var restarted = new ContractPolicyStore(restartedDb, restartedAccess, host.Registry, host.Module, TimeProvider.System);
        EqualJson(ContractJson.ToNode(before), ContractJson.ToNode(restarted.Read(host.ContractId)));
        Equal("duplicate", restarted.Apply(Json(host.Event), host.ProducerPrincipal(), host.Binding, () => true).Status);
        EqualJson(ContractJson.ToNode(before), ContractJson.ToNode(restarted.Read(host.ContractId)));
        return Task.CompletedTask;
    });
}

await GatewayConnectionTests.Run(Test);
Console.WriteLine($"PCC contract policy integration: {passed} passed, {failed} failed.");
return failed == 0 ? 0 : 1;

static string Json<T>(T value) => JsonSerializer.Serialize(value, ContractJson.Options);
static long Revision<T>(T value) => ContractJson.ToNode(value)!["revision"]!.GetValue<long>();
static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static void Equal<T>(T expected, T actual) { if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"Expected {expected}; actual {actual}"); }
static void EqualJson(JsonNode? expected, JsonNode? actual) { if (!JsonNode.DeepEquals(expected, actual)) throw new InvalidOperationException($"JSON differs: expected {expected}; actual {actual}"); }
static async Task<string> Status(HttpResponseMessage response) => (await response.Content.ReadFromJsonAsync<JsonNode>())!["status"]!.GetValue<string>();
static async Task<PolicyDecision> Decision(HttpResponseMessage response) => (await response.Content.ReadAsStringAsync()).DeserializeDecision();

sealed class SyntheticRollback : Exception;
sealed class SlowProjectionContent : HttpContent
{
    private readonly byte[] payload;
    private readonly DateTimeOffset releaseAt;
    public DateTimeOffset FirstWriteAt { get; private set; }
    public DateTimeOffset LastWriteAt { get; private set; }
    public SlowProjectionContent(string json, DateTimeOffset releaseAt)
    {
        payload = Encoding.UTF8.GetBytes(json); this.releaseAt = releaseAt;
        Headers.ContentType = new MediaTypeHeaderValue("application/json"); Headers.ContentLength = payload.Length;
    }
    protected override bool TryComputeLength(out long length) { length = payload.Length; return true; }
    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        var split = payload.Length / 2; FirstWriteAt = DateTimeOffset.UtcNow;
        await stream.WriteAsync(payload.AsMemory(0, split)); await stream.FlushAsync();
        var delay = releaseAt - DateTimeOffset.UtcNow; if (delay > TimeSpan.Zero) await Task.Delay(delay);
        LastWriteAt = DateTimeOffset.UtcNow;
        await stream.WriteAsync(payload.AsMemory(split)); await stream.FlushAsync();
    }
}
sealed class SyntheticClock : TimeProvider
{
    private TimeSpan offset;
    public override DateTimeOffset GetUtcNow() => DateTimeOffset.UtcNow + offset;
    public void Advance(TimeSpan amount) => offset += amount;
}
sealed class SyntheticExecutor(Func<PolicyDecision, CancellationToken, ValueTask<IReadOnlyCollection<string>?>> fulfill) : IContractPolicyObligationExecutor
{
    public bool IsConfigured => true;
    public ValueTask<IReadOnlyCollection<string>?> FulfillAsync(Guid tenant, string actor, OrgCommand command,
        PolicyDecision decision, CancellationToken cancellationToken) => fulfill(decision, cancellationToken);
}
static class DecisionReader
{
    public static PolicyDecision DeserializeDecision(this string json) => ContractJson.Parse(json)!.Deserialize<PolicyDecision>(ContractJson.Options)!;
}

sealed class PccFixture : IAsyncDisposable
{
    public const string Issuer = "https://synthetic-pcc-idp.invalid/realms/fanasa";
    public const string ConsumerAudience = "fanasa-organization-fabric-api";
    public const string ProducerAudience = "fanasa-access-management-web";
    public const string ConsumerClientKey = "synthetic-org-client";
    public const string ProducerClientKey = "synthetic-contracts-client";
    public const string ProducerScope = "contracts.projection.write";
    private readonly string directory;
    private readonly WebApplication app;
    private readonly HttpClient client;
    private readonly RSA rsa;
    private readonly RsaSecurityKey key;
    public string DatabasePath { get; }
    public FabricDatabase Database { get; }
    public PersistentAccessManagement Access { get; }
    public PlatformRegistry Registry { get; }
    public ContractPolicyStore Store { get; }
    public ContractPolicyModule Module { get; }
    public AccessPolicyStore LegacyPolicies => app.Services.GetRequiredService<AccessPolicyStore>();
    public Guid Tenant { get; }
    public Guid OtherTenant { get; }
    public string Actor { get; } = Guid.NewGuid().ToString("D");
    public string UnknownActor { get; } = Guid.NewGuid().ToString("D");
    public TenantUser Member { get; }
    public AccessRole Role { get; }
    public ProductClient ConsumerClient { get; }
    public ProductClient ProducerClient { get; }
    public ContractProjectionEvent Event { get; }
    public string ContractId => Event.Projection.ContractId;
    public ContractProducerBinding Binding { get; } = new(Issuer, ProducerAudience, ProducerClientKey, ProducerScope);

    private PccFixture()
    {
        directory = Path.Combine(Path.GetTempPath(), "synthetic-pcc-contract-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); DatabasePath = Path.Combine(directory, "fabric", "fabric.db");
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Logging.ClearProviders(); builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Platform:RegistryPath"] = Path.Combine(directory, "registry.db"), ["Fabric:DataDirectory"] = Path.Combine(directory, "fabric"),
            ["Organization:DataDirectory"] = Path.Combine(directory, "legacy-org"), ["Accounting:DataDirectory"] = Path.Combine(directory, "legacy-accounting"),
            ["Authentication:Authority"] = Issuer,
            ["Organization:Bearer:Enabled"] = "true", ["Organization:Bearer:Authority"] = Issuer,
            ["Organization:Bearer:Audience"] = ConsumerAudience, ["Organization:Bearer:ClientId"] = ConsumerClientKey, ["Organization:Bearer:Scope"] = "organization.unit.save",
            ["ContractPolicy:Organization:Enabled"] = "true", ["ContractPolicy:Organization:Environment"] = "production", ["ContractPolicy:Organization:Zone"] = "ir-tehran",
            ["ContractPolicy:Organization:MaximumSnapshotAgeSeconds"] = "5",
            ["ContractPolicy:Producer:Enabled"] = "true", ["ContractPolicy:Producer:Authority"] = Issuer,
            ["ContractPolicy:Producer:Audience"] = ProducerAudience, ["ContractPolicy:Producer:ClientId"] = ProducerClientKey,
            ["ContractPolicy:Producer:Scope"] = ProducerScope, ["ContractPolicy:Producer:ProductKey"] = "contracts"
        });
        Registry = new PlatformRegistry(builder.Configuration); builder.Services.AddSingleton(Registry);
        builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(directory, "synthetic-keys")));
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie();
        builder.Services.AddAuthorization(options => options.AddPolicy("PlatformAdmin", policy =>
            policy.RequireAuthenticatedUser().RequireClaim("permission", "platform.admin")));
        builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
        // Production registers Razor Pages before controllers, which supplies
        // the MVC ValidateAntiForgeryToken filter used by administrative writes.
        builder.Services.AddRazorPages();
        FabricServices.AddIdentityOrganizationFabric(builder.Services, builder.Configuration, builder.Environment);
        builder.Services.AddOrganizationBearerSecurity(builder.Configuration);
        builder.Services.AddControllers().AddApplicationPart(typeof(OrganizationOperationApi).Assembly);
        rsa = RSA.Create(2048); key = new RsaSecurityKey(rsa) { KeyId = "synthetic-pcc-key" };
        foreach (var scheme in new[] { OrganizationBearerSecurity.Scheme, "ContractProducerBearer" })
            builder.Services.PostConfigure<JwtBearerOptions>(scheme, options =>
            {
                options.Authority = null; options.MetadataAddress = "";
                var metadata = new OpenIdConnectConfiguration { Issuer = Issuer }; metadata.SigningKeys.Add(key);
                options.Configuration = metadata; options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata);
            });
        app = builder.Build();
        Database = app.Services.GetRequiredService<FabricDatabase>(); Access = app.Services.GetRequiredService<PersistentAccessManagement>();
        Store = app.Services.GetRequiredService<ContractPolicyStore>(); Module = app.Services.GetRequiredService<ContractPolicyModule>();
        var owner = Access.GetTenants().Single().Id;
        Tenant = Access.CreateTenant("synthetic-consumer", "Synthetic Consumer").Id; OtherTenant = Access.CreateTenant("synthetic-other", "Synthetic Other").Id;
        Registry.SetTenantSource(Access.GetTenants); Registry.SetMembershipSource(Access.GetUsers);
        var orgProduct = Registry.Register(new(Guid.NewGuid(), "organization-fabric", "Synthetic Organization", ConsumerAudience, "fanasa.ayin", null, null, owner, "synthetic-owner"), "synthetic-test");
        var contractsProduct = Registry.Register(new(Guid.NewGuid(), "contracts", "Synthetic Contract Producer", ProducerAudience, "fanasa.ayin", null, null, owner, "synthetic-owner"), "synthetic-test");
        ConsumerClient = Access.RegisterClient(orgProduct.Id, new(ConsumerClientKey, "Synthetic consumer", "confidential", [], ["organization.unit.save"]));
        ProducerClient = Access.RegisterClient(contractsProduct.Id, new(ProducerClientKey, "Synthetic producer", "confidential", [], [ProducerScope]));
        Member = Access.AddUser(new(Tenant, Actor, "Synthetic member")); RestoreGrant();
        Role = Access.CreateRole(new(Tenant, "organization-fabric", "ApiInvoker", "Synthetic API Invoker", ["organization.write"]));
        var template = ContractJson.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "integration-projection-event.v2.json")))!.Deserialize<ContractProjectionEvent>(ContractJson.Options)!;
        var now = DateTimeOffset.UtcNow;
        var scope = template.Projection.Limits.Scope with { ConsumerTenantId = Tenant.ToString("D"), Environments = ["production"] };
        Event = template with
        {
            EventId = "synthetic-live-contract-7", OccurredAt = Utc(now),
            Projection = template.Projection with
            {
                OwnerTenantId = owner.ToString("D"), ConsumerTenantId = Tenant.ToString("D"),
                ValidFrom = Utc(now.AddDays(-1)), ValidTo = Utc(now.AddDays(1)), EffectiveAt = Utc(now.AddMinutes(-1)), ZoneConstraints = ["ir-tehran"],
                Limits = template.Projection.Limits with { Scope = scope, CanonicalScope = ContractProjectionValidator.CanonicalScope(scope) }
            }
        };
        Store.PublishPolicy(Tenant, Event.Projection.Policy, 0, "synthetic-publisher", "independent server policy publication"); AssignRole(true);
        app.UseRouting(); app.UseAuthentication(); app.UseAuthorization(); app.MapControllers().RequireAuthorization();
        app.MapGet("/synthetic-cookie", async (Microsoft.AspNetCore.Http.HttpContext http) =>
        {
            var claims = new List<Claim> { new("sub", Actor) };
            if (http.Request.Query["admin"] == "true") claims.Add(new("permission", "platform.admin"));
            http.User = new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
            await http.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, http.User);
            var tokens = http.RequestServices.GetRequiredService<IAntiforgery>().GetAndStoreTokens(http);
            return Results.Ok(new { csrf = tokens.RequestToken });
        }).AllowAnonymous();
        app.MapGet("/synthetic-signout", async (HttpContext http) =>
        {
            await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme); return Results.Ok();
        }).AllowAnonymous();
        client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = true });
    }

    public static async Task<PccFixture> Start()
    {
        var fixture = new PccFixture(); await fixture.app.StartAsync();
        fixture.client.BaseAddress = new Uri(fixture.app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
        return fixture;
    }
    public void RestoreGrant() => Access.SetGrant(new(Tenant, Actor, "organization.write", null), "synthetic-test", "restore direct member permission");
    public void AssignRole(bool active)
    {
        var previous = Store.ReadRoleAssignment(Tenant, Actor);
        var revision = previous is null ? 0 : ContractJson.ToNode(previous)!["revision"]!.GetValue<long>();
        var now = DateTimeOffset.UtcNow;
        Store.SaveRoleAssignment(Tenant, Actor, Role.Id, active, now.AddDays(-1), now.AddDays(1), revision, "synthetic-test", "explicit product role assignment");
    }
    public ContractProjectionEvent Amend(Func<ContractAccessProjection, ContractAccessProjection> change)
    {
        var current = Store.Read(ContractId)!.Projection; var version = current.ContractVersion + 1;
        return Event with { EventId = "synthetic-amendment-" + Guid.NewGuid().ToString("N"), AggregateVersion = version, OccurredAt = Utc(DateTimeOffset.UtcNow), Projection = change(current with { ContractVersion = version }) };
    }
    public ClaimsPrincipal ProducerPrincipal() => new(new ClaimsIdentity([new("iss", Issuer), new("aud", ProducerAudience), new("azp", ProducerClientKey), new("scope", ProducerScope)], "synthetic-trusted-atomic-test"));
    public ClaimsPrincipal ConsumerPrincipal() => new(new ClaimsIdentity(new JwtSecurityTokenHandler().ReadJwtToken(Token()).Claims, OrganizationBearerSecurity.Scheme));
    public ContractPolicyOrganizationAdmission Admission(IContractPolicyObligationExecutor executor, TimeProvider? clock = null) =>
        new(app.Services.GetRequiredService<OrganizationContractPolicyDecisionService>(), executor, Module, clock ?? TimeProvider.System);
    public void PutAssignment(ProductRoleAssignment assignment) => Database.Transaction(() =>
    {
        Database.Put("contract-policy.role-assignment", $"{Tenant:D}:{Actor}", assignment); return true;
    });
    public void PutProjection(ProjectionState state) => Database.Transaction(() =>
    {
        Database.Put("contract-policy.projection", ContractId, state); return true;
    });
    public void PutPolicy(PublishedContractPolicy policy) => Database.Transaction(() =>
    {
        Database.Put("contract-policy.policy", Tenant.ToString("D"), policy); return true;
    });
    public string Token(bool producer = false, string? issuer = null, string? audience = null, string? client = null, string? scope = null,
        string? actor = null, bool badKey = false, Claim[]? extra = null, DateTime? expiresAt = null)
    {
        var claims = new List<Claim> { new("sub", actor ?? Actor), new("azp", client ?? (producer ? ProducerClientKey : ConsumerClientKey)), new("scope", scope ?? (producer ? ProducerScope : "organization.unit.save")) };
        if (extra is not null) claims.AddRange(extra);
        using var other = badKey ? RSA.Create(2048) : null;
        var signingKey = badKey ? new RsaSecurityKey(other!) { KeyId = key.KeyId } : key;
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(issuer ?? Issuer, audience ?? (producer ? ProducerAudience : ConsumerAudience), claims,
            DateTime.UtcNow.AddMinutes(-10), expiresAt ?? DateTime.UtcNow.AddMinutes(5), new SigningCredentials(signingKey, SecurityAlgorithms.RsaSha256)));
    }
    public string RawToken(Func<string, string> change, bool producer = true)
    {
        var parts = Token(producer: producer).Split('.');
        var payload = Base64UrlEncoder.Encode(change(Base64UrlEncoder.Decode(parts[1])));
        var unsigned = parts[0] + "." + payload;
        var signature = rsa.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return unsigned + "." + Base64UrlEncoder.Encode(signature);
    }
    public string MalformedToken(string mode, bool producer = true) => RawToken(payload =>
    {
        if (mode is "fractional-exp" or "exponent-exp")
            return System.Text.RegularExpressions.Regex.Replace(payload, "\"exp\":([0-9]+)", match => match.Value + (mode == "fractional-exp" ? ".0" : "e0"));
        var node = JsonNode.Parse(payload)!;
        switch (mode)
        {
            case "missing-exp": node.AsObject().Remove("exp"); break;
            case "quoted-exp": node["exp"] = node["exp"]!.ToJsonString(); break;
            case "quoted-nbf": node["nbf"] = node["nbf"]!.ToJsonString(); break;
            case "negative-nbf": node["nbf"] = -1; break;
            case "future-nbf": node["nbf"] = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds(); break;
            case "array-scope": node["scope"] = new JsonArray(ProducerScope); break;
            case "tab-scope": node["scope"] = ProducerScope + "\topenid"; break;
            case "mixed-audience": node["aud"] = new JsonArray(ProducerAudience, 7); break;
            default: throw new ArgumentOutOfRangeException(nameof(mode));
        }
        return node.ToJsonString();
    }, producer);
    public async Task<HttpResponseMessage> Admin(string route, object? command = null, bool? adminCookie = null,
        bool csrf = false, bool forgedCsrf = false, string? token = null)
    {
        string? csrfToken = null;
        if (adminCookie is { } admin)
        {
            using var issued = await client.GetAsync("/synthetic-cookie?admin=" + (admin ? "true" : "false"));
            issued.EnsureSuccessStatusCode(); csrfToken = (await issued.Content.ReadFromJsonAsync<JsonNode>())!["csrf"]!.GetValue<string>();
        }
        else { using var signedOut = await client.GetAsync("/synthetic-signout"); signedOut.EnsureSuccessStatusCode(); }
        using var request = new HttpRequestMessage(command is null ? HttpMethod.Get : HttpMethod.Post, $"/api/contract-policy/administration/tenants/{Tenant}/" + route);
        if (command is not null) request.Content = new StringContent(JsonSerializer.Serialize(command, ContractJson.Options), Encoding.UTF8, "application/json");
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (csrf || forgedCsrf) request.Headers.Add("X-CSRF-TOKEN", forgedCsrf ? "synthetic-forged-csrf" : csrfToken!);
        return await client.SendAsync(request);
    }
    public Task<HttpResponseMessage> Ingest(ContractProjectionEvent value, string? token, bool cookie = false) => IngestJson(JsonSerializer.Serialize(value, ContractJson.Options), token, cookie);
    public Task<HttpResponseMessage> IngestJson(string json, string? token, bool cookie = false) => Send("/api/contract-policy/projection-events", token, json, cookie);
    public async Task<HttpResponseMessage> IngestContent(HttpContent content, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/contract-policy/projection-events") { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token); return await client.SendAsync(request);
    }
    public Task<HttpResponseMessage> Evaluate(string? token, Guid? tenant = null, string? body = null, bool cookie = false, bool forgedHeaders = false) =>
        Send($"/api/contract-policy/tenants/{tenant ?? Tenant}/organization.unit.save/evaluate", token, body ?? "", cookie, forgedHeaders);
    public Task<HttpResponseMessage> UnitSave(string? token)
    {
        return Send($"/api/organization-operations/tenants/{Tenant}/unit.save", token, JsonSerializer.Serialize(Command()));
    }
    public OrgCommand Command() => new(0, "unit.save", null, "Synthetic team", "synthetic-team", null, "team", null, 1, null, null, null, null, "synthetic obligation refusal", RequestId: Guid.NewGuid().ToString("D"));
    public async Task NoPermit(string? token, Guid? tenant = null, bool cookie = false)
    {
        var before = BusinessSnapshot(); using var response = await Evaluate(token, tenant, cookie: cookie);
        if (response.StatusCode == HttpStatusCode.InternalServerError) throw new InvalidOperationException("PDP failed with HTTP 500");
        if (response.IsSuccessStatusCode && (await response.Content.ReadAsStringAsync()).DeserializeDecision().Decision == "Permit") throw new InvalidOperationException("Unexpected Permit");
        if (before != BusinessSnapshot()) throw new InvalidOperationException("Denied PDP request mutated business records or outbox");
    }
    public string PersistenceSnapshot() => JsonSerializer.Serialize(new { State = Store.Read(ContractId), Allocation = Store.ReadAllocation(Event.Projection.Limits.AllocationId), Outbox = Database.Outbox(take: 100) });
    public string AdminSnapshot() => JsonSerializer.Serialize(new { Policy = Store.ReadPolicy(Tenant), Role = Store.ReadRoleAssignment(Tenant, Actor), Grants = Access.GetGrants(Actor), Outbox = Database.Outbox(take: 100) });
    public string BusinessSnapshot() => JsonSerializer.Serialize(new
    {
        Organization = Database.All<JsonElement>("organization"), History = Database.All<JsonElement>("organization.history"), Receipts = Database.All<JsonElement>("organization.receipt"),
        Usage = Database.All<JsonElement>("usage"), Measured = Database.All<JsonElement>("measured.operation"), Ledger = Database.All<JsonElement>("accounting"), Outbox = Database.Outbox(take: 100)
    });
    private async Task<HttpResponseMessage> Send(string path, string? token, string body, bool cookie = false, bool forgedHeaders = false)
    {
        if (cookie) { using var issued = await client.GetAsync("/synthetic-cookie"); issued.EnsureSuccessStatusCode(); }
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        if (token is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (forgedHeaders) { request.Headers.Add("X-Tenant-ID", OtherTenant.ToString("D")); request.Headers.Add("X-Policy-Decision", "Permit"); request.Headers.Add("X-Permission", "platform.admin"); }
        return await client.SendAsync(request);
    }
    private static string Utc(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    public async ValueTask DisposeAsync()
    {
        client.Dispose(); await app.StopAsync(); await app.DisposeAsync(); rsa.Dispose(); SqliteConnection.ClearAllPools();
        var full = Path.GetFullPath(directory); var temp = Path.GetFullPath(Path.GetTempPath());
        if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("synthetic-pcc-contract-policy-", StringComparison.Ordinal)) throw new InvalidOperationException("Invalid test cleanup directory");
        Directory.Delete(full, true);
    }
}
