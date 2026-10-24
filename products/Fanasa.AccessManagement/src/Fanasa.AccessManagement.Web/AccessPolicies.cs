using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Organization;
using Fanasa.AccessManagement.Web.Persistence;

namespace Fanasa.AccessManagement.Web.Governance;

public sealed record AccessPattern(Guid Id, string Name, string Template, string Permission, string Effect, Guid? RoleId, Guid? UnitId,
    DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, int? UtcStartHour, int? UtcEndHour, string Status, string CreatedBy, string? PublishedBy, string Reason,
    Guid? CalendarId = null, string CalendarMode = "working-time", bool Enforce = false,
    string? AttributeName = null, string? AttributeValue = null, Guid? PipSourceId = null, bool RequireFreshPip = true);
public sealed record PatternCommand(long ExpectedRevision, string Name, string Template, string Permission, string Effect, Guid? RoleId, Guid? UnitId,
    DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, int? UtcStartHour, int? UtcEndHour, string Reason,
    Guid? CalendarId = null, string CalendarMode = "working-time", bool Enforce = false,
    string? AttributeName = null, string? AttributeValue = null, Guid? PipSourceId = null, bool RequireFreshPip = true);
public sealed record PolicyState(Guid TenantId, long Revision, string Algorithm, AccessPattern[] Patterns);
public sealed record PipDecisionEvidence(Guid SourceId, string Status, string? SourceVersion = null,
    DateTimeOffset? ObservedAt = null, DateTimeOffset? FreshUntil = null, string? Error = null);
public sealed record AccessDecision(string Decision, string Reason, Guid[] Policies, string[] Obligations, string[] Advice,
    OrganizationCalendarEvaluation[]? CalendarChecks = null, PipDecisionEvidence? Pip = null);

