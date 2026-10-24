using Fanasa.AccessManagement.Web.Organization;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fanasa.AccessManagement.Web.Api;

[ApiController]
[Authorize(AuthenticationSchemes = CookieAuthenticationDefaults.AuthenticationScheme, Policy = "PlatformAdmin")]
[Route("api/organization/template")]
public sealed class TemplateOrganizationApi(TemplateOrganizationStore examples) : ControllerBase
{
    [HttpGet]
    public IActionResult Read() => Execute(examples.Read);

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult Ensure(TemplateOrganizationSeedCommand command) => Execute(() => examples.Ensure(OrgAuthorization.Subject(User), command.Reason));

    [HttpGet("scenarios")]
    public IActionResult Scenarios() => Execute(() =>
    {
        var view = examples.Read();
        var results = examples.EvaluateScenarios();
        return new { simulation = true, enforced = false, tenantId = view.Tenant.Id, passed = results.Count(x => x.Passed), total = results.Length, results };
    });

    private IActionResult Execute(Func<object> operation)
    {
        Response.Headers.CacheControl = "no-store";
        try { return Ok(operation()); }
        catch (KeyNotFoundException error) { return NotFound(new { error = error.Message }); }
        catch (ArgumentException error) { return BadRequest(new { error = error.Message }); }
        catch (InvalidOperationException error) { return Conflict(new { error = error.Message }); }
    }
}

public sealed record TemplateOrganizationSeedCommand(string Reason);
