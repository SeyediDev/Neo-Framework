using System.Collections.Concurrent;
using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Organization;
using Fanasa.AccessManagement.Web.Persistence;

var directory = Path.Combine(Path.GetTempPath(), "fanasa-calendar-tests-" + Guid.NewGuid());
Directory.CreateDirectory(directory);
var count = 0;
void Check(bool success, string label) { if (!success) throw new Exception(label); Console.WriteLine("PASS " + label); count++; }
void Reject<T>(Action action, string label) where T : Exception
{ try { action(); } catch (T) { Check(true, label); return; } throw new Exception("Expected rejection: " + label); }
try
{
    using var database = new FabricDatabase(Path.Combine(directory, "fabric.db"));
    var access = new PersistentAccessManagement(database, new InMemoryAccessManagement());
    var tenant = access.CreateTenant("calendar", "آزمون تقویم").Id;
    var other = access.CreateTenant("other-calendar", "سازمان دیگر").Id;
    var store = new OrganizationCalendarStore(database, access);
    OrganizationCalendarCommand Command(string code = "office") => new(store.Read(tenant).Revision, null, code, "ساعات کاری", "work", "Asia/Tehran",
        new(2026, 1, 1), new(2026, 12, 31), [new(1, 480, 960), new(2, 480, 960), new(4, 480, 720)],
        [new(new(2026, 10, 20), "closed", [], "تعطیلی صرفاً آموزشی")], true, "آزمون مصنوعی", "تعریف نمونه");
    Guid Save(OrganizationCalendarCommand command) => store.Save(tenant, command, "test-actor").Calendars.Single(x => x.Code == command.Code).Id;
    OrganizationCalendarEvaluation At(Guid id, string instant, string mode = "working-time") => store.Evaluate(tenant, id, DateTimeOffset.Parse(instant), mode);

    Check(store.Read(tenant).Revision == 0 && store.Read(tenant).Calendars.Length == 0, "missing calendar state is empty");
    var office = Save(Command());
    Check(!At(office, "2026-10-19T07:59:59+03:30").Allowed, "minute boundary denies immediately before opening");
    Check(At(office, "2026-10-19T08:00:00+03:30").Allowed, "opening boundary is inclusive");
    Check(At(office, "2026-10-19T15:59:59.999+03:30").Allowed, "subminute instant before closing allowed");
    Check(!At(office, "2026-10-19T16:00:00+03:30").Allowed, "closing boundary is exclusive");
    Check(At(office, "2026-10-19T04:30:00Z").Allowed, "UTC instant resolves using tenant timezone");
    Check(At(office, "2026-10-19T22:00:00+03:30", "non-holiday").Allowed, "non-holiday does not restrict hours");
    Check(!At(office, "2026-10-23T10:00:00+03:30", "non-holiday").Allowed, "closed weekday denies non-holiday");
    Check(At(office, "2026-10-20T10:00:00+03:30").Reason == "calendar-date-closed", "holiday overrides weekly opening");
    Check(!At(office, "2026-10-20T10:00:00+03:30", "non-holiday").Allowed, "holiday denies whole-day policy");
    Check(!At(office, "2027-01-01T10:00:00+03:30").Known, "expired coverage fails closed");
    Check(!At(office, "2025-12-31T10:00:00+03:30").Known, "not-yet-valid coverage fails closed");
    Check(!store.Evaluate(other, office, DateTimeOffset.UtcNow).Allowed, "cross-tenant identifier does not resolve");
    Check(!At(Guid.NewGuid(), "2026-10-19T10:00:00+03:30").Known, "missing calendar fails closed");
    Check(At(office, "2026-10-19T10:00:00+03:30").LocalTime?.Offset == TimeSpan.FromMinutes(210), "evaluation returns actual offset");

    var night = Save(Command("night") with { WeeklyWindows = [new(1, 1320, 360)], Exceptions = [] });
    Check(At(night, "2026-10-19T22:00:00+03:30").Allowed, "overnight starts on originating day");
    Check(At(night, "2026-10-20T05:59:59+03:30").Reason == "calendar-overnight-window", "overnight continuation uses preceding day");
    Check(!At(night, "2026-10-20T06:00:00+03:30").Allowed, "overnight end excluded");
    Check(!At(night, "2026-10-20T02:00:00+03:30", "non-holiday").Allowed, "overnight continuation does not redefine next working day");
    var holidayNight = Save(Command("holiday-night") with { WeeklyWindows = [new(1, 1320, 360)] });
    Check(At(holidayNight, "2026-10-20T00:00:00+03:30").Reason == "calendar-date-closed", "destination holiday blocks night continuation at midnight");
    var overrideNight = Save(Command("override-night") with
    { WeeklyWindows = [new(1, 1320, 360)], Exceptions = [new(new(2026, 10, 20), "open", [new(600, 720)], "استثنای آموزشی")] });
    Check(!At(overrideNight, "2026-10-20T01:00:00+03:30").Allowed, "date opening replaces inherited night hours");
    Check(At(overrideNight, "2026-10-20T10:00:00+03:30").Allowed, "date opening supplies own schedule");
    Check(!At(overrideNight, "2026-10-20T12:00:00+03:30").Allowed, "override end excluded");
    var exceptionNight = Save(Command("exception-night") with
    { WeeklyWindows = [], Exceptions = [new(new(2026, 10, 19), "open", [new(1320, 360)], "شیفت استثنایی")] });
    Check(At(exceptionNight, "2026-10-20T02:00:00+03:30").Allowed, "date exception may originate overnight shift");
    var coverageStart = Save(Command("coverage-start") with { CoverageFrom = new(2026, 10, 20), WeeklyWindows = [new(1, 1320, 360)], Exceptions = [] });
    Check(!At(coverageStart, "2026-10-20T02:00:00+03:30").Allowed, "calendar cannot inherit an origin outside coverage");
    var weekWrap = Save(Command("week-wrap") with { WeeklyWindows = [new(6, 1320, 360)], Exceptions = [] });
    Check(At(weekWrap, "2026-10-18T02:00:00+03:30").Allowed, "Saturday overnight crosses week boundary");
    var data = Save(Command("data") with { Kind = "data" });
    Check(At(data, "2026-10-19T09:00:00+03:30").Allowed, "data availability uses precise evaluator");
    Check(!At(data, "2026-10-19T09:00:00+03:30", "non-holiday").Known, "data window cannot classify a working day");
    Check(!At(office, "2026-10-19T09:00:00+03:30", "invented-mode").Known, "unknown mode fails closed");
    var disabled = Save(Command("disabled") with { Active = false });
    Check(!At(disabled, "2026-10-19T09:00:00+03:30").Known, "disabled calendar fails closed");

    var dst = Save(Command("dst") with { TimeZoneId = "America/New_York", WeeklyWindows = [new(0, 60, 120)], Exceptions = [] });
    Check(At(dst, "2026-11-01T05:30:00Z").Allowed && At(dst, "2026-11-01T06:30:00Z").Allowed, "both instants in repeated DST hour follow schedule");
    Check(At(dst, "2026-11-01T05:30:00Z").LocalTime?.Offset == TimeSpan.FromHours(-4)
        && At(dst, "2026-11-01T06:30:00Z").LocalTime?.Offset == TimeSpan.FromHours(-5), "DST fold retains distinct offsets");
    Check(!At(dst, "2026-11-01T07:00:00Z").Allowed, "DST fold closing instant excluded");
    var spring = Save(Command("spring") with { TimeZoneId = "America/New_York", WeeklyWindows = [new(0, 150, 210)], Exceptions = [] });
    Check(!At(spring, "2026-03-08T06:59:59Z").Allowed && At(spring, "2026-03-08T07:00:00Z").Allowed, "spring gap uses first existing instant in window");
    Check(!At(spring, "2026-03-08T07:30:00Z").Allowed, "spring DST end excluded");

    var savedRevision = store.Read(tenant).Revision;
    var savedOutbox = database.Outbox().Length;
    Reject<InvalidOperationException>(() => Save(Command("stale") with { ExpectedRevision = 0 }), "stale writer rejected");
    Reject<ArgumentException>(() => Save(Command("OFFICE")), "case-insensitive code uniqueness enforced");
    Reject<KeyNotFoundException>(() => Save(Command("unknown-id") with { Id = Guid.NewGuid() }), "update cannot adopt absent identifier");
    Reject<ArgumentException>(() => Save(Command("invalid-zone") with { TimeZoneId = "Iran Standard Time" }), "Windows zone name rejected by IANA contract");
    Reject<ArgumentException>(() => Save(Command("invalid-zone2") with { TimeZoneId = "Imaginary/City" }), "unknown timezone rejected");
    Reject<ArgumentException>(() => Save(Command("invalid-day") with { WeeklyWindows = [new(7, 480, 960)] }), "invalid weekday rejected");
    Reject<ArgumentException>(() => Save(Command("invalid-minute") with { WeeklyWindows = [new(1, 480, 1441)] }), "invalid minute rejected");
    Reject<ArgumentException>(() => Save(Command("zero-length") with { WeeklyWindows = [new(1, 480, 480)] }), "equal boundaries cannot imply all-day");
    Reject<ArgumentException>(() => Save(Command("overlap") with { WeeklyWindows = [new(1, 480, 960), new(1, 600, 720)] }), "weekly overlap rejected");
    Reject<ArgumentException>(() => Save(Command("night-overlap") with { WeeklyWindows = [new(1, 1320, 360), new(2, 300, 480)] }), "overnight next-day overlap rejected");
    Reject<ArgumentException>(() => Save(Command("wrap-overlap") with { WeeklyWindows = [new(6, 1320, 360), new(0, 300, 480)] }), "week-boundary overlap rejected");
    Reject<ArgumentException>(() => Save(Command("duplicate-date") with { Exceptions = [..Command().Exceptions, ..Command().Exceptions] }), "duplicate date exceptions rejected");
    Reject<ArgumentException>(() => Save(Command("closed-with-hours") with { Exceptions = [new(new(2026, 10, 20), "closed", [new(480, 960)], "invalid")] }), "closed exception cannot contain hours");
    Reject<ArgumentException>(() => Save(Command("empty-open") with { Exceptions = [new(new(2026, 10, 20), "open", [], "invalid")] }), "open exception requires hours");
    Reject<ArgumentException>(() => Save(Command("outside-exception") with { Exceptions = [new(new(2027, 1, 1), "closed", [], "invalid")] }), "uncovered exception rejected");
    Reject<ArgumentException>(() => Save(Command("backward-coverage") with { CoverageThrough = new(2025, 1, 1) }), "reversed coverage rejected");
    Check(store.Read(tenant).Revision == savedRevision && database.Outbox().Length == savedOutbox, "rejected writes leave revision and audit intact");
    var adjacent = Save(Command("adjacent") with { WeeklyWindows = [new(1, 480, 600), new(1, 600, 720)] });
    Check(At(adjacent, "2026-10-19T10:00:00+03:30").Allowed, "adjacent windows have no gap");
    var fullDay = Save(Command("all-day") with { WeeklyWindows = [new(1, 0, 1440)] });
    Check(At(fullDay, "2026-10-19T23:59:59+03:30").Allowed && !At(fullDay, "2026-10-20T00:00:00+03:30").Allowed, "full-day interval ends at midnight");

    var revision = store.Read(tenant).Revision;
    var results = new ConcurrentBag<bool>();
    Parallel.For(0, 2, i => { try { Save(Command("concurrent-" + i) with { ExpectedRevision = revision }); results.Add(true); } catch (InvalidOperationException) { results.Add(false); } });
    Check(results.Count(x => x) == 1 && results.Count(x => !x) == 1, "CAS admits exactly one concurrent writer");
    Check(store.History(tenant, 1).Calendars.Length == 1 && store.History(tenant, 1).Calendars[0].Id == office, "historical snapshot preserved");
    Check(new OrganizationCalendarStore(database, access).Read(tenant).Revision == revision + 1, "state survives store restart");
    var beforeEvaluationAudit = database.Outbox().Length;
    At(office, "2026-10-19T08:00:00+03:30");
    Check(database.Outbox().Length == beforeEvaluationAudit, "preview does not write audit events");
    Check(database.Audit(tenant, ["OrganizationCalendarChanged"]).Length > 0, "calendar changes available in tenant audit");

    var current = store.Read(tenant);
    database.Transaction(() => { database.Put(OrganizationCalendarStore.DocumentKind, tenant.ToString(), current with { TenantId = other }); return true; });
    Check(!At(office, "2026-10-19T09:00:00+03:30").Known, "persisted cross-tenant binding corruption fails closed");
    database.Transaction(() => { database.Put(OrganizationCalendarStore.DocumentKind, tenant.ToString(), current with { Calendars = null! }); return true; });
    Check(!At(office, "2026-10-19T09:00:00+03:30").Known, "persisted null collection fails closed");
    database.Transaction(() => { database.Put(OrganizationCalendarStore.DocumentKind, tenant.ToString(), current); return true; });
    var tenantRow = access.GetTenants().Single(x => x.Id == tenant);
    database.Transaction(() => { database.Put("tenant", tenant.ToString(), tenantRow with { IsActive = false }); return true; });
    Check(!At(office, "2026-10-19T09:00:00+03:30").Known, "tenant deactivation invalidates calendar");
    Console.WriteLine($"{count} calendar checks passed.");
}
finally
{
    Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    Directory.Delete(directory, true);
}