/// <summary>Typed XACML-inspired profile, not a general XML XACML interpreter.</summary>
public sealed class AccessPolicyStore(FabricDatabase database, PersistentAccessManagement access, OrganizationCalendarStore? calendars = null, PipStore? pip = null)
{
    private readonly OrganizationCalendarStore calendars = calendars ?? new(database, access);
    private readonly PipStore? pip = pip;
    private readonly IPolicyInformationPoint? informationPoint = pip;
    private void Tenant(Guid tenant)
    {
        if (!access.GetTenants().Any(x => x.Id == tenant && x.IsActive)) throw new KeyNotFoundException();
    }
    public PolicyState Read(Guid tenant) { Tenant(tenant); return database.Read<PolicyState>("access.policy", tenant.ToString()) ?? new(tenant, 0, "deny-overrides", []); }
    public PolicyState Draft(Guid tenant, PatternCommand command, string actor) => database.Transaction(() =>
    {
        var state = Read(tenant); Revision(state, command.ExpectedRevision); Label(command.Name, 200); Label(command.Reason, 1000); Label(actor, 200);
        if (command.Template is not ("role" or "member-window" or "time-guard" or "attribute") || command.Effect is not ("Permit" or "Deny") || !TenantGrant.Permissions.Contains(command.Permission)) throw new ArgumentException("Unsupported pattern.");
        if (command.StartsAt.HasValue && command.EndsAt.HasValue && command.StartsAt >= command.EndsAt) throw new ArgumentException("Invalid validity window.");
        if (command.UtcStartHour.HasValue != command.UtcEndHour.HasValue || command.UtcStartHour is < 0 or > 23 || command.UtcEndHour is < 0 or > 24 || command.UtcStartHour == command.UtcEndHour && command.UtcStartHour.HasValue) throw new ArgumentException("Invalid UTC daily window.");
        var chart = database.Read<OrgState>("organization", tenant.ToString());
        if (command.Template == "role" && !command.RoleId.HasValue || command.RoleId.HasValue && chart?.Roles?.Any(x => x.Id == command.RoleId) != true)
            throw new ArgumentException("A valid organizational role from the same organization is required.");
        if (command.Template == "member-window" && command.RoleId.HasValue) throw new ArgumentException("Member-window has no role condition.");
        if (command.Template == "member-window" && !command.StartsAt.HasValue && !command.EndsAt.HasValue && !command.UtcStartHour.HasValue && !command.CalendarId.HasValue) throw new ArgumentException("Member-window requires a time condition.");
        if (command.Template == "attribute")
        {
            Label(command.AttributeName, 100); Label(command.AttributeValue, 2000);
            if (command.RoleId.HasValue || command.UnitId.HasValue || !command.PipSourceId.HasValue || command.PipSourceId == Guid.Empty) throw new ArgumentException("Attribute pattern requires a PIP source and has no role or unit condition.");
            if (!command.RequireFreshPip) throw new ArgumentException("PIP attribute patterns always require fresh evidence.");
            if (pip is null) throw new InvalidOperationException("PIP is not configured.");
            var source = pip.ReadSource(tenant, command.PipSourceId!.Value);
            ValidateAttributeComparison(source, command.AttributeName!, command.AttributeValue!);
            if (source.Educational && command.Enforce) throw new ArgumentException("Educational PIP data is available only in simulation.");
        }
        else if (command.AttributeName is not null || command.AttributeValue is not null || command.PipSourceId.HasValue)
            throw new ArgumentException("PIP attributes are only valid for an attribute pattern.");
        if (command.CalendarMode is not ("working-time" or "non-holiday")) throw new ArgumentException("Unsupported calendar mode.");
        if (command.Template == "time-guard" && (command.Effect != "Deny" || !command.CalendarId.HasValue)) throw new ArgumentException("A mandatory time guard requires a calendar and Deny effect; it does not grant access.");
        if (command.CalendarId.HasValue)
        {
            var calendar = calendars.Read(tenant).Calendars.SingleOrDefault(x => x.Id == command.CalendarId && x.Active)
                ?? throw new ArgumentException("An active calendar from the same organization is required.");
            if (command.CalendarMode == "non-holiday" && calendar.Kind != "work") throw new ArgumentException("Non-holiday mode requires a work calendar.");
        }
        else if (command.CalendarMode != "working-time") throw new ArgumentException("Calendar mode requires a calendar.");
        if (command.UnitId.HasValue && chart?.Units.Any(x => x.Id == command.UnitId && x.Active) != true) throw new ArgumentException("Invalid unit condition.");
        if (state.Patterns.Length >= 200) throw new InvalidOperationException("Retain at most 200 pattern versions per tenant.");
        var pattern = new AccessPattern(Guid.NewGuid(), command.Name.Trim(), command.Template, command.Permission, command.Effect, command.RoleId, command.UnitId, command.StartsAt, command.EndsAt, command.UtcStartHour, command.UtcEndHour, "draft", actor, null, command.Reason.Trim(), command.CalendarId, command.CalendarMode, command.Enforce, command.AttributeName?.Trim(), command.AttributeValue, command.PipSourceId, command.RequireFreshPip);
        return Save(state with { Revision = state.Revision + 1, Patterns = [..state.Patterns, pattern] }, actor, "PolicyDrafted");
    });
    public PolicyState Transition(Guid tenant, Guid id, long revision, string operation, string actor, string reason) => database.Transaction(() =>
    {
        var state = Read(tenant); Revision(state, revision); Label(actor, 200); Label(reason, 1000);
        var pattern = state.Patterns.SingleOrDefault(x => x.Id == id) ?? throw new KeyNotFoundException();
        if (operation == "publish" && pattern.Status == "draft")
        {
            if (actor == pattern.CreatedBy) throw new InvalidOperationException("A different administrator must publish the pattern.");
            if (pattern.Template == "attribute")
            {
                if (pip is null || !pattern.PipSourceId.HasValue || !pattern.RequireFreshPip) throw new InvalidOperationException("PIP policy references must be reviewed before publication.");
                var source = pip.ReadSource(tenant, pattern.PipSourceId.Value);
                if (!source.Enabled) throw new InvalidOperationException("Enable and verify the PIP source before publication.");
                ValidateAttributeComparison(source, pattern.AttributeName!, pattern.AttributeValue!);
                if (source.Educational && pattern.Enforce) throw new InvalidOperationException("Educational PIP data cannot be enforced in runtime authorization.");
            }
            pattern = pattern with { Status = "published", PublishedBy = actor, Reason = reason.Trim() };
        }
        else if (operation == "retire" && pattern.Status != "retired") pattern = pattern with { Status = "retired", Reason = reason.Trim() };
        else throw new InvalidOperationException("Invalid policy transition.");
        return Save(state with { Revision = state.Revision + 1, Patterns = state.Patterns.Select(x => x.Id == id ? pattern : x).ToArray() }, actor, operation == "publish" ? "PolicyPublished" : "PolicyRetired");
    });
    private PolicyState Save(PolicyState state, string actor, string kind)
    {
        database.Put("access.policy", state.TenantId.ToString(), state);
        database.Put("access.policy.history", $"{state.TenantId}:{state.Revision}", state);
        database.Emit("policy:" + Guid.NewGuid(), kind, new { state.TenantId, state.Revision, Actor = actor }, DateTimeOffset.UtcNow);
        return state;
    }
    private static void Revision(PolicyState state, long revision) { if (state.Revision != revision) throw new InvalidOperationException("Policy version changed; reload."); }
    internal static void Label(string? value, int length) { if (string.IsNullOrWhiteSpace(value) || value.Length > length) throw new ArgumentException("A valid label is required."); }

