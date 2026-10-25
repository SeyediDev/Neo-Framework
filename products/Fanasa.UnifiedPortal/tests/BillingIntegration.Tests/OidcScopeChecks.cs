using System.Net;
using Fanasa.UnifiedPortal.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

internal static class OidcScopeChecks
{
    private const string Scope = "synthetic.consumer.financial.read";

    public static async Task RunAsync()
    {
        await CheckAsync("enabled and complete", expectedFinancialScope: true);
        await CheckAsync("disabled", expectedFinancialScope: false, ("ConsumerBilling:Enabled", "false"));
        await CheckAsync("missing scope", expectedFinancialScope: false, ("ConsumerBilling:Scope", ""));
        await CheckAsync("multiple scopes", expectedFinancialScope: false, ("ConsumerBilling:Scope", Scope + " other"));
        await CheckAsync("unsafe Gateway", expectedFinancialScope: false, ("ConsumerBilling:GatewayOrigin", "https://foreign.example.invalid/"));
        await CheckAsync("direct PCC origin", expectedFinancialScope: false, ("PlatformControlCenter:BaseUrl", "https://api.fanasa.net.local/"));
        Console.WriteLine("PASS OIDC authorization scope: 6 focused cases, no network or issued token");
    }

    private static async Task CheckAsync(string name, bool expectedFinancialScope, params (string Key, string Value)[] changes)
    {
        var values = new Dictionary<string, string?>
        {
            ["ConsumerBilling:Enabled"] = "true",
            ["ConsumerBilling:GatewayOrigin"] = "https://api.fanasa.net.local/",
            ["ConsumerBilling:Audience"] = "synthetic-backend",
            ["ConsumerBilling:Scope"] = Scope,
            ["Authentication:ClientId"] = "synthetic-portal",
            ["Authentication:Authority"] = "https://identity.example.invalid/realms/fanasa"
        };
        foreach (var (key, value) in changes) values[key] = value;
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var billing = ConsumerBillingOptions.Read(configuration);
        var metadata = new OpenIdConnectConfiguration
        {
            Issuer = billing.Authority,
            AuthorizationEndpoint = billing.Authority + "/protocol/openid-connect/auth"
        };
        var backchannel = new RejectNetworkHandler();
        var services = new ServiceCollection().AddLogging();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
            .AddCookie()
            .AddOpenIdConnect(options =>
            {
                options.Authority = billing.Authority;
                options.ClientId = billing.ClientId;
                options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
                options.ResponseType = "code";
                options.UsePkce = true;
                options.SaveTokens = false;
                options.RequireHttpsMetadata = true;
                options.Backchannel = new HttpClient(backchannel);
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata);
                ConsumerBillingOidc.ConfigureScopes(options, billing);
            });
        await using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(OpenIdConnectDefaults.AuthenticationScheme);
        var context = new DefaultHttpContext { RequestServices = provider };
        context.Request.Scheme = "https";
        context.Request.Host = new HostString("portal.example.invalid");
        context.Request.Path = "/login";
        await context.ChallengeAsync(OpenIdConnectDefaults.AuthenticationScheme);

        var target = new Uri(context.Response.Headers.Location.ToString());
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(target.Query);
        var scopes = query["scope"].ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Require(context.Response.StatusCode == (int)HttpStatusCode.Redirect, name + ": challenge redirects");
        Require(scopes.Contains("openid") && scopes.Contains("profile"), name + ": standard scopes preserved");
        Require(scopes.Contains(Scope) == expectedFinancialScope && scopes.Length == (expectedFinancialScope ? 3 : 2), name + ": optional financial scope eligibility");
        Require(query["response_type"] == "code" && query["code_challenge_method"] == "S256" && query["code_challenge"].ToString().Length > 0,
            name + ": authorization code and PKCE preserved");
        Require(options.UsePkce && !options.SaveTokens && options.RequireHttpsMetadata, name + ": token and transport flags preserved");
        Require(backchannel.Calls == 0, name + ": static metadata causes no network");
        Console.WriteLine("PASS OIDC authorization scope: " + name);
    }

    private static void Require(bool valid, string name)
    {
        if (!valid) throw new InvalidOperationException(name);
    }

    private sealed class RejectNetworkHandler : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("OIDC scope checks must not use network");
        }
    }
}
