using System.Security.Claims;
using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Organization;
using Fanasa.AccessManagement.Web.Api;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

static class ComparisonTests
{
    public static void Run(string directory, Action<bool, string> check)
    {
        var access = new InMemoryAccessManagement(); var tenant = access.GetTenants().Single().Id;
        var store = new OrganizationStore(Path.Combine(directory, "comparison"), access);
        var command = new OrgCommand(0, "unit.save", null, "Before", "ROOT", null, "team", null, 1, null, null, null, null, "test");
        var first = store.Execute(tenant, command, "actor"); var unit = first.Units.Single().Id;
        var second = store.Execute(tenant, command with { ExpectedRevision = 1, Id = unit, Name = "After" }, "actor");
        var difference = OrganizationComparison.Compare(first, second);
        check(difference.Changes.Single().Fields.Single() == new OrgFieldDifference("Name", "Before", "After"), "comparison reports exactly changed fields without unchanged metadata");
        check(OrganizationComparison.Compare(first, first).Total == 0, "same revision comparison is empty");
        var blank = new OrgState(tenant, 0, [], [], [], [], []);
        check(OrganizationComparison.Compare(blank, first).Changes.Single().Change == "added", "comparison identifies baseline additions");
        var archived = store.Execute(tenant, command with { ExpectedRevision = 2, Operation = "unit.archive", Id = unit }, "actor");
        check(OrganizationComparison.Compare(second, archived).Changes.Single().Fields.Single().Field == "Active", "archive is a status change preserving identity");
        var many = blank with { Revision = 1, Units = Enumerable.Range(0, 205).Select(i => new OrgUnit(Guid.NewGuid(), "Unit"+i, "U"+i, null, "team", true)).ToArray() };
        var a = OrganizationComparison.Compare(blank, many); var b = OrganizationComparison.Compare(blank, many, a.NextOffset!.Value); var c = OrganizationComparison.Compare(blank, many, b.NextOffset!.Value);
        check(a.Total == 205 && a.Changes.Length == 100 && b.Changes.Length == 100 && c.Changes.Length == 5 && c.NextOffset is null && a.Changes.Concat(b.Changes).Concat(c.Changes).Select(x => x.Id).Distinct().Count() == 205, "comparison bounds output and pages immutable snapshots without duplication");
        var user = new ClaimsPrincipal(new ClaimsIdentity([new("tenant_permission", $"{tenant}:organization.read")], "test"));
        var api = new OrganizationApi(store) { ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { User = user } } };
        check(api.Compare(tenant, 0, 3) is OkObjectResult, "read-only chart user can compare historical revisions");
        check(api.Compare(Guid.NewGuid(), 0, 0) is ForbidResult, "comparison rejects cross-tenant access");
        check(api.Compare(tenant, 2, 1) is BadRequestResult && api.Compare(tenant, 0, 1, -1) is BadRequestResult && api.Compare(tenant, 0, 99) is NotFoundResult, "comparison validates range offset and missing revisions");
        var gated = new OrganizationApi(new OrganizationStore(Path.Combine(directory, "comparison-gated"), access, true)) { ControllerContext = api.ControllerContext };
        check(gated.Compare(tenant, 0, 0) is ForbidResult, "empty comparison cannot bypass SaaS subscription gate");
        var role = new OrgRole(Guid.NewGuid(), "Role", "ROLE"); var position = new OrgPosition(Guid.NewGuid(), unit, "Position", "POS", 1, true, role.Id); var appointment = new OrgAppointment(Guid.NewGuid(), position.Id, "member", DateTimeOffset.UtcNow, null);
        var structured = blank with { Revision = 1, Roles = [role], Positions = [position], Appointments = [appointment] };
        check(OrganizationComparison.Compare(blank, structured).Changes.Select(x => x.Entity).Order().SequenceEqual(new[] { "appointment", "position", "role" }), "comparison covers roles positions and appointments");
        var ended = structured with { Revision = 2, Appointments = [appointment with { To = appointment.From.AddDays(1) }] };
        check(OrganizationComparison.Compare(structured, ended).Changes.Single().Fields.Single().Field == "To", "comparison identifies appointment termination");
    }
}