    private static PipAttributeDefinition ValidateAttributeComparison(PipSourceDefinition source, string name, string value)
    {
        var definitions = PipSchema.Definitions(source); PipSchema.Validate(definitions);
        var definition = definitions.SingleOrDefault(x => x.Name == name) ?? throw new ArgumentException("The attribute must be declared by the selected PIP source.");
        try { PipSchema.ValidateValue(definition, value); }
        catch (InvalidDataException) { throw new ArgumentException("The comparison value does not match the PIP attribute type."); }
        return definition;
    }

    private static PipDecisionEvidence Evidence(Guid sourceId, PipLookup lookup) => new(sourceId, lookup.Status,
        lookup.Snapshot?.SourceVersion, lookup.Snapshot?.ObservedAt, lookup.Snapshot?.FreshUntil, lookup.Error);

    public AccessDecision Evaluate(Guid tenant, string subject, string permission, DateTimeOffset now) => EvaluateSnapshot(tenant, subject, permission, now, false, false);
    public AccessDecision EvaluateLive(Guid tenant, string subject, string permission, DateTimeOffset now) => EvaluateSnapshot(tenant, subject, permission, now, true, false);
    // The read-only HR3 bridge preserves its pre-calendar legacy policy boundary.
    // Calendar-linked rules are additional opt-in restrictions; simulation-only rules
    // must not silently change an existing HR3 native-grant witness.
    public AccessDecision EvaluateForReadOnlyHost(Guid tenant, string subject, string permission, DateTimeOffset now, bool calendarPoliciesEnabled) =>
        EvaluateSnapshot(tenant, subject, permission, now, false, false, calendarPoliciesEnabled);
    // Preview uses the same evaluator over drafts without publishing or granting a live permission.
    public AccessDecision EvaluateDraftsForSimulation(Guid tenant, string subject, string permission, DateTimeOffset now) => EvaluateSnapshot(tenant, subject, permission, now, false, true);
    private AccessDecision EvaluateSnapshot(Guid tenant, string subject, string permission, DateTimeOffset now, bool live, bool drafts, bool? calendarPoliciesEnabled = null) =>
        database.IsInTransaction ? EvaluateCore(tenant, subject, permission, now, live, drafts, calendarPoliciesEnabled)
            : database.ReadSnapshot(() => EvaluateCore(tenant, subject, permission, now, live, drafts, calendarPoliciesEnabled));

