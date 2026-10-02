using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;

var builder = WebApplication.CreateBuilder(args);
var authority = builder.Configuration["FanasaSso:Authority"] ?? "http://127.0.0.1:18080/realms/fanasa";
var clientId = builder.Configuration["FanasaSso:ClientId"] ?? "fanasa-web";
var requireHttps = builder.Configuration.GetValue("FanasaSso:RequireHttpsMetadata", false);

builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
})
.AddCookie(options =>
{
    options.Cookie.Name = "fanasa.sso.session";
    options.Cookie.HttpOnly = true;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    options.LoginPath = "/login";
    options.LogoutPath = "/logout";
})
.AddOpenIdConnect(options =>
{
    options.Authority = authority;
    options.ClientId = clientId;
    options.ResponseType = "code";
    options.UsePkce = true;
    options.SaveTokens = false;
    options.RequireHttpsMetadata = requireHttps;
    options.GetClaimsFromUserInfoEndpoint = true;
    options.Scope.Clear();
    options.Scope.Add("openid");
    options.Scope.Add("profile");
    options.Scope.Add("email");
});

builder.Services.AddAuthorization();
var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "neo-fanasa-sso" }));
app.MapGet("/", (HttpContext context) => Results.Content(Home(context.User), "text/html; charset=utf-8"));
app.MapGet("/login", (HttpContext context) => Results.Challenge(new AuthenticationProperties { RedirectUri = "/" }, [OpenIdConnectDefaults.AuthenticationScheme]));
app.MapGet("/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    await context.SignOutAsync(OpenIdConnectDefaults.AuthenticationScheme, new AuthenticationProperties { RedirectUri = "/" });
});
app.MapGet("/me", (HttpContext context) => context.User.Identity?.IsAuthenticated == true
    ? Results.Ok(context.User.Claims.ToDictionary(x => x.Type, x => x.Value))
    : Results.Unauthorized());

app.Run();

const string Css = "body{margin:0;min-height:100vh;display:grid;place-items:center;background:linear-gradient(135deg,#101828,#173b5b 55%,#14b8a6);font-family:Tahoma,Arial,sans-serif;color:#fff}main{width:min(90vw,520px);padding:56px 42px;text-align:center;border:1px solid #ffffff33;border-radius:28px;background:#071525aa;box-shadow:0 24px 80px #0005;backdrop-filter:blur(16px)}.mark{width:72px;height:72px;margin:0 auto 24px;border-radius:22px;display:grid;place-items:center;background:linear-gradient(135deg,#f5b942,#f97316);font-size:42px;font-weight:900;color:#10243a}.eyebrow{font-size:12px;letter-spacing:3px;color:#8de1d3}h1{font-size:32px;line-height:1.45;margin:20px 0 14px}.lead{color:#c9d8e6;line-height:2}.button{display:inline-block;margin-top:22px;padding:14px 28px;border-radius:14px;background:#f5b942;color:#10243a;text-decoration:none;font-weight:700}.secondary{background:#8de1d3}.note{margin-top:34px;color:#9db1c5;font-size:12px}";

static string Home(System.Security.Claims.ClaimsPrincipal user)
{
    var authenticated = user.Identity?.IsAuthenticated == true;
    var title = authenticated ? $"خوش آمدی، {System.Net.WebUtility.HtmlEncode(user.Identity?.Name ?? "کاربر فناسا")}" : "ورود امن با SSO فناسا";
    var action = authenticated ? "<a class='button secondary' href='/logout'>خروج امن</a>" : "<a class='button' href='/login'>ورود با SSO فناسا</a>";
    return $"""<!doctype html><html lang='fa' dir='rtl'><head><meta charset='utf-8'><meta name='viewport' content='width=device-width,initial-scale=1'><title>فناسا | SSO</title><style>{Css}</style></head><body><main><div class='mark'>ف</div><p class='eyebrow'>FANASA CENTRAL IDENTITY</p><h1>{title}</h1><p class='lead'>یک ورود امن و یکپارچه برای سامانه‌های فناسا و محصولات Neo.</p>{action}<p class='note'>احراز هویت توسط Keycloak مرکزی انجام می‌شود.</p></main></body></html>""";
}

