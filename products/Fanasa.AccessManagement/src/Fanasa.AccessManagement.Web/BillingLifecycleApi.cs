using System.Globalization;
using System.Text.Json;
using Fanasa.AccessManagement.Web.Accounting;
using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Persistence;
using Fanasa.AccessManagement.Web.Platform;
using Fanasa.AccessManagement.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Data.Sqlite;

namespace Fanasa.AccessManagement.Web.Api;

public sealed record ConsumerAccountEnvelope(string Subject, ConsumerAccountBody Account);
public sealed record ConsumerAccountBody(int SchemaVersion, Guid ConsumerTenantId, DateTimeOffset AsOf,
    string Status, string? Mode, string? Currency, string? WalletBalance, string? Outstanding,
    string? AvailableCredit, IReadOnlyList<ConsumerInvoiceSummary> Invoices);
public sealed record ConsumerInvoiceSummary(Guid Id, string Period, string Amount, string CreditedAmount,
    string PaidAmount, string RemainingAmount, string Status, DateTimeOffset IssuedAt,
    DateTimeOffset? From, DateTimeOffset? To);

// Account and invoice summaries expose no raw ledger, actor, reference, payment token or internal error.
public static class ConsumerAccountProjection
{
    public static ConsumerAccountBody Read(FabricDatabase database, AccountingStore store, Guid consumer,
        TimeProvider clock, Func<bool> authorize)
    {
        if (consumer == Guid.Empty) throw new ArgumentException("A consumer is required.");
        if (!ReferenceEquals(store.Database, database)) throw new InvalidOperationException("Account projection requires the same transactional Fabric store.");
        return database.ReadSnapshot(() =>
        {
            if (!authorize()) throw new UnauthorizedAccessException();
            var now = clock.GetUtcNow().ToUniversalTime();
            ConsumerAccountBody Empty(string status) => new(1, consumer, now, status, null, null, null, null, null, []);
            var document = database.ReadDocument("accounting", consumer.ToString());
            ConsumerAccountBody body;
            if (document is null) body = Empty("notRecorded");
            else
            {
                // Validate the raw representation before a decimal deserializer can round it.
                var account = BillingBalance.Deserialize(document.Json);
                BillingBalance.Validate(account, consumer);
                if (account.Entries.Length > 10000 || account.Invoices.Length > 1000) throw new InvalidDataException();
                var view = store.Read(consumer);
                if (view.WalletBalance < 0 || view.Outstanding < 0 || view.AvailableCredit < 0) throw new InvalidDataException();
                string Exact(decimal value) => value.ToString(CultureInfo.InvariantCulture);
                var invoices = account.Invoices.OrderByDescending(x => x.Period, StringComparer.Ordinal).Select(invoice =>
                {
                    var paid = BillingBalance.InvoicePaid(account, invoice.Id);
                    var credited = BillingBalance.InvoiceCredits(account, invoice.Id);
                    var remaining = BillingBalance.InvoiceRemaining(account, invoice.Id);
                    var status = account.Mode == "payg" ? "statement" : remaining == 0 ? (credited == invoice.Amount && credited > 0 ? "credited" : "paid")
                        : paid > 0 || credited > 0 ? "partiallyPaid" : "issued";
                    return new ConsumerInvoiceSummary(invoice.Id, invoice.Period, Exact(invoice.Amount), Exact(credited),
                        Exact(paid), Exact(remaining), status, invoice.IssuedAt, invoice.From, invoice.To);
                }).ToArray();
                body = new(1, consumer, now, "ready", account.Mode, account.Currency,
                    Exact(view.WalletBalance), Exact(view.Outstanding), Exact(view.AvailableCredit), invoices);
            }
            if (!authorize()) throw new UnauthorizedAccessException();
            return body;
        });
    }
}

