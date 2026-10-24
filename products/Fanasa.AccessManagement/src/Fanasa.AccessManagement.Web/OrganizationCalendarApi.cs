using Fanasa.AccessManagement.Web.Organization;
using Fanasa.AccessManagement.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fanasa.AccessManagement.Web.Api;

[ApiController, Authorize, Route("api/organization/tenants/{tenantId:guid}/calendars")]
public sealed class OrganizationCalendarApi(OrganizationCalendarStore calendars, TemplateOrganizationStore examples) : ControllerBase
{
    private bool Admin => User.Identity?.IsAuthenticated == true && User.HasClaim("permission", "platform.admin");
    private bool Allows(Guid tenant, bool write) => Admin || (write
        ? TenantAuthorization.Allows(User, tenant, "tenancy.manage") && TenantAuthorization.Allows(User, tenant, "organization.write")
        : TenantAuthorization.Allows(User, tenant, "organization.read"));

    [HttpGet]
    public IActionResult Read(Guid tenantId) => Allows(tenantId, false) ? Execute(() => calendars.Read(tenantId)) : Forbid();

    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult Save(Guid tenantId, OrganizationCalendarCommand command)
    {
        if (!Allows(tenantId, true)) return Forbid();
        if (examples.IsTemplateTenant(tenantId)) return Conflict(new { error = "تقویم مرجع سازمان الگو فقط‌خواندنی است؛ نمونه را در سازمان خود تعریف کنید." });
        return Execute(() => calendars.Save(tenantId, command, OrgAuthorization.Subject(User)));
    }

    [HttpPost("{calendarId:guid}/evaluate"), ValidateAntiForgeryToken]
    public IActionResult Evaluate(Guid tenantId, Guid calendarId, CalendarSimulation command)
    {
        if (!Allows(tenantId, false)) return Forbid();
        return Execute(() => calendars.Evaluate(tenantId, calendarId, command.At ?? DateTimeOffset.UtcNow, command.Mode));
    }

    private IActionResult Execute(Func<object> action)
    {
        Response.Headers.CacheControl = "no-store";
        try { return Ok(action()); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException error) { return BadRequest(new { error = error.Message }); }
        catch (InvalidOperationException error) { return Conflict(new { error = error.Message }); }
    }
}
public sealed record CalendarSimulation(DateTimeOffset? At, string Mode = "working-time");
