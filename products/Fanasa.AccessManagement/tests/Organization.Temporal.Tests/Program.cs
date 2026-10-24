using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Governance;
using Fanasa.AccessManagement.Web.Organization;
using Fanasa.AccessManagement.Web.Persistence;
using Fanasa.AccessManagement.Web.Security;
using Microsoft.Data.Sqlite;

if (args.Contains("--preview")) { await TemplatePreview.Run(); return; }

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new InvalidOperationException("FAIL " + name);
    Console.WriteLine("PASS " + name); checks++;
}
void Reject<T>(Action action, string name) where T : Exception
{
    try { action(); }
    catch (T) { Check(true, name); return; }
    throw new InvalidOperationException("Expected " + typeof(T).Name + ": " + name);
}
DateTimeOffset At(string value) => DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
var inside = At("2026-10-17T10:00:00+03:30");
var outside = At("2026-10-17T17:00:00+03:30");

// Use the actual host stores with one on-disk database and the production policy evaluator.
using (var fixture = new Fixture())
{
    Reject<KeyNotFoundException>(() => fixture.Examples.Read(), "template does not appear before bootstrap");
    Check(!fixture.Examples.IsTemplateTenant(Guid.Empty), "empty tenant is not an educational marker");
    var other = fixture.Access.CreateTenant("existing-organization", "Existing organization");
    fixture.Access.AddUser(new(other.Id, "real-existing-member", "Existing member"));
    fixture.Access.SetGrant(new(other.Id, "real-existing-member", "organization.read", null), "existing-admin", "Existing grant");
    var otherTenant = JsonSerializer.Serialize(other);
    var otherMembers = JsonSerializer.Serialize(fixture.Access.GetUsers(other.Id));
    var otherGrants = JsonSerializer.Serialize(fixture.Access.GetGrants("real-existing-member"));
    var view = fixture.Examples.Ensure("actual-seed-operator", "Create requested educational examples");
    Check(view.Tenant.Key == TemplateOrganizationStore.TenantKey && view.Tenant.DisplayName == "سازمان الگو", "template tenant has the requested identity");
    Check(fixture.Examples.IsTemplateTenant(view.Tenant.Id) && !fixture.Examples.IsTemplateTenant(other.Id), "marker binds only its own active tenant");
    Check(view.Marker.CreatedBy == "actual-seed-operator" && view.Marker.Version == TemplateOrganizationStore.Version, "bootstrap records actual actor and recipe version");
    Check(view.Members.Length == 5 && view.Members.All(x => x.KeycloakSubject.StartsWith("urn:fanasa:example:", StringComparison.Ordinal)), "all five members are explicitly synthetic");
    Check(!view.Members.Single(x => x.KeycloakSubject == TemplateOrganizationStore.Inactive).IsActive, "offboarded example is inactive");
    Check(!view.Members.Any(x => x.KeycloakSubject == "actual-seed-operator"), "operator is not silently added as a tenant member");
    Check(view.Chart.Units.Length == 4 && view.Chart.Roles!.Length == 3 && view.Chart.Positions.Length == 3 && view.Chart.Appointments.Length == 3, "example connects units roles positions and appointments");
    Check(view.Chart.Appointments.All(a => view.Chart.Positions.Any(p => p.Id == a.PositionId && p.Active && view.Chart.Units.Any(u => u.Id == p.UnitId && u.Active))), "all appointments resolve to active local structures");
    Check(view.Calendars.Calendars.Length == 3 && view.Calendars.Calendars.Count(x => x.Kind == "data") == 1, "work night and data calendars are persisted separately");
    Check(view.Policies.Patterns.Length == 9 && view.Policies.Patterns.All(x => x.Status == "draft" && x.PublishedBy is null && x.CreatedBy == "actual-seed-operator"), "nine drafts never invent an independent publisher");
    Check(fixture.Access.GetSubscriptions(view.Tenant.Id).Count == 0 && fixture.Access.GetEntitlements(view.Tenant.Id).Count == 0, "examples create no commercial entitlement or subscription");
    Check(fixture.Access.GetGrants("actual-seed-operator").Length == 0, "seed does not grant its operator runtime access");
    Check(fixture.Policies.EvaluateLive(view.Tenant.Id, TemplateOrganizationStore.Reader, "organization.read", inside).Decision == "NotApplicable", "draft examples do not grant live reader access");
    var results = fixture.Examples.EvaluateScenarios();
    Check(results.Length == 24, "all twenty-four stored scenarios run");
    foreach (var result in results) Check(result.Passed, "stored example " + result.Scenario.Key + " expected=" + result.Scenario.ExpectedDecision + " actual=" + result.Actual.Decision);
    Check(JsonSerializer.Serialize(fixture.Access.GetTenants().Single(x => x.Id == other.Id)) == otherTenant
        && JsonSerializer.Serialize(fixture.Access.GetUsers(other.Id)) == otherMembers
        && JsonSerializer.Serialize(fixture.Access.GetGrants("real-existing-member")) == otherGrants, "bootstrap leaves existing tenant membership and grants unchanged");
    Check(fixture.Access.GetGrants(TemplateOrganizationStore.GrantHolder).Single().TenantId == view.Tenant.Id, "synthetic direct grant is scoped to educational tenant");
    var stable = fixture.Fingerprint();
    var replay = fixture.Examples.Ensure("another-operator", "Repeat without reset");
    Check(replay.Tenant.Id == view.Tenant.Id && fixture.Fingerprint() == stable, "bootstrap replay does not write documents or audit");
    fixture.Examples.EvaluateScenarios();
    Check(fixture.Fingerprint() == stable, "scenario evaluation is read only");
    fixture.Restart();
    Check(fixture.Examples.Read().Tenant.Id == view.Tenant.Id && fixture.Fingerprint() == stable, "restart preserves original data and audit");
    Check(fixture.Examples.EvaluateScenarios().All(x => x.Passed), "stored examples pass after restart");
    var originalChart = fixture.Examples.Read().Chart;
    fixture.Database.Transaction(() => { fixture.Database.Put("organization", view.Tenant.Id.ToString(), originalChart with { Revision = 2 }); return true; });
    var afterEdit = fixture.Fingerprint();
    Check(fixture.Examples.Ensure("actual-seed-operator", "Preserve edits").Chart.Revision == 2 && fixture.Fingerprint() == afterEdit, "bootstrap replay preserves subsequent edits");
}

