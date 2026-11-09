using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Fanasa.AccessManagement.Web.Platform;
using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.ConsumerCatalog;
using Fanasa.AccessManagement.Web.Persistence;

namespace Fanasa.AccessManagement.Web.Api;

[ApiController, Route("api/platform"), Authorize(AuthenticationSchemes = "PlatformBearer")]
public sealed class PlatformApi(PlatformRegistry registry, IConfiguration configuration, FabricDatabase database,
    PersistentAccessManagement access, TimeProvider time, IAuthorizationService authorization) : ControllerBase
{
    [HttpGet("memberships"), Authorize(Policy = "PlatformService")]
    public IActionResult Memberships(string subject) => Ok(registry.Tenants(subject));
    [HttpPost("authorize"), Authorize(Policy = "PlatformService")]
    public IActionResult AuthorizeOperation(PlatformAuthorizationRequest request) => Ok(new { allowed = registry.Allows(request.Subject, request.TenantId, request.Permission) });
    [HttpGet("products")]
    public async Task<IActionResult> Products(string subject)
    {
        if (ConsumerApplicationResponses.IsConsumerRequest(Request, User, configuration))
            return ConsumerApplicationResponses.Read(this, true, registry, database, configuration, time, access);
        if (!(await authorization.AuthorizeAsync(User, null, "PlatformService")).Succeeded)
            return Forbid("PlatformBearer");
        return Ok(registry.Products(subject).Select(x => DeveloperProjectRegistration.ForSubject(x, subject, registry)));
    }
    [HttpPost("developer-projects"), Authorize(Policy = "PlatformService")]
    public IActionResult RegisterProject(DeveloperProjectRegistrationRequest request)
    {
        try { return Ok(DeveloperProjectRegistration.Register(request, User.FindFirst("sub")!.Value, registry, configuration)); }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }
    }
    [HttpPost("products"), Authorize(Policy = "PlatformService")]
    public IActionResult Register(PlatformProduct product)
    {
        if (product.Key is null || !product.Key.StartsWith("developer-", StringComparison.Ordinal)
            || !Guid.TryParseExact(product.Key[10..], "N", out var appId)
            || product.Center != "fanasa.rayan" || product.Audience != "fanasa.app." + appId.ToString("N")
            || !ManagedApplicationUrls.IsDeveloperRuntime(product.Url, appId, configuration)
            || string.IsNullOrWhiteSpace(product.Owner))
            return BadRequest(new { error = "Developer service may only register its own runtime applications." });
        if (!product.TenantId.HasValue || !registry.Allows(product.Owner, product.TenantId.Value, "developer.launch")) return Forbid();
        if (DeveloperProjectRegistration.IsProject(product) || registry.Products().Any(x => x.Key == product.Key && DeveloperProjectRegistration.IsProject(x)))
            return Conflict(new { error = "Project registrations must use the developer-projects contract." });
        try { return Ok(registry.Register(product, User.FindFirst("sub")!.Value)); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }
    }
}
public sealed record PlatformAuthorizationRequest(string Subject, Guid TenantId, string Permission);
