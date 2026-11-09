using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Platform;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using System.Net;

var builder = WebApplication.CreateBuilder(args);
#if FANASA_ORGANIZATION_HR_SECURITY
Fanasa.AccessManagement.Web.Security.OrganizationHrHosting.ConfigureOrganizationHrUpstream(builder);
#else
// The optional HR listener must never start without its service-authentication module.
if (builder.Configuration.GetValue<bool>("Organization:HrApi:Enabled"))
    throw new InvalidOperationException("Organization:HrApi cannot be enabled: its security module is unavailable in this build.");
#endif
var isDevelopment = builder.Environment.IsDevelopment();
builder.Services.AddRazorPages(options => options.Conventions.AuthorizeFolder("/"));
builder.Services.AddControllers();
builder.Services.Configure<ForwardedHeadersOptions>(options => {
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    options.KnownIPNetworks.Clear(); options.KnownProxies.Clear();
    options.KnownProxies.Add(IPAddress.Loopback); options.KnownProxies.Add(IPAddress.IPv6Loopback);
    options.KnownProxies.Add(IPAddress.Loopback.MapToIPv6());
});
builder.Services.AddDataProtection().SetApplicationName("Fanasa.AccessManagement")
    .PersistKeysToFileSystem(new DirectoryInfo(builder.Configuration["Fabric:DataProtectionKeysPath"]
        ?? Path.Combine(builder.Environment.ContentRootPath, "App_Data", "keys")));
builder.Services.AddSingleton<PlatformRegistry>();
builder.Services.AddSingleton<IAccessManagement, InMemoryAccessManagement>();
builder.Services.AddSingleton(sp => new Fanasa.AccessManagement.Web.Organization.OrganizationStore(
    builder.Configuration["Organization:DataDirectory"] ?? Path.Combine(builder.Environment.ContentRootPath, "App_Data", "organization"),
    sp.GetRequiredService<IAccessManagement>(), builder.Configuration.GetValue("Organization:RequireSubscription", !isDevelopment)));
builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
builder.Services.AddSingleton(sp => new Fanasa.AccessManagement.Web.Accounting.AccountingStore(
    builder.Configuration["Accounting:DataDirectory"] ?? Path.Combine(builder.Environment.ContentRootPath, "App_Data", "accounting"),
    sp.GetRequiredService<IAccessManagement>()));
builder.Services.AddAuthentication(options => { options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme; options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme; })
 .AddCookie(options => { options.Cookie.Name = "fanasa.access.session"; options.Cookie.HttpOnly = true; options.Cookie.SecurePolicy = CookieSecurePolicy.Always; options.Cookie.SameSite = SameSiteMode.Lax; options.LoginPath = "/login"; options.LogoutPath = "/logout"; })
 .AddOpenIdConnect(options =>
 {
    options.Authority = builder.Configuration["Authentication:Authority"] ?? throw new InvalidOperationException("Authentication:Authority is required.");
    options.ClientId = builder.Configuration["Authentication:ClientId"] ?? throw new InvalidOperationException("Authentication:ClientId is required.");
    options.ClientSecret = builder.Configuration["Authentication:ClientSecret"];
    options.ResponseType = "code";
    options.UsePkce = true;
    options.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;
    options.RequireHttpsMetadata = true;
    options.SaveTokens = false;
    // Identify the client during logout without retaining tokens in the cookie.
    options.Events.OnRedirectToIdentityProviderForSignOut = context =>
    {
        context.ProtocolMessage.ClientId = context.Options.ClientId;
        return Task.CompletedTask;
    };
    options.Scope.Clear();
    options.Scope.Add("openid");
    options.Scope.Add("profile");
 })
 .AddJwtBearer("PlatformBearer", options =>
 {
    options.Authority = builder.Configuration["Authentication:Authority"];
    options.Audience = "fanasa-access-management-web";
    options.MapInboundClaims = false;
    options.RequireHttpsMetadata = true;
    if (builder.Configuration["Platform:CaFile"] is string caFile)
    {
        options.BackchannelHttpHandler = PlatformEndpointTls.CreateHandler(caFile);
    }
 });
builder.Services.AddAuthorization(options => {
    options.AddPolicy("PlatformAdmin", policy => policy.RequireAuthenticatedUser().RequireClaim("permission", "platform.admin"));
    options.AddPolicy("PlatformService", policy =>
    policy.AddAuthenticationSchemes("PlatformBearer").RequireAuthenticatedUser()
        .RequireClaim("azp", "fanasa-developer-service")
        .RequireAssertion(context => context.User.FindFirst("scope")?.Value.Split(' ').Contains("platform.registry") == true));
});
Fanasa.AccessManagement.Web.FabricServices.AddIdentityOrganizationFabric(builder.Services, builder.Configuration, builder.Environment);
Fanasa.AccessManagement.Web.Security.OrganizationBearerSecurity.AddOrganizationBearerSecurity(builder.Services, builder.Configuration);
#if FANASA_ORGANIZATION_HR_SECURITY
Fanasa.AccessManagement.Web.Security.OrganizationHrSecurity.AddOrganizationHrSecurity(builder.Services, builder.Configuration);
#endif
builder.Services.AddHostedService<PlatformInitialMembership>();
var app = builder.Build();
app.UseForwardedHeaders();
app.UseExceptionHandler("/Error"); app.UseStaticFiles(); app.UseRouting(); app.UseAuthentication(); app.UseAuthorization();
app.MapGet("/health/live", () => Results.Ok(new { status = "Healthy" })).AllowAnonymous();
app.MapGet("/login", (HttpContext context) => Results.Challenge(new AuthenticationProperties { RedirectUri = "/" }, [OpenIdConnectDefaults.AuthenticationScheme]));
app.MapGet("/logout", async (HttpContext context) => { await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme); await context.SignOutAsync(OpenIdConnectDefaults.AuthenticationScheme, new AuthenticationProperties { RedirectUri = "/" }); });
app.MapControllers().RequireAuthorization(); app.MapRazorPages(); app.Run();
public partial class AccessManagementHost;
