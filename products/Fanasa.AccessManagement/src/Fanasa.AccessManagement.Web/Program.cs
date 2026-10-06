using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Fanasa.AccessManagement.Web.Application.Access;

var builder = WebApplication.CreateBuilder(args);
var isDevelopment = builder.Environment.IsDevelopment();
builder.Services.AddRazorPages(options => options.Conventions.AuthorizeFolder("/"));
builder.Services.AddControllers();
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
 });
builder.Services.AddAuthorization();
var app = builder.Build();
app.UseExceptionHandler("/Error"); app.UseStaticFiles(); app.UseRouting(); app.UseAuthentication(); app.UseAuthorization();
app.MapGet("/health/live", () => Results.Ok(new { status = "Healthy" })).AllowAnonymous();
app.MapGet("/login", (HttpContext context) => Results.Challenge(new AuthenticationProperties { RedirectUri = "/" }, [OpenIdConnectDefaults.AuthenticationScheme]));
app.MapGet("/logout", async (HttpContext context) => { await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme); await context.SignOutAsync(OpenIdConnectDefaults.AuthenticationScheme, new AuthenticationProperties { RedirectUri = "/" }); });
app.MapControllers().RequireAuthorization(); app.MapRazorPages(); app.Run();
public partial class AccessManagementHost;
