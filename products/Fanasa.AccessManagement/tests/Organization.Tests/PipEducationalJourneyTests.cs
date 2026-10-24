using System.Text.Json;
using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Governance;
using Fanasa.AccessManagement.Web.Organization;
using Fanasa.AccessManagement.Web.Persistence;

static class PipEducationalJourneyTests
{
    public static void Run(string directory, Action<bool, string> check)
    {
        var path = Path.Combine(directory, "pip-journey", "fabric.db");
        using var db = new FabricDatabase(path);
        var access = new PersistentAccessManagement(db, new InMemoryAccessManagement());
        var clock = new JourneyClock(new DateTimeOffset(2026, 10, 24, 8, 0, 0, TimeSpan.Zero));
        var calendar = new OrganizationCalendarStore(db, access);
        var pip = new PipStore(db, access);
        var policy = new AccessPolicyStore(db, access, calendar, pip);
        var template = new TemplateOrganizationStore(db, access, calendar, policy, clock);
        var educational = new PipEducationalExample(db, access, template, policy, pip, clock);
        var journey = new PipEducationalJourney(db, access, template, policy, pip, educational, clock);
        var original = template.Ensure("teacher", "educational fixture");
        var tenant = original.Tenant.Id;
        var v1 = educational.Ensure(tenant, "teacher", "preserve existing v1");
        var oldPolicies = policy.Read(tenant).Patterns;
        var oldDecisions = template.EvaluateScenarios().Select(x => x.Actual.Decision).ToArray();
        var oldSnapshot = pip.Read(tenant, PipEducationalExample.Subject, clock.Now, v1.Source.Id).Snapshot!;
        var other = access.CreateTenant("journey-ordinary", "ordinary tenant").Id;
        Reject(() => journey.Ensure(other, "teacher", "not educational"), check, "PIP journey refuses an ordinary tenant before any fixture write");
        check(pip.ListSources(other).Length == 0 && access.GetUsers(other).Count == 0, "refused journey leaves ordinary tenant data unchanged");
        var first = journey.Ensure(tenant, "teacher", "bootstrap extended educational journey");
        check(first.Scenarios.Length == 15 && first.Scenarios.All(x => x.Passed), "all fifteen PIP journey scenarios run through the PDP with expected decisions and reasons");
        check(first.SourceId == v1.Source.Id && pip.ListSources(tenant).Length == 1 && first.Permission == "tenancy.manage",
            "journey reuses the v1 educational source and a teaching action unused by earlier examples");
        check(first.PolicyIds.Length == 4 && policy.Read(tenant).Patterns.Where(x => first.PolicyIds.Contains(x.Id)).All(x => x.Status == "draft" && !x.Enforce),
            "journey policies remain unforced drafts, including explicit employment and unit vetoes");
        check(first.Simulation && !first.Enforced && first.Scenarios.All(x => x.Synthetic && x.EvaluatedAt < first.GeneratedAt),
            "journey clearly labels historical fixture times as synthetic simulation");
        check(oldPolicies.SequenceEqual(policy.Read(tenant).Patterns.Take(oldPolicies.Length))
            && template.EvaluateScenarios().Select(x => x.Actual.Decision).SequenceEqual(oldDecisions) && oldDecisions.Length == 24,
            "journey preserves v1 policies and every existing template calendar scenario");
        check(pip.Read(tenant, PipEducationalExample.Subject, clock.Now, v1.Source.Id).Snapshot?.ContentHash == oldSnapshot.ContentHash
            && pip.ReadSource(tenant, v1.Source.Id).LastSourceVersion == oldSnapshot.SourceVersion,
            "historical journey fixtures preserve the current v1 observation and shared source summary");
        check(first.Scenarios.All(x => access.GetGrants(x.Subject).Length == 0)
            && first.Scenarios.All(x => policy.Evaluate(tenant, x.Subject, first.Permission, x.EvaluatedAt).Decision != "Permit")
            && first.Scenarios.All(x => policy.EvaluateLive(tenant, x.Subject, first.Permission, x.EvaluatedAt).Decision != "Permit"),
            "journey identities receive no grants or ordinary/live runtime permissions");
        var inactive = first.Scenarios.Single(x => x.Key == "inactive-employment");
        check(access.GetUsers(tenant).Single(x => x.KeycloakSubject == inactive.Subject).IsActive && inactive.Actual.Decision == "Deny",
            "journey distinguishes inactive external employment from active local membership");
        var outage = first.Scenarios.Single(x => x.Key == "outage-fresh");
        var expired = first.Scenarios.Single(x => x.Key == "outage-expired");
        check(outage.Evidence is { SourceStatus: "synthetic-upstream-outage", LastRunError: "transport-failed", SnapshotStatus: "fresh" }
            && outage.Actual.Decision == "Permit" && expired.Evidence.SnapshotStatus == "stale" && expired.Actual.Decision == "Indeterminate",
            "journey explains last-good evidence during an outage and stops permission at original expiry");
        var foreign = first.Scenarios.Single(x => x.Key == "foreign-response");
        check(foreign.Evidence is { LastRunError: "invalid-response", SnapshotStatus: "missing" }
            && pip.Read(tenant, foreign.Subject, foreign.EvaluatedAt, first.SourceId).Snapshot is null,
            "foreign-tenant journey response is rejected without persisting local evidence");
        check(first.Scenarios.Single(x => x.Key == "missing-unit").Actual.Pip?.Status == "missing-attribute"
            && first.Scenarios.Single(x => x.Key == "holiday").Actual.CalendarChecks?.Any(x => x.Known && !x.Allowed && x.TimeZoneId == "Asia/Tehran") == true,
            "journey returns actual missing-attribute and civil-calendar decision evidence");
        var count = db.Outbox().Length;
        var revision = policy.Read(tenant).Revision;
        var members = access.GetUsers(tenant).Count;
        var repeated = journey.Ensure(tenant, "teacher", "read existing journey");
        check(repeated.PolicyIds.SequenceEqual(first.PolicyIds) && repeated.Scenarios.All(x => x.Passed)
            && db.Outbox().Length == count && policy.Read(tenant).Revision == revision && access.GetUsers(tenant).Count == members,
            "repeated journey validation performs no writes or duplicate policy/member creation");
        var rerunV1 = educational.Ensure(tenant, "teacher", "v1 still works after journey");
        check(rerunV1.Scenarios.Length == 3 && rerunV1.Scenarios.All(x => x.Passed) && rerunV1.Source.Id == first.SourceId,
            "original v1 example remains usable after journey installation");
        using (var reopened = new FabricDatabase(path))
        {
            var reopenedAccess = new PersistentAccessManagement(reopened, new InMemoryAccessManagement());
            var reopenedPip = new PipStore(reopened, reopenedAccess);
            var reopenedCalendars = new OrganizationCalendarStore(reopened, reopenedAccess);
            var reopenedPolicies = new AccessPolicyStore(reopened, reopenedAccess, reopenedCalendars, reopenedPip);
            var reopenedTemplate = new TemplateOrganizationStore(reopened, reopenedAccess, reopenedCalendars, reopenedPolicies, clock);
            var reopenedV1 = new PipEducationalExample(reopened, reopenedAccess, reopenedTemplate, reopenedPolicies, reopenedPip, clock);
            var restored = new PipEducationalJourney(reopened, reopenedAccess, reopenedTemplate, reopenedPolicies, reopenedPip, reopenedV1, clock)
                .Ensure(tenant, "teacher", "reopen durable fixtures");
            check(restored.PolicyIds.SequenceEqual(first.PolicyIds) && restored.Scenarios.All(x => x.Passed), "journey markers, receipts, policies and historical snapshots survive database reopen");
        }
        var serialized = JsonSerializer.Serialize(first.Scenarios);
        check(!serialized.Contains("Attributes\":", StringComparison.Ordinal) && !serialized.Contains("SecretReference", StringComparison.Ordinal),
            "journey evidence response contains decision provenance without raw source attributes or credentials");
        var marker = db.Read<PipEducationalJourneyMarker>(PipEducationalJourney.MarkerKind, tenant.ToString("D"))!;
        db.Transaction(() => { db.Put(PipEducationalJourney.MarkerKind, tenant.ToString("D"), marker with { Version = "unknown" }); return true; });
        count = db.Outbox().Length;
        Reject(() => journey.Ensure(tenant, "teacher", "preserve modified fixture"), check, "incompatible journey marker is refused atomically");
        check(db.Outbox().Length == count && policy.Read(tenant).Revision == revision, "refused journey migration does not rewrite fixtures or earlier policy definitions");
    }
    private static void Reject(Action action, Action<bool, string> check, string label)
    {
        try { action(); } catch (InvalidOperationException) { check(true, label); return; }
        throw new Exception("Expected rejection: " + label);
    }
    private sealed class JourneyClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
}