using (var fixture = new Fixture())
{
    fixture.Access.CreateTenant(TemplateOrganizationStore.TenantKey, "An existing unrelated tenant");
    var before = fixture.Fingerprint();
    Reject<InvalidOperationException>(() => fixture.Examples.Ensure("operator", "Do not adopt existing tenant"), "key collision without marker is rejected");
    Check(fixture.Fingerprint() == before, "collision leaves existing tenant unchanged and creates no partial seed");
}

using (var fixture = new Fixture())
{
    // A real late SQLite storage failure proves rollback of all prior domain writes.
    fixture.Sql("CREATE TRIGGER fail_example_marker BEFORE INSERT ON fabric_documents WHEN NEW.kind='organization.example.v1' BEGIN SELECT RAISE(ABORT,'test late marker failure'); END;");
    var before = fixture.Fingerprint();
    Reject<SqliteException>(() => fixture.Examples.Ensure("operator", "Verify atomic failure"), "late marker write failure propagates");
    Check(fixture.Fingerprint() == before, "late failure rolls back every document and outbox record");
    Check(!fixture.Access.GetTenants().Any(x => x.Key == TemplateOrganizationStore.TenantKey), "failed bootstrap leaves no tenant");
    fixture.Sql("DROP TRIGGER fail_example_marker;");
    Check(fixture.Examples.Ensure("operator", "Retry after storage repair").Policies.Patterns.Length == 9, "bootstrap retries cleanly after rollback");
}

