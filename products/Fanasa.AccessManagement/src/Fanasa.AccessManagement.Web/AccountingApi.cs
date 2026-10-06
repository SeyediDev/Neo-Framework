using Fanasa.AccessManagement.Web.Accounting;
using Fanasa.AccessManagement.Web.Organization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Fanasa.AccessManagement.Web.Security;

namespace Fanasa.AccessManagement.Web.Api;
[ApiController, Authorize, Route("api/accounting/tenants/{tenantId:guid}")]
public sealed class AccountingApi(AccountingStore store) : ControllerBase
{
    private bool Allows(Guid tenant, string permission) => TenantAuthorization.Allows(User, tenant, permission);
    [HttpGet]
    public IActionResult Read(Guid tenantId)
    {
        if (!Allows(tenantId, "billing.read")) return Forbid();
        try { return Ok(store.Read(tenantId)); } catch (KeyNotFoundException) { return NotFound(); }
    }
    [HttpPost, ValidateAntiForgeryToken]
    public IActionResult Execute(Guid tenantId, BillingCommand command)
    {
        if (!Allows(tenantId, command.Operation == "usage" ? "billing.meter" : "billing.write")) return Forbid();
        if (command.Key?.StartsWith("organization:", StringComparison.Ordinal) == true || command.Key?.StartsWith("zarinpal:", StringComparison.Ordinal) == true) return BadRequest(new { error = "کلید داخلی از API قابل ثبت نیست." });
        try { return Ok(store.Execute(tenantId, command, OrgAuthorization.Subject(User))); }
        catch (UnauthorizedAccessException) { return Forbid(); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (OverflowException) { return BadRequest(new { error = "مبلغ مصرف خارج از محدوده است." }); }
        catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }
    }
}
