using System.Security.Claims;
using Fanasa.AccessManagement.Web.Api;
using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Governance;
using Fanasa.AccessManagement.Web.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

static class PipApiTests
{
    public static void Run(string directory, Action<bool, string> check)
    {
        using var db = new FabricDatabase(Path.Combine(directory, "pip-api", "fabric.db"));
        var access = new PersistentAccessManagement(db, new InMemoryAccessManagement());
        var tenant = access.CreateTenant("pip-api", "PIP API").Id;
        var other = access.CreateTenant("pip-api-other", "Other PIP API").Id;
        var store = new PipStore(db, access);
        var source = store.Register(tenant, "Pending HR", "https://hr.example.test/attributes", "hr.example.test", null);
        var connector = new NoFetchConnector();
        var sync = new PipSyncService(store, connector, TimeProvider.System);
        var api = new PipApi(store, sync, new PipTransportOptions(), TimeProvider.System)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };
        void Principal(params Claim[] claims) => api.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));
        check(api.Sources(tenant) is ForbidResult, "anonymous callers cannot read PIP configuration");
        api.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity([new("permission", "platform.admin")]));
        check(api.Sources(tenant) is ForbidResult, "unauthenticated admin claims cannot bypass PIP authentication");
        Principal(new("sub", "reader"), new("tenant_id", tenant.ToString()), new("permission", "tenancy.read"));
        check(api.Sources(tenant) is ForbidResult, "tenant read access does not grant PIP administration");
        Principal(new("sub", "manager"), new("tenant_id", tenant.ToString()), new("permission", "tenancy.manage"));
        check(api.Sources(tenant) is OkObjectResult && api.Sources(other) is ForbidResult, "PIP manager can inspect only the authorized tenant");
        check(api.Runs(tenant, source.Id) is OkObjectResult && api.Runs(tenant, source.Id, 101) is BadRequestObjectResult,
            "PIP run queries enforce their bounded history limit");
        check(api.Enable(tenant, source.Id, new(true, "test unapproved endpoint")) is ConflictObjectResult
            && !store.ReadSource(tenant, source.Id).Enabled, "tenant managers cannot enable an endpoint without deployment approval");
        check(api.Register(tenant, new("missing reason", source.Endpoint, source.AllowedHost, null)) is BadRequestObjectResult,
            "PIP source mutations require an audit reason");
        check(api.Sync(tenant, source.Id, new("", "validate empty subject"), CancellationToken.None).GetAwaiter().GetResult() is BadRequestObjectResult,
            "PIP sync validates subject before any external request");
        check(api.Example(tenant, new("ordinary manager request")) is ForbidResult, "educational seed endpoint requires platform administration");
        Principal(new("sub", "admin"), new("permission", "platform.admin"));
        check(api.Runs(other, source.Id) is NotFoundResult, "even platform administration must correlate PIP source and tenant identifiers");
        var writes = new[] { nameof(PipApi.Register), nameof(PipApi.Enable), nameof(PipApi.Sync), nameof(PipApi.Example) };
        check(writes.All(name => typeof(PipApi).GetMethod(name)!.GetCustomAttributes(typeof(ValidateAntiForgeryTokenAttribute), true).Length == 1),
            "all PIP source mutation endpoints require antiforgery validation");
        check(connector.Requests == 0, "PIP catalog and authorization checks never fetch external attributes");
    }

    private sealed class NoFetchConnector : IPipExternalConnector
    {
        public int Requests { get; private set; }
        public Task<PipFetchedSnapshot> FetchAsync(PipSourceDefinition source, string subject, CancellationToken cancellationToken = default)
        {
            Requests++;
            throw new InvalidOperationException("Read-only API checks must not fetch data.");
        }
    }
}
