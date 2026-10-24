using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Fanasa.UnifiedPortal.Web;

var builder = WebApplication.CreateBuilder(args);
var isDevelopment = builder.Environment.IsDevelopment();

builder.Services.AddRazorPages();
builder.Services.AddFanasaPlatformHttpClients();
builder.Services.AddScoped<IPlatformCatalogClient, PlatformCatalogClient>();
builder.Services.AddScoped<IServiceDiscoveryClient, ServiceDiscoveryClient>();
builder.Services.AddScoped<IWorkspaceJourneySource, NotConnectedWorkspaceJourneySource>();
// Disabled by default. Human financial reads use only the explicitly approved Gateway.
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(ConsumerBillingOptions.Read(builder.Configuration));
builder.Services.AddSingleton<ConsumerBillingSessionVault>();
builder.Services.AddScoped<IConsumerUsageHumanCredentialProvider, SessionConsumerUsageCredentialProvider>();
builder.Services.AddScoped<IWorkspaceConsumerBillingReader, GatewayWorkspaceConsumerBillingReader>();
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
    options.Events.OnSigningOut = ConsumerBillingOidc.PurgeAsync;
})
.AddOpenIdConnect(options =>
{
    options.Authority = builder.Configuration["Authentication:Authority"]
        ?? throw new InvalidOperationException("Authentication:Authority is required.");
    options.ClientId = builder.Configuration["Authentication:ClientId"]
        ?? throw new InvalidOperationException("Authentication:ClientId is required.");
    options.ClientSecret = builder.Configuration["Authentication:ClientSecret"];
    options.ResponseType = "code";
    options.MapInboundClaims = false;
    options.UsePkce = true;
    options.RequireHttpsMetadata = !isDevelopment;
    options.SaveTokens = false;
    options.Events.OnTokenValidated = ConsumerBillingOidc.CaptureAsync;
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
