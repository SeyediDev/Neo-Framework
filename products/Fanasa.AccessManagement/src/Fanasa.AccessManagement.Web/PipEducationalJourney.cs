using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Organization;
using Fanasa.AccessManagement.Web.Persistence;

namespace Fanasa.AccessManagement.Web.Governance;

public sealed record PipEducationalJourneyMarker(Guid TenantId, Guid SourceId, Guid CalendarId, Guid[] PolicyIds,
    string Version, DateTimeOffset CreatedAt);
public sealed record PipEducationalJourneyEvidence(string SourceStatus, string? LastRunError, string SnapshotStatus,
    DateTimeOffset? ObservedAt, DateTimeOffset? FreshUntil, long Generation, string? SourceVersion);
public sealed record PipEducationalJourneyScenario(string Key, string Title, string Subject, string Permission,
    DateTimeOffset EvaluatedAt, string ExpectedDecision, string ExpectedReason, AccessDecision Actual, bool Passed,
    string Lesson, string ConfigurationArea, PipEducationalJourneyEvidence Evidence, bool Synthetic = true);
public sealed record PipEducationalJourneyView(string Version, Guid TenantId, Guid SourceId, string Permission,
    Guid[] PolicyIds, DateTimeOffset GeneratedAt, PipEducationalJourneyScenario[] Scenarios,
    bool Simulation = true, bool Enforced = false);