using (var fixture = new Fixture())
{
    var tenant = fixture.Access.CreateTenant("runtime-policy-test", "Runtime policy test").Id;
    const string member = "test-member";
    const string ungranted = "member-without-grant";
    fixture.Access.AddUser(new(tenant, member, "Member with direct read grant"));
    fixture.Access.AddUser(new(tenant, ungranted, "Member without grant"));
    fixture.Access.SetGrant(new(tenant, member, "organization.read", null), "test-authority", "Fixture grant");
    Guid Calendar(string code, int start = 480, int end = 960)
    {
        var state = fixture.Calendars.Read(tenant);
        return fixture.Calendars.Save(tenant, new(state.Revision, null, code, code, "work", "Asia/Tehran", new(2026, 1, 1), new(2027, 12, 31),
            [new(6, start, end)], [], true, "Synthetic test source", "Test calendar"), "calendar-author").Calendars.Single(x => x.Code == code).Id;
    }
    Guid Draft(string name, Guid calendar, bool enforce = true)
    {
        var state = fixture.Policies.Read(tenant);
        return fixture.Policies.Draft(tenant, new(state.Revision, name, "time-guard", "organization.read", "Deny", null, null, null, null, null, null,
            "Test policy", calendar, "working-time", enforce), "policy-author").Patterns.Single(x => x.Name == name).Id;
    }
    void Publish(Guid policy, string actor = "independent-reviewer") => fixture.Policies.Transition(tenant, policy, fixture.Policies.Read(tenant).Revision, "publish", actor, "Reviewed test publication");
    void Retire(Guid policy) => fixture.Policies.Transition(tenant, policy, fixture.Policies.Read(tenant).Revision, "retire", "independent-reviewer", "Retire test publication");
    var office = Calendar("office");
    var guard = Draft("office guard", office);
    var unpublished = fixture.Fingerprint();
    Reject<InvalidOperationException>(() => Publish(guard, "policy-author"), "policy author cannot publish their own pattern");
    Check(fixture.Fingerprint() == unpublished, "failed self-publication changes neither revision nor audit");
    Check(fixture.Policies.EvaluateLive(tenant, member, "organization.read", outside).Decision == "Permit", "unpublished guard does not silently alter live grants");
    Check(fixture.Policies.EvaluateDraftsForSimulation(tenant, member, "organization.read", outside).Decision == "Deny", "draft preview exercises the mandatory guard");
    Publish(guard);
    Check(fixture.Policies.EvaluateLive(tenant, member, "organization.read", inside).Decision == "Permit", "enforced guard permits existing grant inside window");
    Check(fixture.Policies.EvaluateLive(tenant, member, "organization.read", outside).Decision == "Deny", "direct grant cannot bypass published enforced guard");
    Check(fixture.Policies.EvaluateLive(tenant, ungranted, "organization.read", inside).Decision == "NotApplicable", "open guard does not manufacture a base permission");
    Check(!fixture.Policies.AuthorizeLive(tenant, member, "organization.read", outside), "AuthorizeLive refuses blocked direct grant");
    var beforePermit = fixture.Database.Outbox().Length;
    Check(fixture.Policies.AuthorizeLive(tenant, member, "organization.read", inside), "AuthorizeLive permits grant inside window");
    Check(fixture.Database.Outbox().Length == beforePermit + 1 && fixture.Database.Outbox().Last().Kind == "AccessDecisionApplied", "live permit writes required decision audit");
    var narrow = Calendar("narrow", 540, 660);
    var secondGuard = Draft("second guard", narrow); Publish(secondGuard);
    Check(fixture.Policies.EvaluateLive(tenant, member, "organization.read", inside).Decision == "Permit", "two guards allow their intersection");
    var oneClosed = fixture.Policies.EvaluateLive(tenant, member, "organization.read", At("2026-10-17T12:00:00+03:30"));
    Check(oneClosed.Decision == "Deny" && oneClosed.CalendarChecks?.Length == 2, "two guards use AND semantics and report both checks");
    Retire(secondGuard); Retire(guard);
    var laboratoryGuard = Draft("non-enforced published guard", office, false); Publish(laboratoryGuard);
    Check(fixture.Policies.Evaluate(tenant, member, "organization.read", outside).Decision == "Deny", "ordinary evaluation includes published non-enforced policy");
    Check(fixture.Policies.EvaluateLive(tenant, member, "organization.read", outside).Decision == "Permit", "Enforce=false is excluded from live evaluation");
    Retire(laboratoryGuard);
    var liveGuard = Draft("replacement live guard", office); Publish(liveGuard);
    Check(fixture.Policies.EvaluateLive(tenant, member, "organization.read", outside).Decision == "Deny", "published Enforce=true is included in live evaluation");

    var localRole = Guid.NewGuid(); var foreignRole = Guid.NewGuid();
    var otherTenant = fixture.Access.CreateTenant("role-reference-other", "Other organization").Id;
    fixture.Database.Transaction(() =>
    {
        fixture.Database.Put("organization", tenant.ToString(), new OrgState(tenant, 1, [], [], [], [], [new(localRole, "Local role", "local-role")]));
        fixture.Database.Put("organization", otherTenant.ToString(), new OrgState(otherTenant, 1, [], [], [], [], [new(foreignRole, "Foreign role", "foreign-role")]));
        return true;
    });
    PatternCommand RoleGuard(string name, Guid role) => new(fixture.Policies.Read(tenant).Revision, name, "time-guard", "organization.read", "Deny",
        role, null, null, null, null, null, "Role-scoped guard", office, "working-time", true);
    var beforeInvalidRole = fixture.Fingerprint();
    Reject<ArgumentException>(() => fixture.Policies.Draft(tenant, RoleGuard("nonexistent-role", Guid.NewGuid()), "author"), "time guard rejects nonexistent optional role");
    Reject<ArgumentException>(() => fixture.Policies.Draft(tenant, RoleGuard("cross-tenant-role", foreignRole), "author"), "time guard rejects optional role owned by another tenant");
    Check(fixture.Fingerprint() == beforeInvalidRole, "invalid optional role references leave policy and audit unchanged");
    var validRoleGuard = fixture.Policies.Draft(tenant, RoleGuard("valid-local-role", localRole), "author").Patterns.Single(x => x.Name == "valid-local-role");
    Check(validRoleGuard.RoleId == localRole, "time guard accepts optional role belonging to the same tenant");

    var clockReads = 0; var sampledWithinTransaction = false;
    Check(fixture.Policies.AuthorizeLiveNow(tenant, member, "organization.read", () =>
    {
        clockReads++; sampledWithinTransaction = fixture.Database.IsInTransaction; return inside;
    }), "AuthorizeLiveNow permits after acquiring current transaction");
    Check(clockReads == 1 && sampledWithinTransaction, "clock is sampled once only after SQLite transaction has been acquired");
    var boundaryClock = new TransactionBoundaryClock(fixture.Database, inside, outside);
    var currentGate = new TemporalAccessGate(new() { Enabled = true, TenantIds = [tenant] }, fixture.Policies, boundaryClock);
    Check(!currentGate.Allows(tenant, member, "organization.read") && boundaryClock.Reads == 1 && boundaryClock.SampledInside,
        "gate uses post-acquisition time and refuses a window that closed before transaction acquisition");

    var validPolicies = fixture.Policies.Read(tenant);
    var validPattern = validPolicies.Patterns.Single(x => x.Id == liveGuard);
    var malformed = new[]
    {
        (Name: "role without role id", Pattern: validPattern with { Template = "role", Effect = "Permit", RoleId = null }),
        (Name: "member window with a role id", Pattern: validPattern with { Template = "member-window", Effect = "Permit", RoleId = localRole }),
        (Name: "member window without time condition", Pattern: validPattern with { Template = "member-window", Effect = "Permit", CalendarId = null }),
        (Name: "non-holiday without calendar", Pattern: validPattern with { Template = "member-window", Effect = "Permit", CalendarId = null, CalendarMode = "non-holiday", StartsAt = inside.AddDays(-1) })
    };
    foreach (var invalid in malformed)
    {
        fixture.Database.Transaction(() =>
        {
            fixture.Database.Put("access.policy", tenant.ToString(), validPolicies with { Patterns = validPolicies.Patterns.Select(x => x.Id == liveGuard ? invalid.Pattern : x).ToArray() });
            return true;
        });
        var decision = fixture.Policies.EvaluateLive(tenant, member, "organization.read", inside);
        Check(decision.Decision == "Indeterminate" && decision.Reason == "invalid-policy-data", "malformed persisted " + invalid.Name + " fails closed despite direct grant");
    }
    fixture.Database.Transaction(() => { fixture.Database.Put("access.policy", tenant.ToString(), validPolicies); return true; });

    var beforeInvalid = fixture.Fingerprint();
    Reject<ArgumentException>(() => Draft("missing calendar reference", Guid.NewGuid()), "draft rejects unknown calendar reference");
    Check(fixture.Fingerprint() == beforeInvalid, "invalid calendar draft changes neither policy nor audit");
    // Missing persisted references must fail closed even after successful publication.
    var calendars = fixture.Calendars.Read(tenant);
    var officeCalendar = calendars.Calendars.Single(x => x.Id == office);
    fixture.Database.Transaction(() => { fixture.Database.Put(OrganizationCalendarStore.DocumentKind, tenant.ToString(), calendars with { Calendars = calendars.Calendars.Where(x => x.Id != office).ToArray() }); return true; });
    Check(fixture.Policies.EvaluateLive(tenant, member, "organization.read", inside).Decision == "Indeterminate", "missing required calendar cannot fall back to manual grant");
    fixture.Database.Transaction(() => { fixture.Database.Put(OrganizationCalendarStore.DocumentKind, tenant.ToString(), calendars); return true; });
    fixture.Calendars.Save(tenant, new(calendars.Revision, office, officeCalendar.Code, officeCalendar.Name, officeCalendar.Kind, officeCalendar.TimeZoneId,
        officeCalendar.CoverageFrom, officeCalendar.CoverageThrough, officeCalendar.WeeklyWindows, officeCalendar.Exceptions, false, officeCalendar.Source, "Deactivate calendar"), "calendar-author");
    Check(fixture.Policies.EvaluateLive(tenant, member, "organization.read", inside).Decision == "Indeterminate", "inactive required calendar cannot fall back to manual grant");
    Check(!fixture.Policies.AuthorizeLive(tenant, member, "organization.read", inside), "AuthorizeLive fails closed for inactive required calendar");
}

