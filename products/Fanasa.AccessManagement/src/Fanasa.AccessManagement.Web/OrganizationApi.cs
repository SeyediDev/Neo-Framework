using Fanasa.AccessManagement.Web.Organization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fanasa.AccessManagement.Web.Api;

[ApiController, Authorize, Route("api/organization/tenants/{tenantId:guid}")]
public sealed class OrganizationApi(OrganizationStore store) : ControllerBase
{
    [HttpGet("compare")]
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
            return Ok(OrganizationComparison.Compare(At(from), At(to), offset));
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return Forbid(); }
    }
    [HttpGet]
    public IActionResult Read(Guid tenantId, [FromQuery] long? revision)
    {
        if (!OrgAuthorization.Allows(User, tenantId, false)) return Forbid();
        try { return Ok(revision.HasValue ? store.History(tenantId, revision.Value) : store.Read(tenantId)); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (UnauthorizedAccessException) { return Forbid(); }
    }
    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult Execute(Guid tenantId, OrgCommand command)
    {
        if (!OrgAuthorization.Allows(User, tenantId, true)) return Forbid();
        try { return Ok(store.Execute(tenantId, command, OrgAuthorization.Subject(User))); }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }
    }
}
