using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using Fanasa.UnifiedPortal.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// Disposable test host. The production web project never references this assembly.
static class WorkspacePreview
{
    internal const string Address = "http://127.0.0.1:5204";
    internal const string ConsumerSubject = "preview-consumer";
    internal const string AdminSubject = "preview-admin";
    internal const string DeniedSubject = "preview-denied";
    internal static readonly Guid ConsumerA = Guid.Parse("11111111-1111-4111-8111-111111111111");
    internal static readonly Guid ConsumerB = Guid.Parse("22222222-2222-4222-8222-222222222222");
    internal static readonly Guid Producer = Guid.Parse("33333333-3333-4333-8333-333333333333");
    internal static readonly PlatformOrganizationView[] Organizations =
    [
        new(ConsumerA, "سازمان آفتاب", "synthetic-aftab"),
        new(ConsumerB, "سازمان سپهر", "synthetic-sepehr")
    ];
    internal static bool IsMember(string subject, Guid organizationId)
        => (subject is ConsumerSubject or AdminSubject) && Organizations.Any(x => x.Id == organizationId);

    public static async Task RunAsync(bool allowAdminReview)
    {
        var contentRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../src/Fanasa.UnifiedPortal.Web"));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = contentRoot,
            ApplicationName = typeof(PortalHost).Assembly.GetName().Name,
            EnvironmentName = "Development"
        });
        // A disposable browser fixture must not write to the Windows Event Log.
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.UseUrls(Address);
        // These addresses are intercepted by the handlers below; no transport is installed.
        builder.Configuration["PlatformControlCenter:BaseUrl"] = PreviewCatalogHandler.CatalogBase;
        builder.Configuration["PlatformControlCenter:Authority"] = PreviewCatalogHandler.Authority;
        builder.Configuration["PlatformControlCenter:ClientId"] = PreviewCatalogHandler.ClientId;
        builder.Configuration["PlatformControlCenter:ClientSecret"] = PreviewCatalogHandler.ClientSecret;
        builder.Configuration["PlatformControlCenter:Scope"] = "platform.catalog";
        builder.Services.AddHttpClient("platform-control-token")
            .ConfigurePrimaryHttpMessageHandler(() => new PreviewCatalogHandler());
        builder.Services.AddHttpClient("platform-control-catalog")
            .ConfigurePrimaryHttpMessageHandler(() => new PreviewCatalogHandler());
        builder.Services.AddScoped<IPlatformCatalogClient, PlatformCatalogClient>();
        builder.Services.AddSingleton<IWorkspaceJourneySource, SyntheticWorkspaceJourneySource>();
        builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
            {
                options.Cookie.Name = "fanasa.portal.synthetic-preview";
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.LoginPath = "/preview/bootstrap/consumer";
                options.ExpireTimeSpan = TimeSpan.FromHours(1);
                options.SlidingExpiration = false;
                options.Events.OnRedirectToAccessDenied = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status403Forbidden;
                    return Task.CompletedTask;
                };
            });
        builder.Services.AddAuthorization(options => options.AddPolicy("PlatformAdmin", policy =>
            policy.RequireAuthenticatedUser().RequireRole("PlatformAdmin").RequireClaim("permission", "platform.admin")));
        builder.Services.AddRazorPages().AddApplicationPart(typeof(PortalHost).Assembly);
        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            var remote = context.Connection.RemoteIpAddress;
            if (remote?.IsIPv4MappedToIPv6 == true) remote = remote.MapToIPv4();
            if (remote is null || !IPAddress.IsLoopback(remote))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            context.Response.Headers.CacheControl = "no-store";
            await next();
        });
        app.UseStaticFiles();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapGet("/health/live", () => Results.Ok(new { status = "Healthy", syntheticPreview = true }));
        app.MapGet("/login", () => Results.Redirect("/preview/bootstrap/consumer"));
        app.MapGet("/logout", async (HttpContext context) =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect("/");
        });
        app.MapGet("/preview/bootstrap/{mode}", async (HttpContext context, string mode) =>
        {
            var subject = mode switch
            {
                "consumer" => ConsumerSubject,
                "admin" when allowAdminReview => AdminSubject,
                "denied" => DeniedSubject,
                _ => null
            };
            if (subject is null) return Results.NotFound();
            var claims = new List<Claim>
            {
                new("sub", subject), new(ClaimTypes.NameIdentifier, subject),
                new("preferred_username", mode == "admin" ? "مدیر نمونه" : mode == "denied" ? "حساب بدون دسترسی" : "همکار نمونه"),
                new("fanasa.preview", "synthetic")
            };
            // A real role is granted only by the explicitly enabled review bootstrap.
            if (mode == "admin")
            {
                claims.Add(new(ClaimTypes.Role, "PlatformAdmin"));
                claims.Add(new("permission", "platform.admin"));
            }
            await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme)),
                new AuthenticationProperties { IsPersistent = false, AllowRefresh = false });
            return Results.Redirect("/Workspace?tenantId=" + ConsumerA);
        });
        app.MapGet("/preview/session", (HttpContext context) => Results.Ok(new
        {
            syntheticPreview = true,
            authenticated = context.User.Identity?.IsAuthenticated == true,
            subject = context.User.FindFirstValue("sub"),
            platformAdmin = context.User.IsInRole("PlatformAdmin")
        }));
        app.MapGet("/preview/admin-check", () => Results.Ok(new { syntheticReview = true }))
            .RequireAuthorization("PlatformAdmin");
        app.MapGet("/preview/product/{key}", (HttpContext context, string key) =>
        {
            if (context.User.Identity?.IsAuthenticated != true) return Results.Unauthorized();
            var subject = context.User.FindFirstValue("sub") ?? "";
            if (subject is not (ConsumerSubject or AdminSubject)) return Results.Forbid();
            var product = Products(ConsumerA).Concat(Products(ConsumerB)).FirstOrDefault(x => x.Key == key);
            if (product is null) return Results.NotFound();
            return Results.Content($"<!doctype html><html lang=\"fa\" dir=\"rtl\"><meta charset=\"utf-8\"><title>{product.DisplayName}</title><h1>{product.DisplayName}</h1><p>این مقصد صرفاً برای بررسی نمونه فضای کار است.</p><a href=\"/Workspace?tenantId={ConsumerA}\">بازگشت به فضای کار</a></html>", "text/html; charset=utf-8");
        });
        app.MapRazorPages();
        Console.WriteLine("Synthetic workspace preview: " + Address + "/preview/bootstrap/consumer");
        Console.WriteLine("Denied catalog preview: " + Address + "/preview/bootstrap/denied");
        Console.WriteLine(allowAdminReview
            ? "Synthetic admin review: " + Address + "/preview/bootstrap/admin"
            : "Admin bootstrap disabled. Enable explicitly with --synthetic-admin-review.");
        await app.RunAsync();
    }

    internal static PlatformProductView[] Products(Guid consumer)
    {
        if (!Organizations.Any(x => x.Id == consumer)) return [];
        PlatformProductView Product(string id, string key, string name, string center) =>
            new(Guid.Parse(id), key, name, "app", center, Address + "/preview/product/" + key, null,
                Producer, "ارائه‌دهنده نمونه فن آسا", new() { ["preview"] = "synthetic" });
        var document = Product("44444444-4444-4444-8444-444444444444", "document-insights", "تحلیل اسناد", "fanasa.dana");
        return consumer == ConsumerA
            ? [document, Product("55555555-5555-4555-8555-555555555555", "organization-fabric", "ساختار سازمانی", "fanasa.ayin")]
            : [document, Product("66666666-6666-4666-8666-666666666666", "service-automation", "خودکارسازی خدمات", "fanasa.rayan")];
    }
}

