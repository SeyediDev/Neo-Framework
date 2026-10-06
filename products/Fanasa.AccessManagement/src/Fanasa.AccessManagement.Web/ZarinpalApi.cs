using Fanasa.AccessManagement.Web.Organization;
using Fanasa.AccessManagement.Web.Payments;
using Fanasa.AccessManagement.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fanasa.AccessManagement.Web.Api;

public sealed record StartPayment(string RequestId, decimal Amount, Guid? InvoiceId);
[ApiController, Authorize, Route("api/payments/zarinpal")]
public sealed class ZarinpalApi(ZarinpalPayments payments) : ControllerBase
{
    [HttpGet("tenants/{tenantId:guid}")]
    public IActionResult List(Guid tenantId) => TenantAuthorization.Allows(User, tenantId, "billing.read") ? Ok(payments.List(tenantId)) : Forbid();
    [HttpPost("tenants/{tenantId:guid}"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Begin(Guid tenantId, StartPayment command, CancellationToken cancellationToken)
    {
        if (!TenantAuthorization.Allows(User, tenantId, "billing.write")) return Forbid();
        try
        {
            var intent = await payments.Begin(tenantId, OrgAuthorization.Subject(User), command.RequestId, command.Amount, command.InvoiceId, cancellationToken);
            return Ok(new { intent.Id, intent.State, intent.Amount, intent.Sandbox, paymentUrl = intent.State == "pending" ? ZarinpalGateway.PaymentUrl(intent) : null });
        }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        { return StatusCode(503, new { error = "پاسخ درگاه قطعی نیست؛ برای جلوگیری از پرداخت تکراری، درخواست را بررسی کنید." }); }
    }
    [HttpPost("tenants/{tenantId:guid}/{paymentId:guid}/retry"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Retry(Guid tenantId, Guid paymentId, CancellationToken cancellationToken)
    {
        if (!TenantAuthorization.Allows(User, tenantId, "billing.write")) return Forbid();
        try
        {
            var intent = payments.Get(paymentId); if (intent.TenantId != tenantId) return NotFound();
            if (intent.Authority is null) return Conflict(new { error = "درخواست نامشخص باید با گزارش پذیرنده تطبیق داده شود." });
            var completed = await payments.Complete(paymentId, intent.Authority, "OK", cancellationToken);
            return Ok(new { completed.Id, completed.State, completed.RefId });
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        { return StatusCode(503, new { error = "تأیید درگاه موقتاً در دسترس نیست." }); }
    }
    [HttpPost("tenants/{tenantId:guid}/{paymentId:guid}/reconcile"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Reconcile(Guid tenantId, Guid paymentId, CancellationToken cancellationToken)
    {
        if (!TenantAuthorization.Allows(User, tenantId, "billing.write")) return Forbid();
        try
        {
            var intent = await payments.Reconcile(tenantId, paymentId, OrgAuthorization.Subject(User), cancellationToken);
            return Ok(new { intent.Id, intent.State, intent.ProviderStatus, intent.RefId, intent.CheckedAt });
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        { return StatusCode(503, new { error = "استعلام درگاه در دسترس نیست؛ رزرو پرداخت حفظ شد." }); }
    }
    [AllowAnonymous, HttpGet("callback")]
    public async Task<IActionResult> Callback(Guid paymentId, string Authority, string Status, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers["Referrer-Policy"] = "no-referrer";
        Response.Headers["Content-Security-Policy"] = "default-src 'none'; style-src 'unsafe-inline'; base-uri 'none'; frame-ancestors 'none'";
        string message;
        try
        {
            var intent = await payments.Complete(paymentId, Authority, Status, cancellationToken);
            message = intent.State == "paid" ? "پرداخت تأیید و در حساب ثبت شد. کد پیگیری: " + intent.RefId
                : "پرداخت ثبت نشد. برای بررسی وضعیت به حساب بازگردید.";
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or KeyNotFoundException or HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        { message = "پرداخت در حساب ثبت نشده است. وضعیت را در حساب بررسی و تأیید را دوباره اجرا کنید."; }
        return Content("<!doctype html><html lang=\"fa\" dir=\"rtl\"><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><title>نتیجه پرداخت</title><body style=\"font-family:sans-serif;max-width:42rem;margin:4rem auto;padding:2rem\"><h1>نتیجه پرداخت زرین‌پال</h1><p>" + message + "</p><a href=\"/Accounting\">بازگشت به حساب</a></body></html>", "text/html; charset=utf-8");
    }
}
