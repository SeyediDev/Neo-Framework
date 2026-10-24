using Fanasa.AccessManagement.Web.Organization;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fanasa.AccessManagement.Web.Metering;

public sealed record PublishOrganizationUsagePriceCommand(string PriceId, int PriceVersion, UsageRateSnapshot RateSnapshot, string Reason);

[ApiController, Route("api/usage-accounting/organization/prices")]
[Authorize(AuthenticationSchemes = CookieAuthenticationDefaults.AuthenticationScheme, Policy = "PlatformAdmin")]
public sealed class OrganizationUsagePriceApi(OrganizationUsagePriceStore store) : ControllerBase
{
    [HttpGet]
    public IActionResult Read(string priceId, int priceVersion) => Execute(() => store.Read(priceId, priceVersion));

    [HttpPost, ValidateAntiForgeryToken, RequestSizeLimit(16_384)]
    public IActionResult Publish(PublishOrganizationUsagePriceCommand command) => Execute(() =>
        store.Publish(command.PriceId, command.PriceVersion, command.RateSnapshot, OrgAuthorization.Subject(User), command.Reason));

    private IActionResult Execute<T>(Func<T> action)
    {
        try { return Ok(action()); }
        catch (UnauthorizedAccessException) { return Forbid(CookieAuthenticationDefaults.AuthenticationScheme); }
        catch (ArgumentException error) { return BadRequest(new { error = error.Message }); }
        catch (InvalidOperationException error) { return Conflict(new { error = error.Message }); }
    }
}
