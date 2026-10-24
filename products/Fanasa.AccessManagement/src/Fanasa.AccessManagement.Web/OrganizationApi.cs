using Fanasa.AccessManagement.Web.Organization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fanasa.AccessManagement.Web.Api;

[ApiController, Authorize, Route("api/organization/tenants/{tenantId:guid}")]
public sealed class OrganizationApi(OrganizationStore store, Security.TemporalAccessGate? temporalAccess = null, TemplateOrganizationStore? examples = null) : ControllerBase
{
    private void RequireCurrentTimeAccess(Guid tenant)
    {
        if (temporalAccess?.IsEnabled(tenant) == true && !temporalAccess.Allows(tenant, OrgAuthorization.Subject(User), "organization.read"))
            throw new UnauthorizedAccessException();
    }
    [HttpGet("compare"), Security.TemporalPermission("organization.read")]
    public IActionResult Compare(Guid tenantId, [FromQuery] long from, [FromQuery] long to, [FromQuery] int offset = 0)
    {
        if (!OrgAuthorization.Allows(User, tenantId, false)) return Forbid();
        if (from < 0 || to < from || offset < 0) return BadRequest();
        try
        {
            // Read first so even a comparison against the empty baseline enforces the subscription gate.
            var current = store.Read(tenantId);
            if (to > current.Revision) return NotFound();
            OrgState At(long revision) => revision == 0 ? new(tenantId, 0, [], [], [], [], []) : store.History(tenantId, revision);
            var result = OrganizationComparison.Compare(At(from), At(to), offset);
            RequireCurrentTimeAccess(tenantId);
            return Ok(result);
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return Forbid(); }
    }
    [HttpGet, Security.TemporalPermission("organization.read")]
    public IActionResult Read(Guid tenantId, [FromQuery] long? revision)
    {
        if (!OrgAuthorization.Allows(User, tenantId, false)) return Forbid();
        try
        {
            var result = revision.HasValue ? store.History(tenantId, revision.Value) : store.Read(tenantId);
            RequireCurrentTimeAccess(tenantId);
            return Ok(result);
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return Forbid(); }
    }
    [HttpPost, ValidateAntiForgeryToken, Security.TemporalPermission("organization.write")]
    public IActionResult Execute(Guid tenantId, OrgCommand command)
    {
        if (!OrgAuthorization.Allows(User, tenantId, true)) return Forbid();
        if (TemplateOrganizationWriteGuard.Reject(examples, tenantId) is { } readOnly) return readOnly;
        if (command.Operation == "appointment.transfer") return BadRequest(new { error = "انتقال از گردش تأیید راهبری هویت انجام می‌شود؛ اجرای آن هنوز فعال نشده است." });
        try { return Ok(store.Execute(tenantId, command, OrgAuthorization.Subject(User), User)); }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }
    }
}