[ApiController, Authorize(AuthenticationSchemes = "PlatformBearer")]
[Route("api/accounting/v1/consumers/{consumerTenantId:guid}/account")]
public sealed class ConsumerAccountApi(FabricDatabase database, AccountingStore store,
    PersistentAccessManagement access, PlatformRegistry registry, IConfiguration configuration, TimeProvider clock) : ControllerBase
{
    [HttpGet]
    public IActionResult Read(Guid consumerTenantId)
    {
        Response.Headers.CacheControl = "no-store";
        var client = configuration["ConsumerUsageReport:PortalClientId"];
        var product = configuration["ConsumerUsageReport:PortalProductKey"];
        var scope = configuration["ConsumerUsageReport:Scope"];
        static bool Label(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 200
            && value == value.Trim() && !value.Any(char.IsControl);
        if (!configuration.GetValue<bool>("ConsumerUsageReport:Enabled") || !Label(client) || !Label(product)
            || !Label(scope) || scope!.Any(char.IsWhiteSpace))
            return StatusCode(503, new { error = "Account report is unavailable." });
        if (Request.Query.Count != 0 || Request.Headers.ContainsKey("X-Subject") || Request.Headers.ContainsKey("X-User-Id"))
            return BadRequest(new { error = "This account read accepts no subject or query parameters." });
        var boundary = new ConsumerBillingAuthorization(HttpContext, access, registry, clock);
        bool Allows() => boundary.Allows(consumerTenantId, product!, client!, scope!);
        try
        {
            var body = ConsumerAccountProjection.Read(database, store, consumerTenantId, clock, Allows);
            if (!Allows()) return Forbid("PlatformBearer");
            return Ok(new ConsumerAccountEnvelope(User.FindSingleSubject()!, body));
        }
        catch (UnauthorizedAccessException) { return Forbid("PlatformBearer"); }
        catch (Exception error) when (error is SqliteException or IOException or InvalidDataException or JsonException
            or ArgumentException or OverflowException or InvalidOperationException or KeyNotFoundException)
        { return StatusCode(503, new { error = "Account report is unavailable." }); }
    }
}

// The finance manager uses the existing human cookie and live tenant permission; PlatformAdmin is no bypass.
[ApiController, Authorize(AuthenticationSchemes = "Cookies")]
[Route("api/accounting/v1/tenants/{tenantId:guid}")]
public sealed class BillingLifecycleApi(FabricDatabase database, AccountingStore store,
    PersistentAccessManagement access, TimeProvider clock) : ControllerBase
{
    private bool Allows(Guid tenant, string permission)
    {
        var subject = User.FindSingleSubject();
        return User.Identity?.IsAuthenticated == true && subject is not null && tenant != Guid.Empty
            && access.GetTenants().Any(x => x.Id == tenant && x.IsActive)
            && access.GetUsers(tenant).Any(x => x.IsActive && x.KeycloakSubject == subject)
            && access.GetGrants(subject).Any(x => x.TenantId == tenant && x.Permission == permission
                && (!x.ExpiresAt.HasValue || x.ExpiresAt > clock.GetUtcNow()));
    }
    [HttpGet("account")]
    public IActionResult Read(Guid tenantId)
    {
        Response.Headers.CacheControl = "no-store";
        try
        {
            var body = ConsumerAccountProjection.Read(database, store, tenantId, clock, () => Allows(tenantId, "billing.read"));
            if (!Allows(tenantId, "billing.read")) return Forbid("Cookies");
            return Ok(body);
        }
        catch (UnauthorizedAccessException) { return Forbid("Cookies"); }
        catch (Exception error) when (error is SqliteException or IOException or JsonException
            or ArgumentException or OverflowException or InvalidOperationException or KeyNotFoundException)
        { return StatusCode(503, new { error = "حساب در دسترس نیست." }); }
    }
    [HttpPost("invoices"), ValidateAntiForgeryToken, RequestSizeLimit(16384)]
    public IActionResult Issue(Guid tenantId, BillingInvoiceCommand command)
        => Write(tenantId, () => store.IssueInvoice(tenantId, command, User.FindSingleSubject()!));
    [HttpPost("credit-notes"), ValidateAntiForgeryToken, RequestSizeLimit(16384)]
    public IActionResult Credit(Guid tenantId, BillingCreditNoteCommand command)
        => Write(tenantId, () => store.ApplyCreditNote(tenantId, command, User.FindSingleSubject()!));

    private IActionResult Write(Guid tenant, Func<AccountView> execute)
    {
        Response.Headers.CacheControl = "no-store";
        try
        {
            if (!ReferenceEquals(store.Database, database)) throw new InvalidOperationException("The financial store is unavailable.");
            // Authorize in the same writer as the append, so a durable grant cannot change between them.
            return database.Transaction<IActionResult>(() =>
            {
                if (!Allows(tenant, "billing.write")) return Forbid("Cookies");
                var result = execute();
                return Ok(result);
            });
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException error) { return BadRequest(new { error = error.Message }); }
        catch (OverflowException) { return BadRequest(new { error = "مبلغ خارج از محدودهٔ دقیق است." }); }
        catch (InvalidDataException) { return Conflict(new { error = "شواهد حساب نیازمند بررسی است." }); }
        catch (InvalidOperationException error) { return Conflict(new { error = error.Message }); }
    }
}

internal static class BillingSubject
{
    internal static string? FindSingleSubject(this System.Security.Claims.ClaimsPrincipal principal)
    {
        var subjects = principal.FindAll("sub").Select(x => x.Value)
            .Concat(principal.FindAll(System.Security.Claims.ClaimTypes.NameIdentifier).Select(x => x.Value))
            .Distinct(StringComparer.Ordinal).ToArray();
        return subjects.Length == 1 && !string.IsNullOrWhiteSpace(subjects[0]) ? subjects[0] : null;
    }
}