sealed class PreviewCatalogHandler : HttpMessageHandler
{
    internal const string CatalogBase = "https://catalog.workspace-preview.invalid";
    internal const string Authority = "https://identity.workspace-preview.invalid/realms/synthetic";
    internal const string ClientId = "synthetic-portal-preview";
    internal const string ClientSecret = "disposable-test-value";
    private const string Token = "synthetic-catalog-token";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var uri = request.RequestUri;
        if (uri is null || !uri.IsAbsoluteUri) return new(HttpStatusCode.BadRequest);
        if (request.Method == HttpMethod.Post && uri.AbsoluteUri == Authority + "/protocol/openid-connect/token")
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            var form = QueryHelpers.ParseQuery(body);
            return request.Content?.Headers.ContentType?.MediaType == "application/x-www-form-urlencoded"
                && form.Count == 4 && HasValue(form, "grant_type", "client_credentials") && HasValue(form, "client_id", ClientId)
                && HasValue(form, "client_secret", ClientSecret) && HasValue(form, "scope", "platform.catalog")
                ? Json(new { access_token = Token }) : new(HttpStatusCode.BadRequest);
        }
        var authorization = request.Headers.Authorization;
        if (request.Method != HttpMethod.Get || uri.GetLeftPart(UriPartial.Authority) != CatalogBase
            || authorization is null || authorization.Scheme != "Bearer" || authorization.Parameter != Token)
            return new(HttpStatusCode.Forbidden);
        var query = QueryHelpers.ParseQuery(uri.Query);
        if (!query.TryGetValue("subject", out var subjectValues) || subjectValues.Count != 1)
            return new(HttpStatusCode.Forbidden);
        var subject = subjectValues.ToString();
        if (subject is not (WorkspacePreview.ConsumerSubject or WorkspacePreview.AdminSubject))
            return new(HttpStatusCode.Forbidden);
        if (uri.AbsolutePath == "/api/platform/application-tenants" && query.Count == 1)
            return Json(WorkspacePreview.Organizations);
        if (uri.AbsolutePath == "/api/platform/products" && query.Count == 2 && query.TryGetValue("tenant", out var tenantValues)
            && tenantValues.Count == 1 && Guid.TryParse(tenantValues.ToString(), out var tenant) && WorkspacePreview.IsMember(subject, tenant))
            return Json(WorkspacePreview.Products(tenant));
        return new(HttpStatusCode.Forbidden);
    }

    private static HttpResponseMessage Json<T>(T value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
    private static bool HasValue(Dictionary<string, Microsoft.Extensions.Primitives.StringValues> values, string key, string expected)
        => values.TryGetValue(key, out var value) && value.Count == 1 && value == expected;
}

