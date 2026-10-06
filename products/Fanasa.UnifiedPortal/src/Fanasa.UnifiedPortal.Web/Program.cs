using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Fanasa.UnifiedPortal.Web;

var builder = WebApplication.CreateBuilder(args);
var isDevelopment = builder.Environment.IsDevelopment();

builder.Services.AddRazorPages();
builder.Services.AddHttpClient("platform-control-token");
builder.Services.AddHttpClient("platform-control-catalog");
builder.Services.AddScoped<IPlatformCatalogClient, PlatformCatalogClient>();
builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
})
.AddCookie(options =>
{
    options.Cookie.Name = "fanasa.portal.session";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = isDevelopment ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.LoginPath = "/login";
    options.LogoutPath = "/logout";
})
.AddOpenIdConnect(options =>
{
    options.Authority = builder.Configuration["Authentication:Authority"]
        ?? throw new InvalidOperationException("Authentication:Authority is required.");
    options.ClientId = builder.Configuration["Authentication:ClientId"]
        ?? throw new InvalidOperationException("Authentication:ClientId is required.");
    options.ClientSecret = builder.Configuration["Authentication:ClientSecret"];
    options.ResponseType = "code";
    options.UsePkce = true;
    options.RequireHttpsMetadata = !isDevelopment;
    options.SaveTokens = false;
    // Identify the client during logout without retaining tokens in the cookie.
    options.Events.OnRedirectToIdentityProviderForSignOut = context =>
    {
        context.ProtocolMessage.ClientId = context.Options.ClientId;
        return Task.CompletedTask;
    };
    options.GetClaimsFromUserInfoEndpoint = true;
    options.Scope.Clear();
    options.Scope.Add("openid");
    options.Scope.Add("profile");
});

builder.Services.AddAuthorization();
var app = builder.Build();
app.UseExceptionHandler("/Error");
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/health/live", () => Results.Ok(new { status = "Healthy" })).AllowAnonymous();
app.MapGet("/login", (HttpContext context) =>
    Results.Challenge(new AuthenticationProperties { RedirectUri = "/" }, [OpenIdConnectDefaults.AuthenticationScheme]));
app.MapGet("/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    await context.SignOutAsync(OpenIdConnectDefaults.AuthenticationScheme,
        new AuthenticationProperties { RedirectUri = "/" });
});
app.MapRazorPages();
app.Run();

public partial class PortalHost;
