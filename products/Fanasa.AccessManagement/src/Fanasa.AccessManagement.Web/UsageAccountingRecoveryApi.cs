using System.Text.Json;
using Fanasa.AccessManagement.Web.Organization;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fanasa.AccessManagement.Web.Metering;

public sealed record RecoverMeasuredUsageCommand(string BusinessKey, string ExpectedOperationFingerprint, Guid RequestId, string Reason);

[ApiController, Route("api/usage-accounting/organization/{consumer:guid}")]
[Authorize(AuthenticationSchemes = CookieAuthenticationDefaults.AuthenticationScheme, Policy = "PlatformAdmin")]
public sealed class UsageAccountingRecoveryApi(UsageReconciliationStore reconciliation, MeasuredUsageRecoveryStore recovery) : ControllerBase
{
    [HttpGet("reconciliation")]
    public IActionResult Inspect(Guid consumer, int maxRows = 10000) => Execute(() => reconciliation.Inspect(consumer, maxRows));

    [HttpPost("recover"), ValidateAntiForgeryToken, RequestSizeLimit(16_384)]
    public IActionResult Recover(Guid consumer, RecoverMeasuredUsageCommand command) => Execute(() =>
        recovery.Recover(consumer, command.BusinessKey, command.ExpectedOperationFingerprint, command.RequestId,
            OrgAuthorization.Subject(User), command.Reason));

    private IActionResult Execute<T>(Func<T> action)
    {
        try { return Ok(action()); }
        catch (UnauthorizedAccessException) { return Forbid(CookieAuthenticationDefaults.AuthenticationScheme); }
        catch (KeyNotFoundException) { return NotFound(new { error = "No retained measured operation exists." }); }
        catch (ArgumentException error) { return BadRequest(new { error = error.Message }); }
        catch (InvalidDataException) { return Conflict(new { error = "Usage evidence requires reconciliation before recovery." }); }
        catch (JsonException) { return Conflict(new { error = "Persisted usage evidence is invalid." }); }
        catch (InvalidOperationException error) { return Conflict(new { error = error.Message }); }
    }
}
