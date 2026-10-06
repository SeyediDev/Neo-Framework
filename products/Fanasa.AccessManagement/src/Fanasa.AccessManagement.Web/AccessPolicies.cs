using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Organization;
using Fanasa.AccessManagement.Web.Persistence;

namespace Fanasa.AccessManagement.Web.Governance;

public sealed record AccessPattern(Guid Id, string Name, string Template, string Permission, string Effect, Guid? RoleId, Guid? UnitId,
    DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, int? UtcStartHour, int? UtcEndHour, string Status, string CreatedBy, string? PublishedBy, string Reason);
public sealed record PatternCommand(long ExpectedRevision, string Name, string Template, string Permission, string Effect, Guid? RoleId, Guid? UnitId,
    DateTimeOffset? StartsAt, DateTimeOffset? EndsAt, int? UtcStartHour, int? UtcEndHour, string Reason);
public sealed record PolicyState(Guid TenantId, long Revision, string Algorithm, AccessPattern[] Patterns);
public sealed record AccessDecision(string Decision, string Reason, Guid[] Policies, string[] Obligations, string[] Advice);

/// <summary>Typed XACML-inspired profile, not a general XML XACML interpreter.</summary>
public sealed class AccessPolicyStore(FabricDatabase database, PersistentAccessManagement access)
{
    private void Tenant(Guid tenant)
    {
        if (!access.GetTenants().Any(x => x.Id == tenant && x.IsActive)) throw new KeyNotFoundException();
    }
    public PolicyState Read(Guid tenant) { Tenant(tenant); return database.Read<PolicyState>("access.policy", tenant.ToString()) ?? new(tenant, 0, "deny-overrides", []); }
    public PolicyState Draft(Guid tenant, PatternCommand command, string actor) => database.Transaction(() =>
    {
        var state = Read(tenant); Revision(state, command.ExpectedRevision); Label(command.Name, 200); Label(command.Reason, 1000); Label(actor, 200);
        if (command.Template is not ("role" or "member-window") || command.Effect is not ("Permit" or "Deny") || !TenantGrant.Permissions.Contains(command.Permission)) throw new ArgumentException("Unsupported pattern.");
        if (command.StartsAt.HasValue && command.EndsAt.HasValue && command.StartsAt >= command.EndsAt) throw new ArgumentException("Invalid validity window.");
        if (command.UtcStartHour.HasValue != command.UtcEndHour.HasValue || command.UtcStartHour is < 0 or > 23 || command.UtcEndHour is < 0 or > 24 || command.UtcStartHour == command.UtcEndHour && command.UtcStartHour.HasValue) throw new ArgumentException("Invalid UTC daily window.");
        var chart = database.Read<OrgState>("organization", tenant.ToString());
        if (command.Template == "role" && (!command.RoleId.HasValue || chart?.Roles?.Any(x => x.Id == command.RoleId) != true)) throw new ArgumentException("A valid organizational role is required.");
        if (command.Template == "member-window" && command.RoleId.HasValue) throw new ArgumentException("Member-window has no role condition.");
        if (command.Template == "member-window" && !command.StartsAt.HasValue && !command.EndsAt.HasValue && !command.UtcStartHour.HasValue) throw new ArgumentException("Member-window requires a time condition.");
        if (command.UnitId.HasValue && chart?.Units.Any(x => x.Id == command.UnitId && x.Active) != true) throw new ArgumentException("Invalid unit condition.");
        if (state.Patterns.Length >= 200) throw new InvalidOperationException("Retain at most 200 pattern versions per tenant.");
        var pattern = new AccessPattern(Guid.NewGuid(), command.Name.Trim(), command.Template, command.Permission, command.Effect, command.RoleId, command.UnitId, command.StartsAt, command.EndsAt, command.UtcStartHour, command.UtcEndHour, "draft", actor, null, command.Reason.Trim());
        return Save(state with { Revision = state.Revision + 1, Patterns = [..state.Patterns, pattern] }, actor, "PolicyDrafted");
    });
    public PolicyState Transition(Guid tenant, Guid id, long revision, string operation, string actor, string reason) => database.Transaction(() =>
    {
        var state = Read(tenant); Revision(state, revision); Label(actor, 200); Label(reason, 1000);
        var pattern = state.Patterns.SingleOrDefault(x => x.Id == id) ?? throw new KeyNotFoundException();
        if (operation == "publish" && pattern.Status == "draft")
        {
            if (actor == pattern.CreatedBy) throw new InvalidOperationException("A different administrator must publish the pattern.");
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

    public AccessDecision Evaluate(Guid tenant, string subject, string permission, DateTimeOffset now)
    {
        var state = Read(tenant);
        if (!TenantGrant.Permissions.Contains(permission)) return new("NotApplicable", "unsupported-action", [], [], []);
        if (!access.GetUsers(tenant).Any(x => x.KeycloakSubject == subject && x.IsActive)) return new("Deny", "inactive-membership", [], [], []);
        var chart = database.Read<OrgState>("organization", tenant.ToString());
        var matched = new List<AccessPattern>(); var errorPermit = false; var errorDeny = false;
        foreach (var pattern in state.Patterns.Where(x => x.Status == "published" && x.Permission == permission))
        {
            if (pattern.StartsAt.HasValue && now < pattern.StartsAt || pattern.EndsAt.HasValue && now >= pattern.EndsAt) continue;
            if (pattern.UtcStartHour.HasValue && pattern.UtcEndHour.HasValue)
            {
                var hour = now.UtcDateTime.TimeOfDay.TotalHours; var start = pattern.UtcStartHour.Value; var end = pattern.UtcEndHour.Value;
                if (!(start < end ? hour >= start && hour < end : hour >= start || hour < end)) continue;
            }
            // Missing referenced attributes are errors; absence of an appointment is simply no match.
            if (pattern.RoleId.HasValue && chart?.Roles?.Any(x => x.Id == pattern.RoleId) != true || pattern.UnitId.HasValue && chart?.Units.Any(x => x.Id == pattern.UnitId && x.Active) != true)
            { if (pattern.Effect == "Deny") errorDeny = true; else errorPermit = true; continue; }
            if (pattern.RoleId.HasValue || pattern.UnitId.HasValue)
            {
                if (chart is null || !chart.Appointments.Any(a => a.Subject == subject && a.From <= now && (!a.To.HasValue || a.To > now)
                    && chart.Positions.Any(p => p.Id == a.PositionId && p.Active && (!pattern.RoleId.HasValue || p.RoleId == pattern.RoleId) && (!pattern.UnitId.HasValue || p.UnitId == pattern.UnitId)
                        && chart.Units.Any(u => u.Id == p.UnitId && u.Active)))) continue;
            }
            matched.Add(pattern);
        }
        var deny = matched.Where(x => x.Effect == "Deny").ToArray();
        if (deny.Length > 0) return new("Deny", "explicit-deny", deny.Select(x => x.Id).ToArray(), [], []);
        var permits = matched.Where(x => x.Effect == "Permit").ToArray();
        var manual = access.GetGrants(subject).Any(x => x.TenantId == tenant && x.Permission == permission && (!x.ExpiresAt.HasValue || x.ExpiresAt > now));
        if (errorDeny || (errorPermit && permits.Length == 0 && !manual)) return new("Indeterminate", "missing-required-attribute", [], [], ["review-policy-references"]);
        if (permits.Length > 0 || manual) return new("Permit", manual ? "grant-or-policy" : "published-policy", permits.Select(x => x.Id).ToArray(), permits.Length > 0 ? ["audit"] : [], []);
        return new("NotApplicable", "no-applicable-policy-or-grant", [], [], []);
    }
    public bool Authorize(Guid tenant, string subject, string permission, DateTimeOffset now) => database.Transaction(() =>
    {
        var decision = Evaluate(tenant, subject, permission, now);
        if (decision.Decision != "Permit") return false;
        if (decision.Obligations.Contains("audit")) database.Transaction(() =>
        {
            database.Emit("decision:" + Guid.NewGuid(), "AccessDecisionApplied", new { TenantId = tenant, Actor = subject, Permission = permission, decision.Decision, decision.Policies }, now); return true;
        });
        return true;
    });
}
