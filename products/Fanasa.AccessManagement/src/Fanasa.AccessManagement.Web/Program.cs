using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Platform;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using System.Security.Cryptography.X509Certificates;

var builder = WebApplication.CreateBuilder(args);
var isDevelopment = builder.Environment.IsDevelopment();
builder.Services.AddRazorPages(options => options.Conventions.AuthorizeFolder("/"));
builder.Services.AddControllers();
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
 .AddCookie(options => { options.Cookie.Name = "fanasa.access.session"; options.Cookie.HttpOnly = true; options.Cookie.SecurePolicy = isDevelopment ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always; options.Cookie.SameSite = SameSiteMode.Lax; options.LoginPath = "/login"; options.LogoutPath = "/logout"; })
 .AddOpenIdConnect(options =>
 {
    options.Authority = builder.Configuration["Authentication:Authority"] ?? throw new InvalidOperationException("Authentication:Authority is required.");
    options.ClientId = builder.Configuration["Authentication:ClientId"] ?? throw new InvalidOperationException("Authentication:ClientId is required.");
    options.ClientSecret = builder.Configuration["Authentication:ClientSecret"];
    options.ResponseType = "code";
    options.UsePkce = true;
    options.PushedAuthorizationBehavior = PushedAuthorizationBehavior.Disable;
    options.RequireHttpsMetadata = !isDevelopment;
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
        var ca = X509CertificateLoader.LoadCertificateFromFile(caFile);
        options.BackchannelHttpHandler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
        {
            if (certificate is null || (errors & System.Net.Security.SslPolicyErrors.RemoteCertificateNameMismatch) != 0) return false;
            using var chain = new X509Chain(); chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Add(ca); chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            return chain.Build(certificate);
        } };
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
builder.Services.AddHostedService<PlatformInitialMembership>();
var app = builder.Build();
app.UseExceptionHandler("/Error"); app.UseStaticFiles(); app.UseRouting(); app.UseAuthentication(); app.UseAuthorization();
app.MapGet("/health/live", () => Results.Ok(new { status = "Healthy" })).AllowAnonymous();
app.MapGet("/login", (HttpContext context) => Results.Challenge(new AuthenticationProperties { RedirectUri = "/" }, [OpenIdConnectDefaults.AuthenticationScheme]));
app.MapGet("/logout", async (HttpContext context) => { await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme); await context.SignOutAsync(OpenIdConnectDefaults.AuthenticationScheme, new AuthenticationProperties { RedirectUri = "/" }); });
app.MapControllers().RequireAuthorization(); app.MapRazorPages(); app.Run();
public partial class AccessManagementHost;
