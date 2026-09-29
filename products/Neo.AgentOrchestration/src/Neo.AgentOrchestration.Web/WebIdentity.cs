using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Caching.Memory;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;

namespace Neo.AgentOrchestration.Web;

public static class WebIdentity
{
    public const string ChatKey = "neo.chat";
    // Web is one durable client identity per issuer/subject, not a new work
    // owner on each sign-in. Expired sessions must not strand claimed work.
    public static string ChatId(string issuer, ClaimsPrincipal principal)
    {
        var subjects = principal.FindAll("sub").Select(c => c.Value).ToArray();
        if (subjects.Length != 1 || string.IsNullOrWhiteSpace(subjects[0]))
            throw new InvalidOperationException("Identity requires exactly one subject.");
        return "web:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            System.Text.Json.JsonSerializer.Serialize(new[] { issuer.TrimEnd('/'), subjects[0] })))).ToLowerInvariant();
    }
    public static bool Configured(IConfiguration config) => !string.IsNullOrWhiteSpace(config["WebAuthentication:Authority"]) &&
        !string.IsNullOrWhiteSpace(config["WebAuthentication:ClientId"]);
    public static Uri ApiAddress(IConfiguration configuration)
    {
        var configured = configuration["OrchestrationApi:BaseUrl"] ?? "http://127.0.0.1:5180/";
        if (!Uri.TryCreate(configured, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("https" or "http") || (uri.Scheme == "http" && !uri.IsLoopback) ||
            !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("Use an HTTPS API base URL, or loopback HTTP for local use, without credentials/query/fragment.");
        return new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
    }
    public static void AddWebIdentity(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddMemoryCache();
        services.AddSingleton<ITicketStore, WebTicketStore>();
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
        {
            o.Cookie.Name = "Neo.Orchestration.Session"; o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.SecurePolicy = environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            o.LoginPath = "/Login"; o.AccessDeniedPath = "/Denied";
            o.ExpireTimeSpan = TimeSpan.FromMinutes(30); o.SlidingExpiration = false;
        });
        services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
            .Configure<ITicketStore>((options, tickets) => options.SessionStore = tickets);
        // Resolve final host configuration lazily; startup removes this scheme
        // if unconfigured, before any remote handler can initialize.
        services.AddAuthentication().AddOpenIdConnect();
        services.AddOptions<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme)
            .Configure<IConfiguration>((o, config) =>
            {
                o.Authority = config["WebAuthentication:Authority"];
                o.ClientId = config["WebAuthentication:ClientId"];
                o.ClientSecret = config["WebAuthentication:ClientSecret"];
                o.ResponseType = "code"; o.UsePkce = true; o.RequireHttpsMetadata = true;
                o.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                o.MapInboundClaims = false; o.SaveTokens = true; o.GetClaimsFromUserInfoEndpoint = false;
                o.Scope.Clear(); o.Scope.Add("openid"); o.Scope.Add("profile");
                foreach (var scope in config.GetSection("WebAuthentication:Scopes").Get<string[]>() ?? []) o.Scope.Add(scope);
                o.Events.OnTicketReceived = context =>
                {
                    context.Properties ??= new AuthenticationProperties();
                    context.Properties.Items[ChatKey] = ChatId(o.Authority!, context.Principal!);
                    context.Properties.ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(30);
                    return Task.CompletedTask;
                };
                o.Events.OnRemoteFailure = context => { context.HandleResponse(); context.Response.Redirect("/Login?failed=true"); return Task.CompletedTask; };
            });
    }
}

// Only a protected opaque session key reaches the browser. Access/ID tokens
// stay server-side. This initial deployment is single-instance; restart signs
// users out. A shared ticket store is needed before scaling replicas.
public sealed class WebTicketStore(IMemoryCache cache) : ITicketStore
{
    public Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var key = "neo-ticket:" + Guid.NewGuid().ToString("N");
        cache.Set(key, ticket, ticket.Properties.ExpiresUtc ?? DateTimeOffset.UtcNow.AddMinutes(30));
        return Task.FromResult(key);
    }
    public Task RenewAsync(string key, AuthenticationTicket ticket)
    { cache.Set(key, ticket, ticket.Properties.ExpiresUtc ?? DateTimeOffset.UtcNow.AddMinutes(30)); return Task.CompletedTask; }
    public Task<AuthenticationTicket?> RetrieveAsync(string key) => Task.FromResult(cache.Get<AuthenticationTicket>(key));
    public Task RemoveAsync(string key) { cache.Remove(key); return Task.CompletedTask; }
}