    private AccessDecision EvaluateCore(Guid tenant, string subject, string permission, DateTimeOffset now, bool live, bool drafts, bool? calendarPoliciesEnabled)
    {
        var state = Read(tenant);
        if (!TenantGrant.Permissions.Contains(permission)) return new("NotApplicable", "unsupported-action", [], [], []);
        if (!access.GetUsers(tenant).Any(x => x.KeycloakSubject == subject && x.IsActive)) return new("Deny", "inactive-membership", [], [], []);
        var chart = database.Read<OrgState>("organization", tenant.ToString());
        var matched = new List<AccessPattern>(); var guards = new List<Guid>(); var checks = new List<OrganizationCalendarEvaluation>();
        PipDecisionEvidence? pipEvidence = null; var pipError = false;
        var errorPermit = false; var errorDeny = false; var deniedByTime = false;
        foreach (var pattern in state.Patterns.Where(x => (x.Status == "published" || drafts && x.Status == "draft") && x.Permission == permission && (!live || x.Enforce)
            && (calendarPoliciesEnabled is null || !(x.CalendarId.HasValue || x.Template == "time-guard" || x.CalendarMode != "working-time")
                || calendarPoliciesEnabled.Value && x.Enforce)))
        {
            var guard = pattern.Template == "time-guard";
            if (pattern.Template is not ("role" or "member-window" or "time-guard" or "attribute") || pattern.Effect is not ("Permit" or "Deny")
                || guard && (pattern.Effect != "Deny" || !pattern.CalendarId.HasValue)
                || pattern.StartsAt.HasValue && pattern.EndsAt.HasValue && pattern.StartsAt >= pattern.EndsAt
                || pattern.UtcStartHour.HasValue != pattern.UtcEndHour.HasValue
                || pattern.UtcStartHour is < 0 or > 23 || pattern.UtcEndHour is < 0 or > 24
                || pattern.UtcStartHour.HasValue && pattern.UtcStartHour == pattern.UtcEndHour
                || pattern.CalendarMode is not ("working-time" or "non-holiday")
                || pattern.Template == "role" && !pattern.RoleId.HasValue
                || pattern.Template == "member-window" && (pattern.RoleId.HasValue
                    || !pattern.StartsAt.HasValue && !pattern.EndsAt.HasValue && !pattern.UtcStartHour.HasValue && !pattern.CalendarId.HasValue)
                || pattern.Template == "attribute" && (!pattern.RequireFreshPip || string.IsNullOrWhiteSpace(pattern.AttributeName) || pattern.AttributeName.Length > 100 || pattern.AttributeValue is null || pattern.PipSourceId is null || pattern.RoleId.HasValue || pattern.UnitId.HasValue)
                || pattern.CalendarMode == "non-holiday" && !pattern.CalendarId.HasValue)
                return new("Indeterminate", "invalid-policy-data", [pattern.Id], [], ["review-policy-references"], checks.ToArray());
            var inTime = (!pattern.StartsAt.HasValue || now >= pattern.StartsAt) && (!pattern.EndsAt.HasValue || now < pattern.EndsAt);
            if (pattern.UtcStartHour.HasValue && pattern.UtcEndHour.HasValue)
            {
                var hour = now.UtcDateTime.TimeOfDay.TotalHours; var start = pattern.UtcStartHour.Value; var end = pattern.UtcEndHour.Value;
                inTime &= start < end ? hour >= start && hour < end : hour >= start || hour < end;
            }
            if (!guard && !inTime) continue;
            // Missing referenced attributes are errors; absence of an appointment is simply no match.
            if (pattern.RoleId.HasValue && chart?.Roles?.Any(x => x.Id == pattern.RoleId) != true || pattern.UnitId.HasValue && chart?.Units.Any(x => x.Id == pattern.UnitId && x.Active) != true)
            { if (pattern.Effect == "Deny") errorDeny = true; else errorPermit = true; continue; }
            if (pattern.RoleId.HasValue || pattern.UnitId.HasValue)
            {
                if (chart is null || !chart.Appointments.Any(a => a.Subject == subject && a.From <= now && (!a.To.HasValue || a.To > now)
                    && chart.Positions.Any(p => p.Id == a.PositionId && p.Active && (!pattern.RoleId.HasValue || p.RoleId == pattern.RoleId) && (!pattern.UnitId.HasValue || p.UnitId == pattern.UnitId)
                        && chart.Units.Any(u => u.Id == p.UnitId && u.Active)))) continue;
            }
            if (pattern.Template == "attribute")
            {
                if (pip is null || informationPoint is null) return new("Indeterminate", "missing-pip", [pattern.Id], [], ["review-policy-references"], checks.ToArray());
                try
                {
                    var source = pip.ReadSource(tenant, pattern.PipSourceId!.Value);
                    // Educational snapshots participate only in the explicit draft simulation path.
                    if (source.Educational && !drafts) continue;
                    var definition = ValidateAttributeComparison(source, pattern.AttributeName!, pattern.AttributeValue!);
                    var lookup = informationPoint.Read(tenant, subject, now, source.Id);
                    if (!pipError) pipEvidence = Evidence(source.Id, lookup);
                    if (!lookup.IsFresh || lookup.Snapshot is null || !lookup.Snapshot.Attributes.TryGetValue(pattern.AttributeName!, out var value))
                    {
                        pipError = true;
                        pipEvidence = Evidence(source.Id, lookup) with { Status = lookup.IsFresh ? "missing-attribute" : lookup.Status,
                            Error = lookup.IsFresh ? "required-policy-attribute-missing" : lookup.Error };
                        if (pattern.Effect == "Deny") errorDeny = true; else errorPermit = true;
                        continue;
                    }
                    if (!PipSchema.ValuesEqual(definition, value, pattern.AttributeValue!)) continue;
                }
                catch (Exception error) when (error is ArgumentException or InvalidDataException or KeyNotFoundException or InvalidOperationException)
                {
                    pipError = true;
                    pipEvidence = new(pattern.PipSourceId!.Value, "invalid", Error: "review-pip-source-and-schema");
                    if (pattern.Effect == "Deny") errorDeny = true; else errorPermit = true;
                    continue;
                }
            }
            if (pattern.CalendarId.HasValue)
            {
                var check = calendars.Evaluate(tenant, pattern.CalendarId.Value, now, pattern.CalendarMode); checks.Add(check);
                if (!check.Known) { if (pattern.Effect == "Deny" || guard) errorDeny = true; else errorPermit = true; continue; }
                inTime &= check.Allowed;
            }
            if (guard)
            {
                guards.Add(pattern.Id);
                if (!inTime) { matched.Add(pattern); deniedByTime = true; }
                continue;
            }
            if (!inTime) continue;
            matched.Add(pattern);
        }
        var deny = matched.Where(x => x.Effect == "Deny").ToArray();
        if (deny.Length > 0) return new("Deny", deniedByTime ? "time-constraint-denied" : "explicit-deny", deny.Select(x => x.Id).ToArray(), [], [], checks.ToArray(), pipEvidence);
        var permits = matched.Where(x => x.Effect == "Permit").ToArray();
        var manual = access.GetGrants(subject).Any(x => x.TenantId == tenant && x.Permission == permission && (!x.ExpiresAt.HasValue || x.ExpiresAt > now));
        if (errorDeny || pipError || (errorPermit && permits.Length == 0 && !manual)) return new("Indeterminate", pipError ? "stale-or-missing-pip" : "missing-required-attribute", guards.ToArray(), [], ["review-policy-references"], checks.ToArray(), pipEvidence);
        if (permits.Length > 0 || manual) return new("Permit", manual ? "grant-or-policy" : drafts ? "draft-or-published-policy" : "published-policy", permits.Select(x => x.Id).Concat(guards).Distinct().ToArray(), permits.Length > 0 || guards.Count > 0 ? ["audit"] : [], [], checks.ToArray(), pipEvidence);
        return new("NotApplicable", "no-applicable-policy-or-grant", guards.ToArray(), [], [], checks.ToArray(), pipEvidence);
    }
    public bool Authorize(Guid tenant, string subject, string permission, DateTimeOffset now) => AuthorizeCore(tenant, subject, permission, () => now, false);
    public bool AuthorizeLive(Guid tenant, string subject, string permission, DateTimeOffset now) => AuthorizeCore(tenant, subject, permission, () => now, true);
    public bool AuthorizeLiveNow(Guid tenant, string subject, string permission, Func<DateTimeOffset> getUtcNow)
    {
        ArgumentNullException.ThrowIfNull(getUtcNow);
        return AuthorizeCore(tenant, subject, permission, getUtcNow, true);
    }
    private bool AuthorizeCore(Guid tenant, string subject, string permission, Func<DateTimeOffset> getUtcNow, bool live) => database.Transaction(() =>
    {
        // Acquire the transaction first: waiting for another SQLite writer must not retain a pre-window-close timestamp.
        var now = getUtcNow();
        var decision = live ? EvaluateLive(tenant, subject, permission, now) : Evaluate(tenant, subject, permission, now);
        if (decision.Decision != "Permit") return false;
        if (decision.Obligations.Contains("audit")) database.Transaction(() =>
        {
            database.Emit("decision:" + Guid.NewGuid(), "AccessDecisionApplied", new { TenantId = tenant, Actor = subject, Permission = permission, decision.Decision, decision.Policies }, now); return true;
        });
        return true;
    });
}
