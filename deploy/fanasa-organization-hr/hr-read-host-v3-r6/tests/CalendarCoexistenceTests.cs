using Fanasa.AccessManagement.Web.Organization;
using Fanasa.AccessManagement.Web.Security;
using Fanasa.HrReadCandidate;
using Fanasa.HrReadPdp.Tests;

namespace Fanasa.HrReadHost.Tests;

// The HR3 source is query-only. Calendar policies are opt-in and cannot silently
// convert a native audit obligation into a fulfilled admission receipt.
public static class CalendarCoexistenceTests
{
    public static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool value, string label)
        { count++; if (!value) throw new InvalidOperationException("HR3 calendar coexistence failed: " + label); }

        foreach (var enforce in new[] { false, true })
        foreach (var open in new[] { false, true })
        {
            using var f = await CentralHostRecordsTests.CreateFixtureAsync();
            Publish(f, open, enforce);
            var before = f.Fingerprint();
            Check(await f.ReadAsync() is not null, $"default-disabled ignores calendar rule enforce={enforce}, open={open}");
            Check(f.Fingerprint() == before, "disabled calendar coexistence performs no writes");
        }

        using (var f = await CentralHostRecordsTests.CreateFixtureAsync())
        {
            Publish(f, false, true);
            var before = f.Fingerprint();
            Check(await f.Transport.CreateSource(Adapter(f, Guid.NewGuid())).ReadAsync(f.Transport.Query) is not null,
                "an unlisted tenant retains the native-grant witness");
            Check(f.Fingerprint() == before, "unlisted tenant read performs no writes");
        }

        foreach (var open in new[] { false, true })
        {
            using var f = await CentralHostRecordsTests.CreateFixtureAsync();
            Publish(f, open, false);
            var before = f.Fingerprint();
            Check(await f.Transport.CreateSource(Adapter(f, f.Tenant.Id)).ReadAsync(f.Transport.Query) is not null,
                "Enforce=false remains simulation-only even for an enabled tenant");
            Check(f.Fingerprint() == before, "non-enforced calendar read performs no writes");
        }

        foreach (var open in new[] { false, true })
        {
            using var f = await CentralHostRecordsTests.CreateFixtureAsync();
            Publish(f, open, true);
            var before = f.Fingerprint();
            var decision = f.Legacy.EvaluateForReadOnlyHost(f.Tenant.Id, f.Member.KeycloakSubject, HrReadDesign.Permission, FabricSourceTests.Now, true);
            Check(open ? decision.Decision == "Permit" && decision.Obligations.Contains("audit") : decision.Decision == "Deny",
                "enabled rule retains its actual decision and native audit obligation");
            Check(await f.Transport.CreateSource(Adapter(f, f.Tenant.Id)).ReadAsync(f.Transport.Query) is null,
                open ? "query-only bridge refuses an unfulfilled native audit obligation" : "closed enforced calendar denies HR3 witness");
            Check(f.Fingerprint() == before, "enabled restriction neither mutates records nor falsely fulfills audit");
        }

        using (var f = await CentralHostRecordsTests.CreateFixtureAsync())
        {
            Publish(f, true, true);
            f.Transport.Delete(OrganizationCalendarStore.DocumentKind, f.Tenant.Id.ToString());
            var before = f.Fingerprint();
            Check(await f.Transport.CreateSource(Adapter(f, f.Tenant.Id)).ReadAsync(f.Transport.Query) is null,
                "missing enforced calendar fails closed despite the native grant");
            Check(f.Fingerprint() == before, "missing calendar cannot be repaired by the read-only adapter");
        }

        using (var f = await CentralHostRecordsTests.CreateFixtureAsync())
        {
            Publish(f, true, true, FabricSourceTests.Now.AddSeconds(2));
            var before = f.Fingerprint();
            var data = await f.ReadAsync();
            Check(data?.Evidence.ValidUntil == FabricSourceTests.Format(FabricSourceTests.Now.AddSeconds(5)),
                "a disabled calendar rule cannot shorten the existing witness lease");
            Check(f.Fingerprint() == before, "ignored future calendar boundary writes nothing");
        }
        return count;
    }

    private static Hr3CentralHostRecords Adapter(CentralHostRecordsTests.Fixture f, Guid configuredTenant) =>
        new(f.Transport.Database, f.Registry, f.Access, f.Legacy, f.ProofSource, f.Transport.Time,
            new TemporalAccessGate(new() { Enabled = true, TenantIds = [configuredTenant] }, f.Legacy, f.Transport.Time));

    private static void Publish(CentralHostRecordsTests.Fixture f, bool open, bool enforce, DateTimeOffset? startsAt = null)
    {
        var now = FabricSourceTests.Now;
        var calendars = new OrganizationCalendarStore(f.Transport.Database, f.Access, f.Transport.Time);
        var state = calendars.Save(f.Tenant.Id, new(0, null, "hr3-calendar", "Synthetic HR3 calendar", "work", "UTC",
            DateOnly.FromDateTime(now.UtcDateTime).AddDays(-1), DateOnly.FromDateTime(now.UtcDateTime).AddDays(1),
            open ? [new((int)now.DayOfWeek, 0, 1440)] : [], [], true, "Synthetic isolated source", "Calendar coexistence test"), "calendar-author");
        var draft = f.Legacy.Draft(f.Tenant.Id, new(f.Legacy.Read(f.Tenant.Id).Revision, "Synthetic time guard", "time-guard",
            HrReadDesign.Permission, "Deny", null, null, startsAt, null, null, null, "Calendar coexistence test",
            state.Calendars.Single().Id, "working-time", enforce), "policy-author");
        f.Legacy.Transition(f.Tenant.Id, draft.Patterns.Last().Id, draft.Revision, "publish", "independent-reviewer", "Reviewed synthetic rule");
    }
}
