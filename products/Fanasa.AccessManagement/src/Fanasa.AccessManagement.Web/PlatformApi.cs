using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Fanasa.AccessManagement.Web.Platform;

namespace Fanasa.AccessManagement.Web.Api;

[ApiController, Route("api/platform"), Authorize(Policy = "PlatformService")]
public sealed class PlatformApi(PlatformRegistry registry) : ControllerBase
{
    [HttpGet("memberships")]
    public IActionResult Memberships(string subject) => Ok(registry.Tenants(subject));
    [HttpPost("authorize")]
    public IActionResult AuthorizeOperation(PlatformAuthorizationRequest request) => Ok(new { allowed = registry.Allows(request.Subject, request.TenantId, request.Permission) });
    [HttpGet("products")]
    public IActionResult Products(string subject) => Ok(registry.Products(subject));
    [HttpPost("products")]
    public IActionResult Register(PlatformProduct product)
    {
        if (product.Key is null || !product.Key.StartsWith("developer-", StringComparison.Ordinal)
            || !Guid.TryParseExact(product.Key[10..], "N", out var appId)
            || product.Center != "fanasa.rayan" || product.Audience != "fanasa.app." + appId.ToString("N")
            || product.Url != "https://developer.fanasa.net.local/runtime/" + appId + "/"
            || string.IsNullOrWhiteSpace(product.Owner))
            return BadRequest(new { error = "Developer service may only register its own runtime applications." });
        if (!product.TenantId.HasValue || !registry.Allows(product.Owner, product.TenantId.Value, "developer.launch")) return Forbid();
        try { return Ok(registry.Register(product, User.FindFirst("sub")!.Value)); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }
    }
}
public sealed record PlatformAuthorizationRequest(string Subject, Guid TenantId, string Permission);
