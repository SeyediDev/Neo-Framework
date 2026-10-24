using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Fanasa.AccessManagement.Web.Pages;

[Authorize(AuthenticationSchemes = CookieAuthenticationDefaults.AuthenticationScheme, Policy = "PlatformAdmin")]
public sealed class OrganizationHrModel(IConfiguration configuration, IWebHostEnvironment environment) : PageModel
{
    public const string CanonicalBaseUrl = "https://api.fanasa.net.local/development/organization-hr/v1";
    public string ApiBaseUrl { get; private set; } = CanonicalBaseUrl;
    public string? Authority { get; private set; }
    public string? ClientId { get; private set; }
    public string? Audience { get; private set; }
    public string ReadScope { get; private set; } = "organization.hr.read";
    public string WriteScope { get; private set; } = "organization.hr.write";
    public bool ReadScopeConfigured { get; private set; }
    public bool WriteScopeConfigured { get; private set; }
    public bool ApiEnabled { get; private set; }
    public bool MeteringEnabled { get; private set; }
    public bool GatewayConnected { get; private set; }
    public DateTimeOffset? GatewayVerifiedAt { get; private set; }
    public string? ReleaseId { get; private set; }
    public string PipelineStatus { get; private set; } = "unknown";
    public string? PipelineUrl { get; private set; }
    public string? RepositoryUrl { get; private set; }
    public string? DevelopmentTenantId { get; private set; }
    public DateTimeOffset? DevelopmentGrantExpiresAt { get; private set; }
    public string? LastSuccessfulRun { get; private set; }
    public DateTimeOffset? PipelineVerifiedAt { get; private set; }
    public bool PipelineVerified => PipelineStatus == "verified" && LastSuccessfulRun is not null && PipelineVerifiedAt.HasValue;
    public bool HasOpenApi { get; private set; }
    public string? MetadataNotice { get; private set; }
    public string PipelineStatusLabel => PipelineVerified ? "اجرای موفق با مدرک ثبت‌شده" : PipelineStatus switch
    {
        "prepared" => "فایل پایپ‌لاین آماده است؛ اجرای خودکار تأیید نشده",
        "configured" => "پیکربندی ثبت شده؛ اجرای موفق تأیید نشده",
        "running" => "اجرای گزارش‌شده در حال انجام است؛ نتیجه هنوز تأیید نشده",
        "failed" => "آخرین وضعیت ثبت‌شده: ناموفق",
        "pending-service-identity" => "در انتظار پیکربندی هویت سرویس",
        _ => "مدرک اجرای موفق پایپ‌لاین موجود نیست"
    };

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Vary = "Cookie";
        Response.Headers.XContentTypeOptions = "nosniff";
        Response.Headers.XFrameOptions = "SAMEORIGIN";
        var settings = configuration.GetSection("Organization:HrApi");
        ApiBaseUrl = SafeHttpsUrl(settings["PublicBaseUrl"])?.TrimEnd('/') ?? CanonicalBaseUrl;
        Authority = SafeHttpsUrl(settings["Authority"]);
        ClientId = SafeText(settings["ClientId"]);
        Audience = SafeText(settings["Audience"]);
        var readScope = Scope(settings["ReadScope"]);
        var writeScope = Scope(settings["WriteScope"]);
        ReadScopeConfigured = readScope is not null;
        WriteScopeConfigured = writeScope is not null;
        ReadScope = readScope ?? ReadScope;
        WriteScope = writeScope ?? WriteScope;
        ApiEnabled = settings.GetValue<bool>("Enabled");
        MeteringEnabled = configuration.GetValue<bool>("UsageMetering:Organization:Enabled");
        HasOpenApi = System.IO.File.Exists(Path.Combine(environment.WebRootPath, "contracts", "organization-hr-v1.openapi.json"));
        var metadataPath = settings["DeploymentMetadataPath"];
        if (string.IsNullOrWhiteSpace(metadataPath))
        {
            MetadataNotice = "گزارش استقرار برای این اجرا ثبت نشده است؛ وجود این صفحه اثبات اتصال Gateway یا اجرای CI/CD نیست.";
            return;
        }
        try
        {
            await using var input = new FileStream(Path.GetFullPath(metadataPath, environment.ContentRootPath),
                FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.Asynchronous);
            if (input.Length > 32768) throw new InvalidDataException("Deployment metadata is too large.");
            using var metadata = await JsonDocument.ParseAsync(input, new JsonDocumentOptions { MaxDepth = 8 }, cancellationToken);
            var root = metadata.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Deployment metadata must be an object.");
            ReleaseId = SafeText(String(root, "releaseId"));
            PipelineStatus = String(root, "pipelineStatus") is "prepared" or "configured" or "running" or "verified" or "failed" or "pending-service-identity"
                ? String(root, "pipelineStatus")! : "unknown";
            PipelineUrl = SafeHttpsUrl(String(root, "pipelineUrl"));
            RepositoryUrl = SafeHttpsUrl(String(root, "repositoryUrl"));
            var developmentTenantId = String(root, "developmentTenantId");
            DevelopmentTenantId = Guid.TryParseExact(developmentTenantId, "D", out var tenantId)
                && tenantId != Guid.Empty && tenantId.ToString("D") == developmentTenantId ? developmentTenantId : null;
            DevelopmentGrantExpiresAt = FutureTimestamp(String(root, "developmentGrantExpiresAt"));
            LastSuccessfulRun = SafeHttpsUrl(String(root, "lastSuccessfulRun"));
            PipelineVerifiedAt = Timestamp(String(root, "pipelineVerifiedAt"));
            GatewayVerifiedAt = Timestamp(String(root, "gatewayVerifiedAt"));
            GatewayConnected = root.TryGetProperty("gatewayConnected", out var gateway)
                && gateway.ValueKind == JsonValueKind.True && GatewayVerifiedAt.HasValue && ApiEnabled;
            if (PipelineStatus == "verified" && !PipelineVerified)
                MetadataNotice = "گزارش پایپ‌لاین کامل نیست: لینک اجرای موفق و زمان تأیید باید ثبت شوند.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            MetadataNotice = "گزارش استقرار قابل خواندن یا معتبر نیست؛ وضعیت اجرا تأیید نشده است.";
        }
    }

    private static string? String(JsonElement root, string name) => root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string? SafeText(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 200
        && !value.Any(char.IsControl) ? value : null;
    private static string? Scope(string? value) => SafeText(value) is { } scope && !scope.Any(char.IsWhiteSpace) ? scope : null;
    private static string? SafeHttpsUrl(string? value) => Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Fragment)
        && string.IsNullOrEmpty(uri.Query) ? uri.AbsoluteUri : null;
    private static DateTimeOffset? Timestamp(string? value) => value is { Length: > 0 and <= 50 }
        && (value.EndsWith('Z') || System.Text.RegularExpressions.Regex.IsMatch(value, @"[+-]\d{2}:\d{2}$"))
        && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp)
        && timestamp <= DateTimeOffset.UtcNow.AddMinutes(1) ? timestamp : null;
    private static DateTimeOffset? FutureTimestamp(string? value) => value is { Length: > 0 and <= 50 }
        && (value.EndsWith('Z') || System.Text.RegularExpressions.Regex.IsMatch(value, @"[+-]\d{2}:\d{2}$"))
        && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp) ? timestamp : null;
}
