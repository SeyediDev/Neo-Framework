using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Organization;
using Fanasa.AccessManagement.Web.Persistence;

namespace Fanasa.AccessManagement.Web.Governance;

public sealed record PipEducationalMarker(Guid TenantId, Guid SourceId, Guid PolicyId, string Version);
public sealed record PipEducationalScenario(string Name, string Subject, DateTimeOffset At, string Expected, string Actual, bool Passed);
public sealed record PipEducationalView(PipSourceDefinition Source, string Subject, Guid PolicyId,
    DateTimeOffset ObservedAt, DateTimeOffset FreshUntil, PipEducationalScenario[] Scenarios,
    bool Simulation = true, bool Enforced = false);

/// <summary>Atomic, isolated examples in the explicitly marked teaching tenant. No external request or policy publication.</summary>
public sealed class PipEducationalExample(FabricDatabase database, PersistentAccessManagement access,
    TemplateOrganizationStore templates, AccessPolicyStore policies, PipStore pip, TimeProvider clock)
{
    public const string MarkerKind = "pip.educational.v1";
    public const string Version = "fanasa.pip-example/1";
    public const string Subject = "urn:fanasa:example:pip:reader";
    public const string MissingSubject = "urn:fanasa:example:pip:missing-data";
    public const string Permission = "tenancy.read";
    public const string Endpoint = "https://example.invalid/fanasa/pip/educational";
    private const string PolicyName = "نمونهٔ PIP: مشاهدهٔ اعضا با وضعیت همکاری معتبر";

    public PipEducationalView Ensure(Guid tenantId, string actor, string reason) => database.Transaction(() =>
    {
        AccessPolicyStore.Label(actor, 200); AccessPolicyStore.Label(reason, 1000);
        if (!templates.IsTemplateTenant(tenantId))
            throw new InvalidOperationException("نمونهٔ PIP فقط در سازمان دارای نشان آموزشی ساخته می‌شود.");
        var now = clock.GetUtcNow();
        var marker = database.Read<PipEducationalMarker>(MarkerKind, tenantId.ToString("D"));
        if (marker is not null && (marker.TenantId != tenantId || marker.Version != Version))
            throw new InvalidOperationException("نشان نمونهٔ PIP با این نسخه سازگار نیست؛ دادهٔ موجود تغییر نکرد.");

        var source = marker is not null ? pip.ReadSource(tenantId, marker.SourceId)
            : pip.ListSources(tenantId).SingleOrDefault(x => x.Educational);
        if (source is null)
            source = pip.Register(tenantId, "منابع انسانی آموزشی · دادهٔ مصنوعی", Endpoint, "example.invalid", null,
                maxAgeSeconds: 300, enabled: true, definitions: PipSchema.Default, actor: actor, reason: reason, educational: true);
        if (!source.Educational || !source.Enabled || source.Endpoint != Endpoint || source.SchemaVersion != 1
            || source.MaxAgeSeconds != 300 || source.PollIntervalSeconds != 0 || source.SecretReference is not null
            || !PipSchema.Definitions(source).SequenceEqual(PipSchema.Default))
            throw new InvalidOperationException("منبع نمونهٔ PIP تغییر کرده است؛ نمونهٔ موجود بازنویسی نشد.");

        EnsureMember(tenantId, Subject, "عضو مصنوعی با دادهٔ PIP", actor, reason);
        EnsureMember(tenantId, MissingSubject, "عضو مصنوعی بدون دادهٔ PIP", actor, reason);
        var state = policies.Read(tenantId);
        AccessPattern policy;
        if (marker is null)
        {
            var next = policies.Draft(tenantId, new PatternCommand(state.Revision, PolicyName, "attribute", Permission,
                "Permit", null, null, null, null, null, null, reason, Enforce: false,
                AttributeName: "employment.active", AttributeValue: "true", PipSourceId: source.Id, RequireFreshPip: true), actor);
            policy = next.Patterns[^1];
            marker = new(tenantId, source.Id, policy.Id, Version);
            database.Put(MarkerKind, tenantId.ToString("D"), marker);
        }
        else
        {
            policy = state.Patterns.SingleOrDefault(x => x.Id == marker.PolicyId)
                ?? throw new InvalidOperationException("پیش‌نویس نمونهٔ PIP پیدا نشد؛ سیاست تازه به‌جای آن ساخته نشد.");
            if (policy.Template != "attribute" || policy.Status != "draft" || policy.Enforce || policy.Permission != Permission
                || policy.Effect != "Permit" || policy.PipSourceId != source.Id || !policy.RequireFreshPip
                || policy.AttributeName != "employment.active" || policy.AttributeValue != "true"
                || policy.RoleId.HasValue || policy.UnitId.HasValue || policy.CalendarId.HasValue
                || policy.StartsAt.HasValue || policy.EndsAt.HasValue || policy.UtcStartHour.HasValue || policy.UtcEndHour.HasValue)
                throw new InvalidOperationException("سیاست نمونهٔ PIP تغییر کرده است؛ سیاست موجود بازنویسی یا منتشر نشد.");
        }

        var attributes = new Dictionary<string, string>(StringComparer.Ordinal)
            { ["employment.active"] = "true", ["organization.unit"] = "engineering" };
        var previous = pip.Read(tenantId, Subject, now, source.Id).Snapshot;
        if (previous?.ObservedAt > now) throw new InvalidOperationException("ساعت نمونه نسبت به آخرین داده عقب رفته است.");
        // A repeated call at the same injected clock instant is idempotent, including in deterministic tests.
        var version = previous?.ObservedAt == now ? previous.SourceVersion : "example-" + Guid.NewGuid().ToString("N");
        var hash = PipStore.HashAttributes(tenantId, Subject, 1, attributes, version, now);
        var snapshot = pip.SaveSnapshot(source, Subject, new(tenantId, Subject, 1, attributes, version, null, now, hash), now);
        pip.RecordRun(new(Guid.NewGuid(), source.Id, tenantId, Subject, "succeeded", now, now, snapshot.ContentHash, null, actor, reason));
        database.Emit("pip-example-refresh:" + Guid.NewGuid().ToString("N"), "PipEducationalExampleRefreshed",
            new { TenantId = tenantId, SourceId = source.Id, PolicyId = policy.Id, Actor = actor, Reason = reason, snapshot.SourceVersion }, now);

        PipEducationalScenario Scenario(string name, string subject, DateTimeOffset at, string expected)
        {
            var actual = policies.EvaluateDraftsForSimulation(tenantId, subject, Permission, at).Decision;
            return new(name, subject, at, expected, actual, actual == expected);
        }
        return new PipEducationalView(pip.ReadSource(tenantId, source.Id), Subject, policy.Id, snapshot.ObservedAt, snapshot.FreshUntil,
        [
            Scenario("دادهٔ تازه و همکاری فعال", Subject, now, "Permit"),
            Scenario("پایان اعتبار داده؛ اجازه متوقف می‌شود", Subject, snapshot.FreshUntil, "Indeterminate"),
            Scenario("عضویت فعال بدون دادهٔ مرجع", MissingSubject, now, "Indeterminate")
        ]);
    });

    private void EnsureMember(Guid tenantId, string subject, string name, string actor, string reason)
    {
        var member = access.GetUsers(tenantId).SingleOrDefault(x => x.KeycloakSubject == subject);
        if (member is null)
            access.Audited(() => access.AddUser(new(tenantId, subject, name)), actor, reason, "PipEducationalMemberAdded");
        else if (!member.IsActive)
            throw new InvalidOperationException("عضو مصنوعی نمونه غیرفعال شده است؛ عضویت موجود بازنویسی نشد.");
    }
}
