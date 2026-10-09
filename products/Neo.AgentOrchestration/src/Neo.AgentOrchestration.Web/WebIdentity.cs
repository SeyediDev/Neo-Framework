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
    public static TimeSpan SessionLifetime(IConfiguration config)
    {
        var raw = config["WebAuthentication:SessionLifetimeMinutes"];
        if (raw is null) return TimeSpan.FromMinutes(30);
        if (!int.TryParse(raw, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var minutes) || minutes is < 15 or > 1440)
            throw new InvalidOperationException("WebAuthentication:SessionLifetimeMinutes must be between 15 and 1440.");
        return TimeSpan.FromMinutes(minutes);
    }
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
        services.AddSingleton(TimeProvider.System);
        services.AddHttpClient("WebSessionRefresh", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
            client.MaxResponseContentBufferSize = 65536;
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
        services.AddSingleton<WebTokenRefresher>();
        services.AddSingleton<ITicketStore, WebTicketStore>();
        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
        {
            o.Cookie.Name = "Neo.Orchestration.Session"; o.Cookie.HttpOnly = true;
            o.Cookie.SameSite = SameSiteMode.Lax;
            o.Cookie.SecurePolicy = environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
            o.LoginPath = "/Login"; o.AccessDeniedPath = "/Denied";
            o.ExpireTimeSpan = TimeSpan.FromMinutes(30); o.SlidingExpiration = false;
            o.Events.OnRedirectToLogin = context =>
            {
                if (context.Request.Headers["X-Neo-Navigation"] == "1") context.Response.StatusCode = 401;
                else context.Response.Redirect(context.RedirectUri);
                return Task.CompletedTask;
            };
            o.Events.OnRedirectToAccessDenied = context =>
            {
                if (context.Request.Headers["X-Neo-Navigation"] == "1") context.Response.StatusCode = 403;
                else context.Response.Redirect(context.RedirectUri);
                return Task.CompletedTask;
            };
        });
        services.AddOptions<CookieAuthenticationOptions>(CookieAuthenticationDefaults.AuthenticationScheme)
            .Configure<ITicketStore>((options, tickets) => options.SessionStore = tickets)
            .Configure<IConfiguration>((options, config) => options.ExpireTimeSpan = SessionLifetime(config));
        // Resolve final host configuration lazily; startup removes this scheme
        // if unconfigured, before any remote handler can initialize.
        services.AddAuthentication().AddOpenIdConnect();
        services.AddOptions<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme)
            .Configure<IConfiguration>((o, config) =>
            {
                o.Authority = config["WebAuthentication:Authority"];
                o.ClientId = config["WebAuthentication:ClientId"];
                o.ClientSecret = config["WebAuthentication:ClientSecret"];
                o.ResponseType = "code"; o.UsePkce = true; o.RequireHttpsMetadata = !environment.IsDevelopment();
                o.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                o.MapInboundClaims = false; o.SaveTokens = true; o.GetClaimsFromUserInfoEndpoint = false;
                o.Scope.Clear(); o.Scope.Add("openid"); o.Scope.Add("profile");
                foreach (var scope in config.GetSection("WebAuthentication:Scopes").Get<string[]>() ?? []) o.Scope.Add(scope);
                o.Events.OnTicketReceived = context =>
                {
                    context.Properties ??= new AuthenticationProperties();
                    context.Properties.Items[ChatKey] = ChatId(o.Authority!, context.Principal!);
                    context.Properties.ExpiresUtc = DateTimeOffset.UtcNow.Add(SessionLifetime(config));
                    return Task.CompletedTask;
                };
                o.Events.OnRemoteFailure = context =>
                {
                    context.HandleResponse();
                    context.Response.Redirect("/Login?failed=true&returnUrl=" +
                        Uri.EscapeDataString(context.Properties?.RedirectUri ?? "/Workspace"));
                    return Task.CompletedTask;
                };
            });
    }
}

// Only a protected opaque session key reaches the browser. Access/ID tokens
// stay server-side. This initial deployment is single-instance; restart signs
// users out. A shared ticket store is needed before scaling replicas.
public sealed class WebTicketStore(IMemoryCache cache, WebTokenRefresher? refresher = null) : ITicketStore
{
    private sealed class Entry(AuthenticationTicket ticket)
    {
        public AuthenticationTicket Ticket = ticket;
        public readonly SemaphoreSlim Gate = new(1, 1);
    }
    private static AuthenticationTicket Copy(AuthenticationTicket ticket) =>
        TicketSerializer.Default.Deserialize(TicketSerializer.Default.Serialize(ticket))!;
    public Task<string> StoreAsync(AuthenticationTicket ticket)
    {
        var key = "neo-ticket:" + Guid.NewGuid().ToString("N");
        cache.Set(key, new Entry(Copy(ticket)), ticket.Properties.ExpiresUtc ?? DateTimeOffset.UtcNow.AddMinutes(30));
        return Task.FromResult(key);
    }
    public async Task RenewAsync(string key, AuthenticationTicket ticket)
    {
        if (cache.Get<Entry>(key) is not { } entry) return;
        await entry.Gate.WaitAsync();
        try
        {
            if (!ReferenceEquals(cache.Get<Entry>(key), entry)) return;
            var next = Copy(ticket);
            // A late cookie renewal must not overwrite rotated tokens or extend
            // the absolute deadline. Logout must never resurrect an entry.
            next.Properties.StoreTokens(entry.Ticket.Properties.GetTokens());
            next.Properties.ExpiresUtc = entry.Ticket.Properties.ExpiresUtc;
            entry.Ticket = next;
        }
        finally { entry.Gate.Release(); }
    }
    public async Task<AuthenticationTicket?> RetrieveAsync(string key)
    {
        if (cache.Get<Entry>(key) is not { } entry) return null;
        await entry.Gate.WaitAsync();
        try
        {
            if (!ReferenceEquals(cache.Get<Entry>(key), entry)) return null;
            if (refresher is not null && !await refresher.RefreshIfNeededAsync(entry.Ticket))
            { cache.Remove(key); return null; }
            return ReferenceEquals(cache.Get<Entry>(key), entry) ? Copy(entry.Ticket) : null;
        }
        finally { entry.Gate.Release(); }
    }
    public Task RemoveAsync(string key) { cache.Remove(key); return Task.CompletedTask; }
}
