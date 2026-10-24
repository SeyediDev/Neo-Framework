using System.Reflection;
using System.Security.Claims;
using Fanasa.AccessManagement.Web.Api;
using Fanasa.AccessManagement.Web.Domain.Access;
using Fanasa.AccessManagement.Web.Organization;
using Fanasa.AccessManagement.Web.Pages;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Routing;

internal static class TemplateReadOnlyTests
{
    public static void Run(Action<bool, string> check)
    {
        using var fixture = new Fixture();
        var template = fixture.Examples.Ensure("seed-author", "Create reference fixture");
        var tenant = template.Tenant.Id;
        var other = fixture.Access.CreateTenant("ordinary-tenant", "سازمان الگو").Id;
        var admin = new ClaimsPrincipal(new ClaimsIdentity([new("sub", "test-admin"), new("permission", "platform.admin")], "test"));
        var api = new TenantAdministrationApi(fixture.Access, fixture.Examples)
        { ControllerContext = new() { HttpContext = new DefaultHttpContext { User = admin } } };
        var fingerprint = fixture.Fingerprint();
        var member = template.Members.Single(x => x.KeycloakSubject == TemplateOrganizationStore.Reader);
        check(api.AddMember(tenant, new("new-human", "Human", "Attempt reference change")) is ConflictObjectResult,
            "tenant administration rejects adding a member to marked template");
        check(api.Membership(tenant, member.Id, new(false, "Attempt offboarding")) is ConflictObjectResult,
            "tenant administration rejects deactivating a reference member");
        check(api.Grant(tenant, new(member.KeycloakSubject, "organization.write", null, "Attempt grant")) is ConflictObjectResult,
            "tenant administration rejects changing reference grants");
        check(api.Subscribe(tenant, new("organization-fabric", Guid.NewGuid(), 10, "Attempt subscription")) is ConflictObjectResult,
            "tenant administration rejects adding a reference subscription");
        check(api.Subscription(tenant, Guid.NewGuid(), new("suspended", null, "Attempt subscription change")) is ConflictObjectResult,
            "tenant administration rejects subscription mutation before domain writes");
        check(api.Plan(tenant, new("organization-fabric", "example-plan", "Example", "payg", "IRR", "Attempt plan")) is ConflictObjectResult,
            "tenant administration rejects plan authoring through reference tenant");
        check(api.Read(tenant) is OkObjectResult && fixture.Fingerprint() == fingerprint,
            "template administration metadata remains readable with no writes");
        check(api.CreateTenant(new(TemplateOrganizationStore.TenantKey, "Conflicting name", "Attempt duplicate key")) is ConflictObjectResult
            && fixture.Fingerprint() == fingerprint, "creating an existing template key fails without overwriting reference data");

        var observer = new ClaimsPrincipal(new ClaimsIdentity([new("sub", "ordinary-member")], "test"));
        api.ControllerContext.HttpContext.User = observer;
        check(api.AddMember(tenant, new("new-human", "Human", "No authorization")) is ForbidResult,
            "reference guard does not replace ordinary authorization");
        api.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new("permission", "platform.admin")]));
        check(api.AddMember(tenant, new("new-human", "Human", "Unauthenticated claim")) is ForbidResult,
            "an unauthenticated admin claim cannot mutate reference membership");
        api.ControllerContext.HttpContext.User = admin;
        check(api.AddMember(other, new("ordinary-human", "Human", "Ordinary tenant change")) is OkObjectResult
            && fixture.Access.GetUsers(other).Any(x => x.KeycloakSubject == "ordinary-human"),
            "ordinary tenant with identical display name keeps normal membership behavior");

        var page = new TenancyModel(fixture.Access, fixture.Examples)
        { PageContext = new PageContext(new ActionContext(new DefaultHttpContext { User = admin }, new RouteData(), new PageActionDescriptor())) };
        page.OnGet(tenant);
        check(page.IsTemplateTenant && !page.CanManage, "membership page hides mutations for marked template even for admin");
        page.OnGet(other);
        check(!page.IsTemplateTenant && page.CanManage, "membership page retains admin management for other tenants");

        var store = new OrganizationStore(Path.GetDirectoryName(fixture.DatabasePath)!, fixture.Access, database: fixture.Database);
        var writer = new ClaimsPrincipal(new ClaimsIdentity([new("sub", "test-admin"), new("tenant_permission", $"{tenant}:organization.write")], "test"));
        var organization = new OrganizationApi(store, examples: fixture.Examples)
        { ControllerContext = new() { HttpContext = new DefaultHttpContext { User = writer } } };
        var beforeChart = fixture.Fingerprint();
        var command = new OrgCommand(template.Chart.Revision, "unit.save", null, "Unauthorized change", "change", null,
            "department", null, 1, null, null, null, null, "Attempt reference chart mutation");
        check(organization.Execute(tenant, command) is ConflictObjectResult && fixture.Fingerprint() == beforeChart,
            "organization mutation cannot change template even with an otherwise valid write claim");

        var boundary = new AccessBoundaryFilter(fixture.Access, new AcceptedTestAntiforgery(), fixture.Examples);
        bool Invoke(string action, string verb, Dictionary<string, object?> arguments, Action? nextAction = null)
        {
            var http = new DefaultHttpContext { User = admin }; http.Request.Method = verb;
            var descriptor = new ControllerActionDescriptor
            {
                ActionName = action, ControllerTypeInfo = typeof(AccessApi).GetTypeInfo(),
                MethodInfo = typeof(AccessApi).GetMethods().Single(x => x.Name == action && (action != "Usage" || x.GetParameters().Length == 1))
            };
            var controller = new AccessApi(fixture.Access);
            var context = new ActionContext(http, new RouteData(), descriptor);
            var executing = new ActionExecutingContext(context, [], arguments, controller);
            var called = false;
            boundary.OnActionExecutionAsync(executing, () =>
            {
                called = true; nextAction?.Invoke();
                return Task.FromResult(new ActionExecutedContext(context, [], controller));
            }).GetAwaiter().GetResult();
            return called;
        }
        var legacyBefore = fixture.Fingerprint();
        var legacyCases = new (string Action, object Request)[]
        {
            ("AddUser", new AddTenantUserRequest(tenant, "legacy-human", "Human")),
            ("CreateRole", new CreateRoleRequest(tenant, "organization-fabric", "role", "Role", ["organization.read"])),
            ("Subscribe", new CreateSubscriptionRequest(tenant, "organization-fabric", Guid.NewGuid(), 10)),
            ("ContractEvent", new ContractLifecycleEvent("event", tenant, "contract", "organization-fabric", "fanasa.ayin", "plan", "ContractActivated", 1, "unit", DateTimeOffset.UtcNow)),
            ("Usage", new RecordUsageCommand(tenant, "organization-fabric", "fanasa.ayin", "metric", 1, "unit", DateTimeOffset.UtcNow, "test", "request", null))
        };
        foreach (var item in legacyCases)
            check(!Invoke(item.Action, "POST", new() { ["request"] = item.Request }), "legacy Access mutation is blocked for template: " + item.Action);
        check(!Invoke("Charge", "POST", new() { ["tenantId"] = tenant }), "legacy Access route-scoped charge is blocked for template");
        check(fixture.Fingerprint() == legacyBefore, "blocked legacy mutations leave every document and audit record unchanged");
        check(Invoke("Users", "GET", new() { ["tenantId"] = tenant }), "legacy template metadata remains readable");
        check(Invoke("AddUser", "POST", new() { ["request"] = new AddTenantUserRequest(other, "legacy-ordinary", "Ordinary") },
            () => fixture.Access.AddUser(new(other, "legacy-ordinary", "Ordinary")))
            && fixture.Access.GetUsers(other).Any(x => x.KeycloakSubject == "legacy-ordinary"), "legacy Access writes still work for ordinary tenant");
        check(fixture.Examples.EvaluateScenarios().All(x => x.Passed), "reference scenarios remain valid after all rejected HTTP mutations");
    }

    // Only isolates existing CSRF validation from these mutation-scope tests; production still uses its real antiforgery service.
    private sealed class AcceptedTestAntiforgery : IAntiforgery
    {
        public AntiforgeryTokenSet GetAndStoreTokens(HttpContext httpContext) => new("request", "cookie", "field", "header");
        public AntiforgeryTokenSet GetTokens(HttpContext httpContext) => GetAndStoreTokens(httpContext);
        public Task<bool> IsRequestValidAsync(HttpContext httpContext) => Task.FromResult(true);
        public Task ValidateRequestAsync(HttpContext httpContext) => Task.CompletedTask;
        public void SetCookieTokenAndHeader(HttpContext httpContext) { }
    }
}
