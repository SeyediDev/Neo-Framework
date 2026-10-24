using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Domain.Access;
using Fanasa.AccessManagement.Web.Governance;
using Fanasa.AccessManagement.Web.Persistence;

namespace Fanasa.AccessManagement.Web.Organization;

public sealed record TemplateOrganizationMarker(Guid TenantId, string Version, string CreatedBy, DateTimeOffset CreatedAt);
public sealed record TemplateOrganizationLesson(string Key, string Title, string Explanation, string ConfigurationArea);
public sealed record TemplateOrganizationScenario(string Key, string Title, string Subject, string Permission,
    DateTimeOffset At, string ExpectedDecision, string Lesson);
public sealed record TemplateOrganizationScenarioResult(TemplateOrganizationScenario Scenario, AccessDecision Actual, bool Passed);
public sealed record TemplateOrganizationView(TemplateOrganizationMarker Marker, Tenant Tenant, TenantUser[] Members,
    OrgState Chart, OrganizationCalendarState Calendars, PolicyState Policies, TemplateOrganizationLesson[] Lessons, string[] Notes);

/// <summary>
/// A separately marked educational tenant. Bootstrap is atomic and repeat calls preserve edits.
/// Subjects are synthetic identifiers, never Keycloak users; example policies remain drafts.
/// </summary>
public sealed class TemplateOrganizationStore(FabricDatabase database, PersistentAccessManagement access,
    OrganizationCalendarStore calendars, AccessPolicyStore policies, TimeProvider? timeProvider = null)
{
    public const string TenantKey = "template-organization";
    public const string Version = "fanasa.organization-example/1";
    public const string MarkerKind = "organization.example.v1";
    public const string Reader = "urn:fanasa:example:template-organization:hr-reader";
    public const string Auditor = "urn:fanasa:example:template-organization:finance-auditor";
    public const string Operator = "urn:fanasa:example:template-organization:night-operator";
    public const string GrantHolder = "urn:fanasa:example:template-organization:grant-holder";
    public const string Inactive = "urn:fanasa:example:template-organization:inactive-member";
    private const string SeedReason = "دادهٔ مصنوعی آموزشی سازمان الگو؛ بدون حساب ورود، قرارداد تجاری یا انتشار سیاست.";

    public TemplateOrganizationView Read() => database.ReadSnapshot(ReadCore);

    public bool IsTemplateTenant(Guid tenant)
    {
        bool Check() => tenant != Guid.Empty
            && database.Read<TemplateOrganizationMarker>(MarkerKind, TenantKey) is { Version: Version } marker
            && marker.TenantId == tenant
            && access.GetTenants().Any(x => x.Id == tenant && x.Key == TenantKey && x.IsActive);
        return database.IsInTransaction ? Check() : database.ReadSnapshot(Check);
    }

    private TemplateOrganizationView ReadCore()
    {
        var marker = database.Read<TemplateOrganizationMarker>(MarkerKind, TenantKey) ?? throw new KeyNotFoundException("سازمان الگو هنوز ساخته نشده است.");
        if (marker.Version != Version) throw new InvalidOperationException("نسخهٔ سازمان الگو با این برنامه سازگار نیست؛ دادهٔ موجود بازنویسی نشد.");
        var tenant = access.GetTenants().SingleOrDefault(x => x.Id == marker.TenantId && x.Key == TenantKey && x.IsActive)
            ?? throw new InvalidOperationException("شناسهٔ سازمان الگو با دادهٔ ثبت‌شده سازگار نیست.");
        var chart = database.Read<OrgState>("organization", tenant.Id.ToString()) ?? throw new InvalidOperationException("ساختار سازمان الگو در دسترس نیست.");
        return new(marker, tenant, access.GetUsers(tenant.Id).ToArray(), chart, calendars.Read(tenant.Id), policies.Read(tenant.Id), Lessons,
        [
            "تمام اشخاص و تاریخ‌های تعطیلی این سازمان آموزشی‌اند؛ هیچ حساب ورود Keycloak برای آن‌ها ساخته نشده است.",
            "سیاست‌های نمونه پیش‌نویس‌اند. نتیجهٔ سناریو با همان موتور سیاست و با احتساب پیش‌نویس‌ها محاسبه می‌شود؛ مجوز اجرای واقعی نیست.",
            "برای اجرای سیاست در سازمان واقعی، مدیر مستقل باید آن را بررسی و منتشر کند؛ تقویم و پوشش تعطیلات باید از مرجع معتبر همان سازمان تأمین شود.",
            "تقویم آموزشی از ۲۰۲۶/۰۱/۰۱ تا ۲۰۲۷/۱۲/۳۱ معتبر است؛ خارج این پوشش دسترسی وابسته به آن رد می‌شود.",
            "راه‌اندازی دوباره داده‌ها را پاک یا بازنویسی نمی‌کند. تغییر نمونه‌ها می‌تواند نتیجهٔ سناریوهای مرجع را عوض کند.",
            "برای این سازمان هیچ اشتراک، قرارداد، اعتبار مالی یا دسترسی به سازمان دیگری ایجاد نمی‌شود."
        ]);
    }

    public TemplateOrganizationView Ensure(string actor, string reason) => database.Transaction(() =>
    {
        AccessPolicyStore.Label(actor, 200); AccessPolicyStore.Label(reason, 1000);
        if (database.Read<TemplateOrganizationMarker>(MarkerKind, TenantKey) is not null) return ReadCore();
        if (access.GetTenants().Any(x => x.Key.Equals(TenantKey, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("کلید سازمان الگو قبلاً بدون نشان آموزشی ثبت شده است؛ سازمان موجود تصاحب یا تغییر نکرد.");

        var now = (timeProvider ?? TimeProvider.System).GetUtcNow();
        var tenant = access.Audited(() => access.CreateTenant(TenantKey, "سازمان الگو"), actor, reason, "TenantCreated");
        foreach (var member in new[]
        {
            (Reader, "کارشناس نمونهٔ منابع انسانی"), (Auditor, "حسابرس نمونهٔ مالی"),
            (Operator, "کارشناس نمونهٔ شیفت شب"), (GrantHolder, "عضو نمونه با مجوز مستقیم"), (Inactive, "عضو نمونهٔ غیرفعال")
        }) access.Audited(() => access.AddUser(new(tenant.Id, member.Item1, member.Item2)), actor, SeedReason, "TemplateMemberAdded");
        var inactive = access.GetUsers(tenant.Id).Single(x => x.KeycloakSubject == Inactive);
        access.SetMembership(tenant.Id, inactive.Id, false, actor, SeedReason);
        access.SetGrant(new(tenant.Id, GrantHolder, "organization.read", At("2027-12-31T23:59:59+03:30")), actor,
            "آزمون آموزشی: محدودیت اجباری تقویم باید مجوز مستقیم همین عضو مصنوعی را هم محدود کند.");

        var rootUnit = Guid.NewGuid(); var hr = Guid.NewGuid(); var finance = Guid.NewGuid(); var operations = Guid.NewGuid();
        var hrRole = Guid.NewGuid(); var financeRole = Guid.NewGuid(); var operatorRole = Guid.NewGuid();
        var hrPosition = Guid.NewGuid(); var financePosition = Guid.NewGuid(); var operatorPosition = Guid.NewGuid();
        var started = At("2026-01-01T00:00:00+03:30"); var ended = At("2028-01-01T00:00:00+03:30");
        // This is the one-time educational bootstrap, not a billable organization command or an imported tenant.
        var chart = new OrgState(tenant.Id, 1,
        [
            new(rootUnit, "سازمان الگو", "EXAMPLE", null, "division", true),
            new(hr, "منابع انسانی", "HR", rootUnit, "department", true),
            new(finance, "مالی", "FINANCE", rootUnit, "department", true),
            new(operations, "عملیات شبانه", "OPERATIONS", rootUnit, "team", true)
        ],
        [
            new(hrPosition, hr, "کارشناس منابع انسانی", "HR-READER", 2, true, hrRole),
            new(financePosition, finance, "حسابرس مالی", "FINANCE-AUDITOR", 1, true, financeRole),
            new(operatorPosition, operations, "کارشناس شیفت شب", "NIGHT-OPERATOR", 1, true, operatorRole)
        ],
        [
            new(Guid.NewGuid(), hrPosition, Reader, started, ended),
            new(Guid.NewGuid(), financePosition, Auditor, started, ended),
            new(Guid.NewGuid(), operatorPosition, Operator, started, ended)
        ],
        [new(Guid.NewGuid(), 1, actor, SeedReason, now, "template.bootstrap")],
        [new(hrRole, "خوانندهٔ منابع انسانی", "HR-READER"), new(financeRole, "حسابرس مالی", "FINANCE-AUDITOR"), new(operatorRole, "عامل شیفت شب", "NIGHT-OPERATOR")]);
        database.Put("organization", tenant.Id.ToString(), chart);
        database.Put("organization.history", $"{tenant.Id}:1", chart);
        database.Emit($"organization:{tenant.Id}:1", "OrganizationChanged", new { TenantId = tenant.Id, Revision = 1, Actor = actor, Operation = "template.bootstrap" }, now);

        var office = SaveCalendar(tenant.Id, "example-office", "ساعت کاری آموزشی", "work",
            [.. new[] { 6, 0, 1, 2, 3 }.Select(day => new OrganizationWeeklyWindow(day, 480, 960)), new(4, 480, 720)],
            [new(new(2026, 10, 20), "closed", [], "تعطیلی آموزشی؛ ادعای تعطیل رسمی نیست."),
             new(new(2026, 10, 21), "closed", [], "تعطیلی آموزشی هم‌زمان با پنجرهٔ انتشار داده."),
             new(new(2026, 10, 24), "open", [new(600, 720)], "روز کاری استثنایی آموزشی فقط ۱۰ تا ۱۲.")], actor);
        var night = SaveCalendar(tenant.Id, "example-night", "شیفت شب آموزشی", "work", [new(1, 1320, 360)],
            [new(new(2026, 10, 27), "closed", [], "تعطیلی آموزشی روز مقصد؛ ادامهٔ شیفت روز قبل هم مسدود می‌شود.")], actor);
        var data = SaveCalendar(tenant.Id, "example-finance-data", "پنجرهٔ انتشار دادهٔ مالی آموزشی", "data", [new(1, 540, 660), new(3, 540, 660)], [], actor);

        Draft(tenant.Id, "خواندن ساختار توسط نقش منابع انسانی در واحد خودش", "role", "organization.read", "Permit", hrRole, hr, null, "working-time", actor);
        Draft(tenant.Id, "خواندن ساختار فقط در ساعت کاری و غیرتعطیل", "time-guard", "organization.read", "Deny", null, null, office, "working-time", actor);
        Draft(tenant.Id, "مشاهدهٔ مالی توسط حسابرس واحد مالی", "role", "billing.read", "Permit", financeRole, finance, null, "working-time", actor);
        Draft(tenant.Id, "اطلاعات مالی فقط در پنجرهٔ انتشار داده", "time-guard", "billing.read", "Deny", null, null, data, "working-time", actor);
        Draft(tenant.Id, "اطلاعات مالی فقط در روز غیرتعطیل", "time-guard", "billing.read", "Deny", null, null, office, "non-holiday", actor);
        Draft(tenant.Id, "عملیات مالی آموزشی توسط حسابرس", "role", "billing.write", "Permit", financeRole, finance, null, "working-time", actor);
        Draft(tenant.Id, "عملیات مالی آموزشی فقط در روز غیرتعطیل؛ بدون شرط ساعت", "time-guard", "billing.write", "Deny", null, null, office, "non-holiday", actor);
        Draft(tenant.Id, "ویرایش ساختار توسط عامل شیفت شب", "role", "organization.write", "Permit", operatorRole, operations, null, "working-time", actor);
        Draft(tenant.Id, "ویرایش ساختار فقط در شیفت شب", "time-guard", "organization.write", "Deny", null, null, night, "working-time", actor);
        var marker = new TemplateOrganizationMarker(tenant.Id, Version, actor, now);
        database.Put(MarkerKind, TenantKey, marker);
        database.Put("organization.example.scenarios.v1", tenant.Id.ToString(), Scenarios);
        database.Emit("organization-example:" + tenant.Id, "TemplateOrganizationCreated", new { TenantId = tenant.Id, Version, Actor = actor, Reason = reason }, now);
        return ReadCore();
    });

    private Guid SaveCalendar(Guid tenant, string code, string name, string kind, OrganizationWeeklyWindow[] weekly,
        OrganizationDateException[] exceptions, string actor)
    {
        var state = calendars.Read(tenant);
        var next = calendars.Save(tenant, new(state.Revision, null, code, name, kind, "Asia/Tehran", new(2026, 1, 1), new(2027, 12, 31),
            weekly, exceptions, true, "دادهٔ مصنوعی آموزشی سازمان الگو؛ تقویم رسمی نیست.", SeedReason), actor);
        return next.Calendars.Single(x => x.Code == code).Id;
    }

    private void Draft(Guid tenant, string name, string template, string permission, string effect, Guid? role, Guid? unit,
        Guid? calendar, string mode, string actor)
    {
        var state = policies.Read(tenant);
        policies.Draft(tenant, new(state.Revision, name, template, permission, effect, role, unit, null, null, null, null,
            SeedReason, calendar, mode, true), actor);
    }

    public TemplateOrganizationScenarioResult[] EvaluateScenarios() => database.ReadSnapshot(() =>
    {
        var view = ReadCore();
        var scenarios = database.Read<TemplateOrganizationScenario[]>("organization.example.scenarios.v1", view.Tenant.Id.ToString())
            ?? throw new InvalidOperationException("سناریوهای مرجع سازمان الگو در دسترس نیستند.");
        return scenarios.Select(scenario =>
        {
            var result = policies.EvaluateDraftsForSimulation(view.Tenant.Id, scenario.Subject, scenario.Permission, scenario.At);
            return new TemplateOrganizationScenarioResult(scenario, result, result.Decision == scenario.ExpectedDecision);
        }).ToArray();
    });

    private static DateTimeOffset At(string value) => DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
    private static TemplateOrganizationScenario Scenario(string key, string title, string subject, string permission, string localTime, string expected, string lesson)
        => new(key, title, subject, permission, At(localTime + "+03:30"), expected, lesson);

    private static readonly TemplateOrganizationLesson[] Lessons =
    [
        new("structure", "عضو، نقش و سمت سه مفهوم جدا هستند", "عضو به سمت دارای ظرفیت انتصاب می‌گیرد؛ سمت به نقش و واحد متصل است. صرف عضویت، مجوز خواندن یا نوشتن نمی‌دهد.", "Organization"),
        new("calendar", "تعریف تقویم و پنجرهٔ داده در سازمان", "روزهای هفته، ساعت محلی، منطقهٔ زمانی، تعطیلی و استثنا در سازمان تعریف می‌شوند؛ سیاست فقط به شناسهٔ آن‌ها ارجاع می‌دهد.", "Organization"),
        new("guard", "فقط در ساعت کاری یعنی محدودیت اجباری", "قانون محدودیت زمانی به‌تنهایی مجوز نمی‌دهد و بیرون پنجره حتی مجوز مستقیم را می‌بندد. نقش یا مجوز پایه همچنان لازم است.", "Governance"),
        new("holiday", "تعطیلات و روز استثنایی", "تعطیلی، تمام روز محلی را می‌بندد. روز باز استثنایی ساعت معمول و ادامهٔ شیفت قبل را با بازهٔ خودش جایگزین می‌کند.", "Organization"),
        new("night", "شیفت عبوری از نیمه‌شب", "دوشنبه ۲۲ تا سه‌شنبه ۶ یک شیفت است؛ تعطیلی روز مقصد ادامهٔ شیفت را هم می‌بندد. پایان بازه شامل دسترسی نیست.", "Organization"),
        new("data-window", "دادهٔ آمادهٔ انتشار", "داشتن نقش مالی کافی نیست: داده فقط دوشنبه و چهارشنبه از ۹ تا ۱۱ در پنجرهٔ انتشار در دسترس است.", "Governance"),
        new("non-holiday", "غیرتعطیل با ساعت کاری یکسان نیست", "شرط روز غیرتعطیل به‌تنهایی ساعت را محدود نمی‌کند؛ برای ساعت کار باید working-time انتخاب شود. چند محدودیت اجباری باید هم‌زمان برقرار باشند.", "Governance"),
        new("freshness", "پوشش معلوم تقویم", "خارج بازهٔ پوشش، موتور تعطیلات یا ساعت کار را حدس نمی‌زند و دسترسی وابسته به تقویم را متوقف می‌کند.", "Organization"),
        new("review", "آزمون پیش از انتشار مستقل", "سناریوها با دادهٔ ذخیره‌شده و موتور مشترک اجرا می‌شوند. انتشار واقعی توسط مدیر دوم و ثبت ممیزی، مرحله‌ای جدا از پیش‌نمایش آموزشی است.", "Governance")
    ];

    private static readonly TemplateOrganizationScenario[] Scenarios =
    [
        Scenario("before-opening", "یک دقیقه پیش از شروع ساعت کار", Reader, "organization.read", "2026-10-17T07:59:00", "Deny", "guard"),
        Scenario("opening-inclusive", "لحظهٔ شروع ساعت کار شامل دسترسی است", Reader, "organization.read", "2026-10-17T08:00:00", "Permit", "guard"),
        Scenario("before-closing", "یک دقیقه پیش از پایان ساعت کار", Reader, "organization.read", "2026-10-17T15:59:00", "Permit", "guard"),
        Scenario("closing-exclusive", "لحظهٔ پایان شامل دسترسی نیست", Reader, "organization.read", "2026-10-17T16:00:00", "Deny", "guard"),
        Scenario("short-thursday", "پنجشنبه ساعت کوتاه‌تر دارد", Reader, "organization.read", "2026-10-22T11:59:00", "Permit", "calendar"),
        Scenario("thursday-closed", "پنجشنبه پس از ساعت دوازده", Reader, "organization.read", "2026-10-22T12:00:00", "Deny", "calendar"),
        Scenario("weekend", "جمعهٔ غیرکاری", Reader, "organization.read", "2026-10-23T10:00:00", "Deny", "holiday"),
        Scenario("holiday", "تعطیلی آموزشی وسط هفته", Reader, "organization.read", "2026-10-20T10:00:00", "Deny", "holiday"),
        Scenario("exception-before", "روز استثنایی ساعت معمول را جایگزین می‌کند", Reader, "organization.read", "2026-10-24T09:00:00", "Deny", "holiday"),
        Scenario("exception-open", "روز استثنایی از ساعت ده باز است", Reader, "organization.read", "2026-10-24T10:00:00", "Permit", "holiday"),
        Scenario("no-role", "عضو واحد مالی نقش منابع انسانی ندارد", Auditor, "organization.read", "2026-10-17T10:00:00", "NotApplicable", "structure"),
        Scenario("inactive-member", "عضو غیرفعال حتی در ساعت مجاز رد می‌شود", Inactive, "organization.read", "2026-10-17T10:00:00", "Deny", "structure"),
        Scenario("manual-grant-inside", "مجوز مستقیم داخل پنجره", GrantHolder, "organization.read", "2026-10-17T10:00:00", "Permit", "guard"),
        Scenario("manual-grant-outside", "مجوز مستقیم محدودیت زمانی را دور نمی‌زند", GrantHolder, "organization.read", "2026-10-17T17:00:00", "Deny", "guard"),
        Scenario("data-open", "حسابرس در پنجرهٔ انتشار داده", Auditor, "billing.read", "2026-10-19T09:00:00", "Permit", "data-window"),
        Scenario("data-closed", "ساعت کاری سازمان ولی خارج پنجرهٔ داده", Auditor, "billing.read", "2026-10-19T11:00:00", "Deny", "data-window"),
        Scenario("data-holiday", "پنجرهٔ داده باز است ولی تقویم کاری تعطیل است", Auditor, "billing.read", "2026-10-21T10:00:00", "Deny", "data-window"),
        Scenario("non-holiday-evening", "شرط غیرتعطیل به‌تنهایی ساعت عصر را نمی‌بندد", Auditor, "billing.write", "2026-10-17T20:00:00", "Permit", "non-holiday"),
        Scenario("non-holiday-closed", "شرط غیرتعطیل روز جمعه را می‌بندد", Auditor, "billing.write", "2026-10-23T20:00:00", "Deny", "non-holiday"),
        Scenario("night-start", "شروع شیفت دوشنبه‌شب", Operator, "organization.write", "2026-10-19T22:00:00", "Permit", "night"),
        Scenario("night-spill", "ادامهٔ شیفت در بامداد روز بعد", Operator, "organization.write", "2026-10-20T02:00:00", "Permit", "night"),
        Scenario("night-end", "پایان شیفت در ساعت شش", Operator, "organization.write", "2026-10-20T06:00:00", "Deny", "night"),
        Scenario("night-holiday", "تعطیلی روز مقصد ادامهٔ شیفت را می‌بندد", Operator, "organization.write", "2026-10-27T02:00:00", "Deny", "night"),
        Scenario("unknown-coverage", "تقویم خارج از پوشش قابل اتکا نیست", Reader, "organization.read", "2028-01-01T10:00:00", "Indeterminate", "freshness")
    ];
}
