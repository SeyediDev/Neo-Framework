using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Governance;
using Fanasa.AccessManagement.Web.Organization;
using Fanasa.AccessManagement.Web.Persistence;

static class PipEducationalTests
{
    public static void Run(string directory, Action<bool, string> check)
    {
        using var db = new FabricDatabase(Path.Combine(directory, "pip-educational", "fabric.db"));
        var access = new PersistentAccessManagement(db, new InMemoryAccessManagement());
        var now = new DateTimeOffset(2026, 10, 23, 8, 0, 0, TimeSpan.Zero);
        var clock = new ExampleClock(now);
        var calendar = new OrganizationCalendarStore(db, access);
        var pip = new PipStore(db, access);
        var policy = new AccessPolicyStore(db, access, calendar, pip);
        var template = new TemplateOrganizationStore(db, access, calendar, policy, clock);
        var examples = new PipEducationalExample(db, access, template, policy, pip, clock);
        var original = template.Ensure("teacher", "create isolated educational fixture");
        var tenant = original.Tenant.Id;
        var originalPolicies = policy.Read(tenant);
        var originalScenarioResults = template.EvaluateScenarios().Select(x => x.Actual.Decision).ToArray();
        var other = access.CreateTenant("not-educational", "ordinary tenant").Id;
        bool Rejected(Action action) { try { action(); return false; } catch (InvalidOperationException) { return true; } }
        check(Rejected(() => examples.Ensure(other, "teacher", "must not seed ordinary tenant"))
            && pip.ListSources(other).Length == 0 && access.GetUsers(other).Count == 0,
            "PIP example cannot seed a tenant without the educational marker");
        var first = examples.Ensure(tenant, "teacher", "demonstrate fresh and missing PIP data");
        check(first.Source.Educational && first.Source.Enabled && first.Source.Endpoint == PipEducationalExample.Endpoint
            && first.Source.SecretReference is null && first.Source.PollIntervalSeconds == 0,
            "PIP example uses a marked synthetic source with no external credential or polling");
        check(first.Scenarios.Length == 3 && first.Scenarios.All(x => x.Passed)
            && first.Scenarios.Select(x => x.Actual).SequenceEqual(new[] { "Permit", "Indeterminate", "Indeterminate" }),
            "PIP educational scenarios evaluate fresh, expired and missing evidence through the actual PDP");
        check(first.Simulation && !first.Enforced && first.FreshUntil == now.AddMinutes(5),
            "PIP educational evidence has bounded freshness and explicitly simulated results");
        var addedPolicy = policy.Read(tenant).Patterns.Single(x => x.Id == first.PolicyId);
        check(addedPolicy.Status == "draft" && !addedPolicy.Enforce && addedPolicy.Permission == "tenancy.read"
            && policy.Read(tenant).Patterns.Take(originalPolicies.Patterns.Length).SequenceEqual(originalPolicies.Patterns),
            "PIP example adds an isolated draft without modifying or publishing existing policies");
        check(access.GetUsers(tenant).Count == original.Members.Length + 2
            && access.GetGrants(PipEducationalExample.Subject).Length == 0
            && access.GetGrants(PipEducationalExample.MissingSubject).Length == 0,
            "PIP teaching identities are local synthetic members without direct grants");
        check(template.EvaluateScenarios().Select(x => x.Actual.Decision).SequenceEqual(originalScenarioResults),
            "PIP example preserves all existing template organization scenario decisions");
        var revision = policy.Read(tenant).Revision;
        var second = examples.Ensure(tenant, "teacher", "repeat same educational request");
        check(second.Source.Id == first.Source.Id && second.PolicyId == first.PolicyId
            && policy.Read(tenant).Revision == revision && pip.ListSources(tenant).Length == 1
            && access.GetUsers(tenant).Count == original.Members.Length + 2 && second.Scenarios.All(x => x.Passed),
            "repeating PIP example at the same clock instant does not duplicate source, policy or membership");
        clock.Now = now.AddMinutes(1);
        var refreshed = examples.Ensure(tenant, "teacher", "refresh synthetic observation");
        check(refreshed.ObservedAt == clock.Now && refreshed.FreshUntil == clock.Now.AddMinutes(5)
            && refreshed.Source.LastSourceVersion != first.Source.LastSourceVersion && refreshed.Scenarios.All(x => x.Passed),
            "educational refresh replaces only its synthetic snapshot with a new bounded observation");
        check(policy.Evaluate(tenant, PipEducationalExample.Subject, PipEducationalExample.Permission, clock.Now).Decision == "NotApplicable",
            "educational PIP draft cannot confer permission in ordinary evaluation");
        using (var reopened = new FabricDatabase(Path.Combine(directory, "pip-educational", "fabric.db")))
        {
            var reopenedAccess = new PersistentAccessManagement(reopened, new InMemoryAccessManagement());
            var reopenedPip = new PipStore(reopened, reopenedAccess);
            var reopenedPolicy = new AccessPolicyStore(reopened, reopenedAccess, null, reopenedPip);
            check(reopenedPip.ReadSource(tenant, first.Source.Id).Educational
                && reopenedPip.Read(tenant, PipEducationalExample.Subject, clock.Now, first.Source.Id).Snapshot?.SourceVersion == refreshed.Source.LastSourceVersion
                && reopenedPolicy.Read(tenant).Patterns.Single(x => x.Id == first.PolicyId).Status == "draft",
                "educational PIP source, snapshot and draft survive database reopen");
        }
        var marker = db.Read<PipEducationalMarker>(PipEducationalExample.MarkerKind, tenant.ToString("D"))!;
        db.Transaction(() => { db.Put(PipEducationalExample.MarkerKind, tenant.ToString("D"), marker with { Version = "unsupported" }); return true; });
        var before = db.Outbox().Length;
        check(Rejected(() => examples.Ensure(tenant, "teacher", "do not overwrite incompatible example"))
            && db.Outbox().Length == before && policy.Read(tenant).Revision == revision,
            "incompatible educational marker fails atomically without rewriting existing data");
    }

    private sealed class ExampleClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
