using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fanasa.AccessManagement.Web.Platform;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Fanasa.AccessManagement.Web.Pages;

[Authorize(AuthenticationSchemes = CookieAuthenticationDefaults.AuthenticationScheme, Policy = "PlatformAdmin")]
[RequestSizeLimit(16_384)]
public sealed class ProductRegistrationModel(PlatformRegistry registry, IDataProtectionProvider protection) : PageModel
{
    public const string ReceiptPurpose = "Fanasa.ProductRegistration.Preview.v1";
    private readonly IDataProtector protector = protection.CreateProtector(ReceiptPurpose);
    [BindProperty] public ProductRegistrationInput Input { get; set; } = new();
    [BindProperty, StringLength(12_000)] public string? PreviewReceipt { get; set; }
    public PlatformTenant[] Tenants { get; private set; } = [];
    public PlatformProduct[] Products { get; private set; } = [];
    public bool IsPreview { get; private set; }
    public PlatformProduct? Created { get; private set; }
    public string CenterName => CapabilityCatalog.All.FirstOrDefault(x => x.Audience == Input.Center)?.Name ?? "";
    public string TenantName => Tenants.FirstOrDefault(x => x.Id == Input.TenantId)?.Name ?? "";

    public IActionResult OnGet(Guid? created)
    {
        if (!PlatformAuthorization.IsAdmin(User)) return Forbid();
        Load();
        Created = Products.SingleOrDefault(x => x.Id == created);
        return Page();
    }

    public IActionResult OnPostPreview()
    {
        if (!PlatformAuthorization.IsAdmin(User)) return Forbid();
        Load();
        PreviewReceipt = null;
        if (!ValidateInput()) return Invalid(400);
        if (Products.Any(x => x.Key == Input.Key)) return ConflictPage();
        PreviewReceipt = protector.Protect(JsonSerializer.Serialize(new RegistrationReceipt(Actor(),
            Fingerprint(), DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds())));
        IsPreview = true;
        return Page();
    }

    public IActionResult OnPostRegister()
    {
        if (!PlatformAuthorization.IsAdmin(User)) return Forbid();
        Load();
        if (!ValidateInput()) return Invalid(400);
        RegistrationReceipt? receipt;
        try { receipt = JsonSerializer.Deserialize<RegistrationReceipt>(protector.Unprotect(PreviewReceipt ?? "")); }
        catch (Exception ex) when (ex is CryptographicException or JsonException or ArgumentException)
        { return ReceiptError(); }
        if (receipt is null || receipt.Actor != Actor() || receipt.Expires <= DateTimeOffset.UtcNow.ToUnixTimeSeconds()
            || receipt.Fingerprint != Fingerprint()) return ReceiptError();
        try
        {
            Dictionary<string, string>? attributes = string.IsNullOrEmpty(Input.BoardUrl) ? null
                : new() { ["entrypoint.kanban.url"] = Input.BoardUrl };
            var product = registry.Create(new(Guid.Empty, Input.Key, Input.DisplayName, Input.Audience,
                Input.Center, Input.Url, null, Input.TenantId, Input.Owner, attributes), Actor());
            return RedirectToPage(new { created = product.Id });
        }
        catch (InvalidOperationException) { return ConflictPage(); }
        catch (ArgumentException)
        {
            ModelState.AddModelError("", "اطلاعات ثبت معتبر نیست یا سازمان تغییر کرده است. دوباره بررسی کنید.");
            return Invalid(400);
        }
    }

    private void Load()
    {
        Response.Headers.CacheControl = "no-store";
        Tenants = registry.Tenants();
        Products = registry.Products();
    }
    private string Actor() => User.FindFirstValue("sub") ?? User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "";
    private string Fingerprint() => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(Input))));
    private bool ValidateInput()
    {
        if (Actor().Length == 0) ModelState.AddModelError("", "نشست معتبر نیست. دوباره وارد شوید.");
        var errors = new List<ValidationResult>();
        Validator.TryValidateObject(Input, new ValidationContext(Input), errors, true);
        foreach (var error in errors)
            ModelState.AddModelError("Input." + (error.MemberNames.FirstOrDefault() ?? ""), error.ErrorMessage ?? "اطلاعات معتبر نیست.");
        if (Input.Key?.StartsWith("developer-", StringComparison.OrdinalIgnoreCase) == true)
            ModelState.AddModelError("Input.Key", "محصول‌های استودیو توسعه از مسیر اختصاصی خود ثبت می‌شوند.");
        if (!CapabilityCatalog.All.Any(x => x.Audience == Input.Center)) ModelState.AddModelError("Input.Center", "مرکز رسمی را انتخاب کنید.");
        if (!Tenants.Any(x => x.Id == Input.TenantId)) ModelState.AddModelError("Input.TenantId", "سازمان فعال را انتخاب کنید.");
        if (!SafeUrl(Input.Url, out var canonical)) ModelState.AddModelError("Input.Url", "نشانی HTTPS مصوب، بدون رمز، query یا fragment لازم است.");
        if (!string.IsNullOrEmpty(Input.BoardUrl) && (!SafeUrl(Input.BoardUrl, out var board) || canonical is null
            || !string.Equals(board?.Host, canonical.Host, StringComparison.OrdinalIgnoreCase)))
            ModelState.AddModelError("Input.BoardUrl", "لینک کانبان باید امن و روی همان دامنهٔ سامانه باشد.");
        return ModelState.IsValid;
    }
    public static bool SafeUrl(string? value, out Uri? uri) => Uri.TryCreate(value, UriKind.Absolute, out uri)
        && uri.Scheme == "https" && ManagedApplicationUrls.IsManagedHost(uri.IdnHost)
        && uri.Port == 443 && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0;
    private IActionResult Invalid(int status) { Response.StatusCode = status; IsPreview = false; return Page(); }
    private IActionResult ReceiptError()
    {
        ModelState.AddModelError("", "پیش‌نمایش معتبر نیست، تغییر کرده یا منقضی شده است. دوباره پیش‌نمایش بگیرید.");
        return Invalid(400);
    }
    private IActionResult ConflictPage()
    {
        ModelState.AddModelError("", "این کلید قبلاً ثبت شده است. ثبت دوباره، محصول موجود را تغییر نمی‌دهد.");
        return Invalid(409);
    }
    private sealed record RegistrationReceipt(string Actor, string Fingerprint, long Expires);
}

public sealed record ProductRegistrationInput
{
    [Required, StringLength(90), RegularExpression("^[a-z0-9][a-z0-9.-]{2,89}$")]
    public string Key { get; set; } = "";
    [Required, StringLength(160)] public string DisplayName { get; set; } = "";
    [Required, StringLength(120)] public string Audience { get; set; } = "";
    [Required, StringLength(90)] public string Center { get; set; } = "";
    public Guid TenantId { get; set; }
    [Required, StringLength(120)] public string Owner { get; set; } = "platform";
    [Required, StringLength(1024)] public string Url { get; set; } = "";
    [StringLength(1024)] public string? BoardUrl { get; set; }
}
