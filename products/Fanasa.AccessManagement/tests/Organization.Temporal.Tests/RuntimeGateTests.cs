using System.Reflection;
using System.Security.Claims;
using Fanasa.AccessManagement.Web.Api;
using Fanasa.AccessManagement.Web.Organization;
using Fanasa.AccessManagement.Web.Security;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;

internal static class RuntimeGateTests
{
    public static void Run(Action<bool, string> check)
    {
        void Reject<T>(Action action, string label) where T : Exception
        {
            try { action(); }
            catch (T) { check(true, label); return; }
            throw new InvalidOperationException("Expected " + typeof(T).Name + ": " + label);
        }
        using var fixture = new Fixture();
        var tenant = fixture.Access.CreateTenant("runtime-gate", "Runtime gate test").Id;
        var other = fixture.Access.CreateTenant("outside-runtime-gate", "Outside configured scope").Id;
        const string subject = "runtime-gate-subject";
        fixture.Access.AddUser(new(tenant, subject, "Runtime gate member"));
        fixture.Access.SetGrant(new(tenant, subject, "organization.read", null), "authority", "Test direct read grant");
        fixture.Access.SetGrant(new(tenant, subject, "organization.write", null), "authority", "Test direct write grant");
        var calendar = fixture.Calendars.Save(tenant, new(0, null, "office", "Office", "work", "Asia/Tehran",
            new(2026, 1, 1), new(2026, 12, 31), [new(6, 480, 960)], [], true, "Synthetic source", "Test definition"), "calendar-author").Calendars.Single().Id;
        foreach (var permission in new[] { "organization.read", "organization.write" })
        {
            var draft = fixture.Policies.Draft(tenant, new(fixture.Policies.Read(tenant).Revision, permission, "time-guard", permission,
                "Deny", null, null, null, null, null, null, "Runtime restriction", calendar, "working-time", true), "policy-author");
            fixture.Policies.Transition(tenant, draft.Patterns.Last().Id, draft.Revision, "publish", "independent-reviewer", "Reviewed restriction");
        }
        var inside = DateTimeOffset.Parse("2026-10-17T10:00:00+03:30", System.Globalization.CultureInfo.InvariantCulture);
        var outside = DateTimeOffset.Parse("2026-10-17T17:00:00+03:30", System.Globalization.CultureInfo.InvariantCulture);
        var clock = new ScriptedGateClock(outside);
        var gate = new TemporalAccessGate(new() { Enabled = true, TenantIds = [tenant] }, fixture.Policies, clock);
        var disabled = new TemporalAccessGate(new(), fixture.Policies, clock);
        var beforeDefault = fixture.Fingerprint();
        check(disabled.Allows(tenant, subject, "organization.write") && clock.Calls == 0, "disabled gate does not evaluate or change existing access");
        check(gate.Allows(other, "unrelated-subject", "organization.write") && clock.Calls == 0, "tenant outside exact allowlist remains untouched");
        check(fixture.Fingerprint() == beforeDefault, "disabled and unlisted gates perform no database writes");
        Reject<InvalidOperationException>(() => new TemporalAccessOptions { Enabled = true }.Validate(), "enabled gate rejects empty allowlist");
        Reject<InvalidOperationException>(() => new TemporalAccessOptions { Enabled = true, TenantIds = [tenant, tenant] }.Validate(), "gate rejects duplicate tenant identifiers");
        Reject<InvalidOperationException>(() => new TemporalAccessOptions { Enabled = true, TenantIds = [Guid.Empty] }.Validate(), "gate rejects empty tenant identifier");
        Reject<InvalidOperationException>(() => new TemporalAccessOptions { TenantIds = null! }.Validate(), "gate rejects null tenant allowlist");
        check(!gate.Allows(tenant, subject, "billing.read"), "enabled gate cannot imply support for another permission");
        check(!gate.Allows(tenant, subject, "organization.write"), "server clock outside window vetoes an existing write grant");
        clock.Reset(inside);
        check(gate.Allows(tenant, subject, "organization.write"), "server clock inside window accepts existing write grant");
        check(!gate.Allows(tenant, "no-membership-or-grant", "organization.write"), "open calendar cannot give an unknown subject access");

        var organizationDirectory = Path.Combine(Path.GetDirectoryName(fixture.DatabasePath)!, "organization");
        var organization = new OrganizationStore(organizationDirectory, fixture.Access, database: fixture.Database, temporalAccess: gate);
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new("sub", subject), new("client_time", inside.ToString("O"))], "test"));
        OrgCommand Command(string code) => new(organization.Read(tenant).Revision, "unit.save", null, code, code,
            null, "department", null, 1, null, null, inside, null, "Runtime transaction test", RequestId: Guid.NewGuid().ToString());
        clock.Reset(outside);
        var beforeBlocked = fixture.Fingerprint();
        Reject<UnauthorizedAccessException>(() => organization.Execute(tenant, Command("outside"), subject, principal), "store ignores caller time and rejects write using current server time");
        check(fixture.Fingerprint() == beforeBlocked && organization.Read(tenant).Revision == 0, "outside-window write leaves all documents and outbox unchanged");

        clock.Reset(inside, outside);
        var beforeBoundary = fixture.Fingerprint();
        Reject<UnauthorizedAccessException>(() => organization.Execute(tenant, Command("cross-boundary"), subject, principal), "window closure between mutation checks rejects final commit");
        check(clock.Calls == 2, "mutation takes a fresh server-clock sample before and after domain work");
        check(fixture.Fingerprint() == beforeBoundary && organization.Read(tenant).Revision == 0,
            "post-mutation denial rolls back chart history receipt and all decision/domain audit records");
        clock.Reset(inside);
        var allowed = organization.Execute(tenant, Command("allowed"), subject, principal);
        check(allowed.Revision == 1 && organization.Read(tenant).Units.Single().Code == "allowed", "in-window authenticated mutation commits normally");
        var beforeActorMismatch = fixture.Fingerprint();
        Reject<UnauthorizedAccessException>(() => organization.Execute(tenant, Command("actor-mismatch"), "different-subject", principal), "commit cannot borrow another principal's time permission");
        check(fixture.Fingerprint() == beforeActorMismatch, "actor mismatch writes no domain or decision state");
        clock.Reset(outside);
        var unrelatedCommand = new OrgCommand(0, "unit.save", null, "unlisted", "unlisted", null, "department", null, 1,
            null, null, null, null, "Existing unlisted store behavior");
        check(organization.Execute(other, unrelatedCommand, "existing-actor").Revision == 1, "store restriction does not change unlisted tenant behavior");

        var filter = new TemporalAccessFilter(gate);
        (ActionExecutingContext Context, bool Ran) Filter(Guid target, ClaimsPrincipal user, bool withMetadata = true)
        {
            var descriptor = new ActionDescriptor
            { EndpointMetadata = withMetadata ? [new TemporalPermissionAttribute("organization.read")] : [] };
            var http = new DefaultHttpContext { User = user };
            var action = new ActionContext(http, new RouteData(), descriptor);
            var controller = new object();
            var context = new ActionExecutingContext(action, [], new Dictionary<string, object?> { ["tenantId"] = target }, controller);
            var ran = false;
            filter.OnActionExecutionAsync(context, () =>
            {
                ran = true;
                return Task.FromResult(new ActionExecutedContext(action, [], controller));
            }).GetAwaiter().GetResult();
            return (context, ran);
        }
        var blocked = Filter(tenant, principal);
        check(!blocked.Ran && blocked.Context.Result is ObjectResult { StatusCode: 403 }, "temporal filter blocks action execution outside window");
        check(blocked.Context.HttpContext.Response.Headers.CacheControl == "no-store", "denied time-sensitive response is not cacheable");
        clock.Reset(inside);
        var passed = Filter(tenant, principal);
        check(passed.Ran && passed.Context.Result is null, "filter continues original action when time restriction passes");
        var anonymous = Filter(tenant, new ClaimsPrincipal());
        check(!anonymous.Ran && anonymous.Context.Result is ObjectResult { StatusCode: 403 }, "temporal filter denies anonymous invocation");
        clock.Reset(outside);
        check(Filter(other, principal).Ran, "filter preserves unrelated tenant request flow");
        check(Filter(tenant, principal, false).Ran, "filter does not expand beyond explicitly marked business endpoints");

        foreach (var (type, method, permission) in new (Type, string, string)[]
        {
            (typeof(OrganizationApi), nameof(OrganizationApi.Read), "organization.read"),
            (typeof(OrganizationApi), nameof(OrganizationApi.Compare), "organization.read"),
            (typeof(OrganizationApi), nameof(OrganizationApi.Execute), "organization.write"),
            (typeof(OrganizationHrApi), nameof(OrganizationHrApi.Chart), "organization.read"),
            (typeof(OrganizationHrApi), nameof(OrganizationHrApi.History), "organization.read"),
            (typeof(OrganizationHrApi), nameof(OrganizationHrApi.Execute), "organization.write"),
            (typeof(OrganizationOperationApi), nameof(OrganizationOperationApi.Execute), "organization.write")
        })
        {
            var attribute = type.GetMethod(method)!.GetCustomAttribute<TemporalPermissionAttribute>();
            check(attribute?.Permission == permission, type.Name + "." + method + " has the required temporal endpoint metadata");
        }
    }

    private sealed class ScriptedGateClock(DateTimeOffset initial) : TimeProvider
    {
        private DateTimeOffset[] samples = [initial];
        public int Calls { get; private set; }
        public void Reset(params DateTimeOffset[] values)
        {
            if (values.Length == 0) throw new ArgumentException("At least one clock sample is required.");
            samples = values; Calls = 0;
        }
        public override DateTimeOffset GetUtcNow() => samples[Math.Min(Calls++, samples.Length - 1)];
    }
}
