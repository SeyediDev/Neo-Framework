using Neo.AgentOrchestration.Web;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.HttpOverrides;
using System.Net;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Services.AddRazorPages().AddMvcOptions(o => o.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true);
builder.Services.AddHttpContextAccessor();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    options.KnownProxies.Add(IPAddress.Loopback);
});
builder.Services.AddWebIdentity(builder.Configuration, builder.Environment);
builder.Services.AddAuthorization(o =>
{
    o.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAssertion(context =>
        context.User.Identity?.IsAuthenticated == true ||
        context.Resource is HttpContext http && LocalDevelopmentAccess.IsAllowed(http)).Build();
});
builder.Services.AddHttpClient<OrchestrationClient>((provider, c) =>
{
    c.BaseAddress = WebIdentity.ApiAddress(provider.GetRequiredService<IConfiguration>());
    c.Timeout = TimeSpan.FromSeconds(20);
}).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
var app = builder.Build();
// Remote handlers initialize on every request, even public pages. Use the
// final host configuration and leave cookie protection active without an IdP.
if (!WebIdentity.Configured(app.Configuration))
    app.Services.GetRequiredService<IAuthenticationSchemeProvider>().RemoveScheme(OpenIdConnectDefaults.AuthenticationScheme);
_ = WebIdentity.ApiAddress(app.Configuration);
app.UseExceptionHandler("/Error");
app.UseForwardedHeaders();
if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing")) app.UseHsts();
app.UseStaticFiles();
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self'; connect-src 'self'; style-src 'self'; img-src 'self'; object-src 'none'; frame-ancestors 'none'; base-uri 'self'; form-action 'self'";
    await next();
});
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();
app.MapGet("/health/live", () => Results.Ok(new { status = "Healthy" })).AllowAnonymous();
app.Run();

public partial class WebHost;