RuntimeGateTests.Run(Check);
TemplateReadOnlyTests.Run(Check);
Console.WriteLine($"{checks} temporal integration checks passed.");

sealed class Fixture : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "fanasa-temporal-tests-" + Guid.NewGuid().ToString("N"));
    private readonly TimeProvider clock = new FixedClock(DateTimeOffset.Parse("2026-10-16T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture));
    public string DatabasePath => Path.Combine(directory, "fabric.db");
    public FabricDatabase Database { get; private set; } = null!;
    public PersistentAccessManagement Access { get; private set; } = null!;
    public OrganizationCalendarStore Calendars { get; private set; } = null!;
    public AccessPolicyStore Policies { get; private set; } = null!;
    public TemplateOrganizationStore Examples { get; private set; } = null!;
    public Fixture() => Open();
    private void Open()
    {
        Database = new(DatabasePath); Access = new(Database, new InMemoryAccessManagement());
        Calendars = new(Database, Access, clock); Policies = new(Database, Access, Calendars);
        Examples = new(Database, Access, Calendars, Policies, clock);
    }
    public void Restart() { Database.Dispose(); Open(); }
    public void Sql(string sql)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());
        connection.Open(); using var command = connection.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
    public string Fingerprint()
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open(); using var transaction = connection.BeginTransaction(deferred: true);
        var text = new StringBuilder();
        foreach (var query in new[] { "SELECT kind,key,json FROM fabric_documents ORDER BY kind,key", "SELECT id,key,kind,payload,recorded FROM fabric_outbox ORDER BY id" })
        {
            using var command = connection.CreateCommand(); command.Transaction = transaction; command.CommandText = query;
            using var reader = command.ExecuteReader();
            while (reader.Read()) for (var column = 0; column < reader.FieldCount; column++) text.Append(JsonSerializer.Serialize(reader.GetValue(column))).Append('\n');
        }
        transaction.Commit();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
    public void Dispose()
    {
        Database.Dispose(); SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }
}

sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

sealed class TransactionBoundaryClock(FabricDatabase database, DateTimeOffset beforeAcquire, DateTimeOffset afterAcquire) : TimeProvider
{
    public int Reads { get; private set; }
    public bool SampledInside { get; private set; }
    public override DateTimeOffset GetUtcNow()
    {
        Reads++; SampledInside = database.IsInTransaction;
        return SampledInside ? afterAcquire : beforeAcquire;
    }
}