// Immutable seed data, scoped to one validated consumer organization per response.
sealed class SyntheticWorkspaceJourneySource : IWorkspaceJourneySource
{
    public bool IsSyntheticPreview => true;

    public Task<WorkspaceJourneyData> GetAsync(string subject, Guid organizationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!WorkspacePreview.IsMember(subject, organizationId))
            return Task.FromResult(new WorkspaceJourneyData([], [], [], new("forbidden", "", "", "IRR", "0", []), organizationId, subject));
        var isAftab = organizationId == WorkspacePreview.ConsumerA;
        var plans = new WorkspacePlan[]
        {
            new("standard", "استاندارد", "1200000", "IRR", "ماهانه", "۵۰۰ فراخوان در ماه"),
            new("business", "سازمانی", "3600000", "IRR", "ماهانه", "۲٬۰۰۰ فراخوان در ماه")
        };
        WorkspaceOffer[] catalog =
        [
            new("document-insights", "document-insights", "تحلیل اسناد", "fanasa.dana", "استخراج و جست‌وجوی اطلاعات از اسناد سازمان شما.", plans),
            new("service-automation", "service-automation", "خودکارسازی خدمات", "fanasa.rayan", "اجرای فرایندهای تکرارشونده با اتصال به خدمات سازمان.",
                [new("starter", "شروع", "1800000", "IRR", "ماهانه", "۱٬۰۰۰ اجرای فرایند در ماه")]),
            new("managed-compute", "managed-compute", "پردازش مدیریت‌شده", "fanasa.bonyan", "ظرفیت پردازشی برای اجرای کارهای سازمان.",
                [new("team", "تیمی", "5400000", "IRR", "ماهانه", "۱۲۰ ساعت پردازش در ماه"),
                 new("capacity-review", "ظرفیت توسعه", "9007199254740993.0100", "IRR", "ماهانه", "نمونه بررسی نمایش مبلغ دقیق")])
        ];
        var suffix = isAftab ? "aftab" : "sepehr";
        WorkspaceRequest[] requests =
        [
            new("request-doc-" + suffix, "document-insights", "تحلیل اسناد", "standard", "استاندارد", organizationId,
                "approved", "2026-10-08T08:30:00Z", "جست‌وجوی اسناد واحد عملیات",
                [new("pending", "درخواست ثبت شد", "2026-10-08T08:30:00Z"), new("approved", "درخواست تأیید شد", "2026-10-08T10:00:00Z")], subject),
            new("request-compute-" + suffix, "managed-compute", "پردازش مدیریت‌شده", "team", "تیمی", organizationId,
                "pending", "2026-10-11T09:15:00Z", "پردازش گزارش‌های ماهانه",
                [new("pending", "درخواست ثبت شد", "2026-10-11T09:15:00Z"), new("pending", "در انتظار بررسی", "2026-10-11T09:15:00Z")], subject),
            new("request-automation-" + suffix, "service-automation", "خودکارسازی خدمات", "starter", "شروع", organizationId,
                "rejected", "2026-10-09T07:20:00Z", "نیاز به تکمیل شرح کاربرد",
                [new("pending", "درخواست ثبت شد", "2026-10-09T07:20:00Z"), new("rejected", "برای تکمیل اطلاعات بازگردانده شد", "2026-10-09T12:00:00Z")], subject)
        ];
        WorkspacePublication[] publications =
        [
            new("publication-" + suffix, isAftab ? "خدمت گزارش عملیات" : "خدمت گزارش پشتیبانی", "fanasa.rayan", "گزارش‌های سازمان در یک مسیر مشخص و قابل پیگیری.",
                WorkspacePreview.Address + "/preview/product/service-automation", organizationId, "pending", "2026-10-10T11:00:00Z",
                [new("pending", "اطلاعات خدمت ثبت شد", "2026-10-10T11:00:00Z"), new("pending", "در انتظار بررسی انتشار", "2026-10-10T11:00:00Z")], subject)
        ];
        var usage = new WorkspaceUsage("ready", "مهر ۱۴۰۵", "2026-10-12T08:00:00Z", "IRR", isAftab ? "4080000" : "2160000",
            isAftab
                ? [new("document-insights", "تحلیل اسناد", "100", "فراخوان", "1200000", "۵۰۰ فراخوان"),
                   new("organization-fabric", "ساختار سازمانی", "24", "رویداد", "2880000", "۱٬۰۰۰ رویداد")]
                : [new("document-insights", "تحلیل اسناد", "30", "فراخوان", "360000", "۵۰۰ فراخوان"),
                   new("service-automation", "خودکارسازی خدمات", "150", "اجرا", "1800000", "۱٬۰۰۰ اجرا")]);
        return Task.FromResult(new WorkspaceJourneyData(catalog, requests, publications, usage, organizationId, subject));
    }
}