/// <summary>
/// Immutable historical fixtures evaluated by the real PDP. The v1 example remains intact, and no connector,
/// account, direct grant or published policy is created. Employment claims here are explicitly synthetic.
/// </summary>
public sealed class PipEducationalJourney(FabricDatabase database, PersistentAccessManagement access,
    TemplateOrganizationStore templates, AccessPolicyStore policies, PipStore pip,
    PipEducationalExample educational, TimeProvider clock)
{
    public const string MarkerKind = "pip.educational.journey.v1";
    public const string Version = "fanasa.pip-journey/1";
    public const string Permission = "tenancy.manage";
    public const string SubjectPrefix = "urn:fanasa:example:pip:journey:";

    public PipEducationalJourneyView Ensure(Guid tenantId, string actor, string reason) => database.Transaction<PipEducationalJourneyView>(() =>
    {
        AccessPolicyStore.Label(actor, 200); AccessPolicyStore.Label(reason, 1000);
        if (!templates.IsTemplateTenant(tenantId)) throw new InvalidOperationException("مسیر آموزشی فقط در سازمان الگو ساخته می‌شود.");
        var marker = database.Read<PipEducationalJourneyMarker>(MarkerKind, tenantId.ToString("D"));
        if (marker is null)
        {
            if (policies.Read(tenantId).Patterns.Any(x => x.Permission == Permission))
                throw new InvalidOperationException("عملیات آموزشی قبلاً سیاست دارد؛ سیاست موجود تغییر نکرد.");
            var source = educational.Ensure(tenantId, actor, reason).Source;
            // Read through the calendar store inside this writer transaction; template.Read opens its own read snapshot.
            var office = new OrganizationCalendarStore(database, access).Read(tenantId).Calendars
                .SingleOrDefault(x => x.Code == "example-office" && x.Active && x.Kind == "work")
                ?? throw new InvalidOperationException("تقویم کاری آموزشی در دسترس نیست.");
            var ids = Commands(source.Id, office.Id).Select(command =>
            {
                var state = policies.Read(tenantId);
                return policies.Draft(tenantId, command with { ExpectedRevision = state.Revision, Reason = reason }, actor).Patterns[^1].Id;
            }).ToArray();
            foreach (var fixture in Fixtures) Seed(tenantId, source, fixture, actor, reason);
            // Historical fixture storage must not leave the shared v1 source summary pointing at a historical run.
            educational.Ensure(tenantId, actor, reason);
            marker = new(tenantId, source.Id, office.Id, ids, Version, clock.GetUtcNow());
            database.Put(MarkerKind, tenantId.ToString("D"), marker);
            database.Emit("pip-journey:" + tenantId, "PipEducationalJourneyCreated",
                new { TenantId = tenantId, SourceId = source.Id, Version, Actor = actor, Reason = reason }, clock.GetUtcNow());
        }
        Validate(tenantId, marker);
        var sourceNow = pip.ReadSource(tenantId, marker.SourceId);
        var scenarios = Fixtures.Select(fixture =>
        {
            var subject = SubjectPrefix + fixture.Key;
            var actual = policies.EvaluateDraftsForSimulation(tenantId, subject, Permission, fixture.At);
            var lookup = pip.Read(tenantId, subject, fixture.At, marker.SourceId);
            var checkpoint = pip.Checkpoint(tenantId, marker.SourceId, subject);
            var evidence = new PipEducationalJourneyEvidence(fixture.ForeignResponse ? "synthetic-rejected-response"
                : fixture.Outage ? "synthetic-upstream-outage" : "synthetic-ready", checkpoint?.Error, lookup.Status,
                lookup.Snapshot?.ObservedAt, lookup.Snapshot?.FreshUntil, lookup.Snapshot?.Generation ?? sourceNow.Generation,
                lookup.Snapshot?.SourceVersion);
            return new PipEducationalJourneyScenario(fixture.Key, fixture.Title, subject, Permission, fixture.At,
                fixture.Expected, fixture.Reason, actual, actual.Decision == fixture.Expected && actual.Reason == fixture.Reason,
                fixture.Lesson, fixture.Area, evidence);
        }).ToArray();
        return new PipEducationalJourneyView(Version, tenantId, marker.SourceId, Permission, marker.PolicyIds, clock.GetUtcNow(), scenarios);
    });

    private void Seed(Guid tenant, PipSourceDefinition source, Fixture fixture, string actor, string reason)
    {
        var subject = SubjectPrefix + fixture.Key;
        if (access.GetUsers(tenant).Any(x => x.KeycloakSubject == subject)
            || pip.Read(tenant, subject, fixture.At, source.Id).Snapshot is not null)
            throw new InvalidOperationException("شناسهٔ آموزشی قبلاً داده دارد؛ دادهٔ موجود تصاحب نشد.");
        var member = access.Audited(() => access.AddUser(new(tenant, subject, fixture.Title)), actor, reason, "PipJourneyMemberAdded");
        if (fixture.InactiveMembership) access.SetMembership(tenant, member.Id, false, actor, reason);
        var observed = fixture.At.AddSeconds(-fixture.AgeSeconds);
        string? hash = null;
        if (fixture.HasSnapshot)
        {
            var attributes = Attributes(fixture);
            var version = SnapshotVersion(fixture);
            hash = PipStore.HashAttributes(tenant, subject, 1, attributes, version, observed);
            pip.SaveSnapshot(source, subject, new(tenant, subject, 1, attributes, version, null, observed, hash), observed);
        }
        if (fixture.ForeignResponse)
        {
            var foreignTenant = Guid.Parse("4b21a1a1-701f-4df8-b3f2-c725415a68cd");
            if (tenant == foreignTenant) foreignTenant = Guid.Empty;
            var attributes = Attributes(fixture);
            var version = SnapshotVersion(fixture);
            var rejected = false;
            try
            {
                pip.SaveSnapshot(source, subject, new(foreignTenant, subject, 1, attributes, version, null, observed,
                    PipStore.HashAttributes(foreignTenant, subject, 1, attributes, version, observed)), observed);
            }
            catch (InvalidDataException) { rejected = true; }
            if (!rejected) throw new InvalidOperationException("پاسخ سازمان دیگر باید پیش از ذخیره رد شود.");
        }
        if (fixture.HasSnapshot || fixture.ForeignResponse)
        {
            var error = fixture.ForeignResponse ? "invalid-response" : fixture.Outage ? "transport-failed" : null;
            var completed = observed.AddSeconds(fixture.Outage ? 1 : 0);
            // This is a labelled synthetic receipt, not an external request or a change to global source health.
            pip.RecordRun(new(Guid.NewGuid(), source.Id, tenant, subject, error is null ? "succeeded" : "failed",
                observed, completed, error is null ? hash : null, error, actor, "synthetic-journey: " + fixture.Key, source.Generation));
        }
    }

    private void Validate(Guid tenant, PipEducationalJourneyMarker marker)
    {
        if (marker.TenantId != tenant || marker.Version != Version || marker.PolicyIds is not { Length: 4 })
            throw new InvalidOperationException("نسخهٔ مسیر آموزشی سازگار نیست؛ دادهٔ موجود تغییر نکرد.");
        var v1 = database.Read<PipEducationalMarker>(PipEducationalExample.MarkerKind, tenant.ToString("D"));
        var source = pip.ReadSource(tenant, marker.SourceId);
        if (v1 is null || v1.SourceId != source.Id || v1.Version != PipEducationalExample.Version || !source.Educational || !source.Enabled
            || source.Endpoint != PipEducationalExample.Endpoint || source.SecretReference is not null || source.PollIntervalSeconds != 0
            || source.SchemaVersion != 1 || source.MaxAgeSeconds != 300 || !PipSchema.Definitions(source).SequenceEqual(PipSchema.Default))
            throw new InvalidOperationException("منبع مصنوعی مسیر آموزشی تغییر کرده است؛ بازنویسی نشد.");
        var state = policies.Read(tenant);
        var commands = Commands(source.Id, marker.CalendarId);
        for (var i = 0; i < commands.Length; i++)
        {
            var pattern = state.Patterns.SingleOrDefault(x => x.Id == marker.PolicyIds[i]);
            if (pattern is null || (pattern with { Id = Guid.Empty, CreatedBy = "", Reason = "" }) != ExpectedPattern(commands[i]))
                throw new InvalidOperationException("سیاست مسیر آموزشی تغییر کرده است؛ بازنویسی یا منتشر نشد.");
        }
        foreach (var fixture in Fixtures)
        {
            var subject = SubjectPrefix + fixture.Key;
            var member = access.GetUsers(tenant).SingleOrDefault(x => x.KeycloakSubject == subject);
            if (member is null || member.IsActive == fixture.InactiveMembership || access.GetGrants(subject).Length != 0)
                throw new InvalidOperationException("عضویت یا مجوز مستقیم نمونه تغییر کرده است؛ بازنویسی نشد.");
            var lookup = pip.Read(tenant, subject, fixture.At, source.Id);
            var checkpoint = pip.Checkpoint(tenant, source.Id, subject);
            var expectedError = fixture.ForeignResponse ? "invalid-response" : fixture.Outage ? "transport-failed" : null;
            if ((fixture.HasSnapshot || fixture.ForeignResponse) && (checkpoint is null || checkpoint.Error != expectedError
                || checkpoint.Status != (expectedError is null ? "succeeded" : "failed")
                || checkpoint.CompletedAt > fixture.At || checkpoint.StartedAt != fixture.At.AddSeconds(-fixture.AgeSeconds)))
                throw new InvalidOperationException("رسید مصنوعی مسیر آموزشی تغییر کرده است؛ بازنویسی نشد.");
            if (!fixture.HasSnapshot)
            {
                if (lookup.Status != "missing" || lookup.Error != "no-snapshot") throw new InvalidOperationException("سناریوی بدون داده تغییر کرده است.");
                continue;
            }
            var expectedHash = PipStore.HashAttributes(tenant, subject, 1, Attributes(fixture), SnapshotVersion(fixture), fixture.At.AddSeconds(-fixture.AgeSeconds));
            if (lookup.Snapshot is not { } snapshot || snapshot.ContentHash != expectedHash || snapshot.Generation != source.Generation)
                throw new InvalidOperationException("دادهٔ مصنوعی مسیر آموزشی تغییر کرده است؛ بازنویسی نشد.");
        }
    }

    private static PatternCommand[] Commands(Guid source, Guid calendar) =>
    [
        new(0, "مسیر آموزشی: واحد مهندسی اجازه دارد", "attribute", Permission, "Permit", null, null, null, null, null, null, "fixture",
            CalendarId: calendar, AttributeName: "organization.unit", AttributeValue: "engineering", PipSourceId: source),
        new(0, "مسیر آموزشی: انتقال به واحد دیگر منع دارد", "attribute", Permission, "Deny", null, null, null, null, null, null, "fixture",
            AttributeName: "organization.unit", AttributeValue: "engineering", PipSourceId: source, AttributeOperator: "not-equals"),
        new(0, "مسیر آموزشی: همکاری غیرفعال منع دارد", "attribute", Permission, "Deny", null, null, null, null, null, null, "fixture",
            AttributeName: "employment.active", AttributeValue: "false", PipSourceId: source),
        new(0, "مسیر آموزشی: ساعت کار و تعطیلات اجباری است", "time-guard", Permission, "Deny", null, null, null, null, null, null, "fixture", CalendarId: calendar)
    ];

    private static AccessPattern ExpectedPattern(PatternCommand c) => new(Guid.Empty, c.Name, c.Template, c.Permission, c.Effect,
        c.RoleId, c.UnitId, c.StartsAt, c.EndsAt, c.UtcStartHour, c.UtcEndHour, "draft", "", null, "", c.CalendarId,
        c.CalendarMode, c.Enforce, c.AttributeName, c.AttributeValue, c.PipSourceId, c.RequireFreshPip, c.AttributeOperator);
    private static Dictionary<string, string> Attributes(Fixture fixture)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal) { ["employment.active"] = fixture.Active ? "true" : "false" };
        if (fixture.Unit is not null) result["organization.unit"] = fixture.Unit;
        return result;
    }
    private static string SnapshotVersion(Fixture fixture) => "journey-v1-" + fixture.Key;
    private static DateTimeOffset At(string local) => DateTimeOffset.Parse(local + "+03:30", System.Globalization.CultureInfo.InvariantCulture).ToUniversalTime();
    private sealed record Fixture(string Key, string Title, DateTimeOffset At, string Expected, string Reason, string Lesson, string Area,
        bool Active = true, string? Unit = "engineering", bool HasSnapshot = true, int AgeSeconds = 0,
        bool InactiveMembership = false, bool Outage = false, bool ForeignResponse = false);
    private static readonly Fixture[] Fixtures =
    [
        new("active-engineering", "همکاری فعال، واحد مهندسی، ساعت مجاز", At("2026-10-17T10:00:00"), "Permit", "draft-or-published-policy", "اجازهٔ واحد و نبود منع، همراه ساعت مجاز لازم است. employment در این مثال مصنوعی است؛ عضویت به‌تنهایی اثبات استخدام نیست.", "policy"),
        new("inactive-employment", "همکاری غیرفعال با عضویت همچنان فعال", At("2026-10-17T10:00:00"), "Deny", "explicit-deny", "منع صریح همکاری غیرفعال بر اجازهٔ واحد و هر مجوز مستقیم مقدم است.", "policy", Active: false),
        new("transferred-unit", "پس از انتقال از مهندسی به مالی", At("2026-10-17T10:00:00"), "Deny", "explicit-deny", "این تصویر مصنوعی پس از انتقال است؛ مقایسهٔ نابرابر واحد، اجازهٔ قبلی را با منع صریح می‌بندد.", "policy", Unit: "finance"),
        new("before-opening", "یک دقیقه پیش از آغاز کار", At("2026-10-17T07:59:00"), "Deny", "time-constraint-denied", "دادهٔ تازه و واحد درست محدودیت اجباری زمان را دور نمی‌زند.", "calendar"),
        new("opening", "لحظهٔ آغاز کار", At("2026-10-17T08:00:00"), "Permit", "draft-or-published-policy", "شروع بازه شامل است؛ تقویم زمان محلی Asia/Tehran را ارزیابی می‌کند.", "calendar"),
        new("closing", "لحظهٔ پایان کار", At("2026-10-17T16:00:00"), "Deny", "time-constraint-denied", "انتهای بازه شامل نیست؛ دادهٔ تازه پس از ساعت بسته مجوز نمی‌سازد.", "calendar"),
        new("holiday", "تعطیلی مصنوعی وسط هفته", At("2026-10-20T10:00:00"), "Deny", "time-constraint-denied", "استثنای تعطیل کل روز محلی را می‌بندد؛ این تاریخ ادعای تعطیل رسمی نیست.", "calendar"),
        new("weekend", "جمعهٔ غیرکاری", At("2026-10-23T10:00:00"), "Deny", "time-constraint-denied", "نبود پنجرهٔ کاری در جمعه، اجازه را متوقف می‌کند.", "calendar"),
        new("missing-snapshot", "عضو فعال بدون دادهٔ مرجع", At("2026-10-17T10:00:00"), "Indeterminate", "stale-or-missing-pip", "نبود داده به معنی همکاری فعال یا واحد مجاز نیست؛ مرجع باید دادهٔ معتبر بدهد.", "source", HasSnapshot: false),
        new("missing-unit", "دادهٔ تازه بدون ویژگی واحد", At("2026-10-17T10:00:00"), "Indeterminate", "stale-or-missing-pip", "ویژگی اختیاری در قرارداد منبع، هنگام ارجاع سیاست الزامی می‌شود؛ نبود آن معادل نابرابری نیست.", "source", Unit: null),
        new("expired", "لحظهٔ پایان اعتبار پنج‌دقیقه‌ای", At("2026-10-17T10:05:00"), "Indeterminate", "stale-or-missing-pip", "در FreshUntil داده منقضی است؛ ارزیابی اجازه را متوقف می‌کند.", "source", AgeSeconds: 300),
        new("outage-fresh", "قطع مصنوعی منبع با آخرین دادهٔ هنوز معتبر", At("2026-10-17T10:04:00"), "Permit", "draft-or-published-policy", "خطای دریافت آخرین دادهٔ سالم را تا پایان مهلت اصلی حفظ می‌کند؛ خطا زمان اعتبار را تمدید نمی‌کند.", "source", AgeSeconds: 240, Outage: true),
        new("outage-expired", "ادامهٔ قطع منبع پس از پایان اعتبار", At("2026-10-17T10:05:00"), "Indeterminate", "stale-or-missing-pip", "دادهٔ قدیمی پس از انقضا مجوز نمی‌دهد؛ رفع خطا و دریافت مشاهدهٔ تازه لازم است.", "source", AgeSeconds: 300, Outage: true),
        new("foreign-response", "پاسخ منبع متعلق به سازمان دیگر", At("2026-10-17T10:00:00"), "Indeterminate", "stale-or-missing-pip", "پاسخ با tenant متفاوت واقعاً پیش از ذخیره رد شده است؛ داده‌ای برای این عضو و سازمان ساخته نمی‌شود.", "source", HasSnapshot: false, ForeignResponse: true),
        new("inactive-membership", "عضویت غیرفعال با همکاری مصنوعی فعال", At("2026-10-17T10:00:00"), "Deny", "inactive-membership", "عضویت سازمانی و وضعیت همکاری دو تعریف مستقل‌اند؛ عضویت غیرفعال پیش از ارزیابی سیاست رد می‌شود.", "membership", InactiveMembership: true)
    ];
}
