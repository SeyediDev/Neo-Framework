using Fanasa.UnifiedPortal.Web;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;

internal static class PortalSignInTests
{
    public static async Task<int> RunAsync()
    {
        var checks = 0;
        void Check(bool valid, string name)
        {
            if (!valid) throw new InvalidOperationException(name);
            Console.WriteLine("PASS sign-in return: " + name);
            checks++;
        }
        const string organization = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
        foreach (var section in new[] { "catalog", "requests", "services", "usage", "publish", "review" })
        {
            var target = "/Workspace?tenantId=" + organization + "#" + section;
            Check(PortalSignIn.SafeDestination(target) == target, "allowed workspace section " + section);
        }
        Check(PortalSignIn.SafeDestination("/index?tenantId=" + organization.ToUpperInvariant() + "#centers") == "/?tenantId=" + organization + "#centers",
            "home alias and organization normalize to canonical local route");
        Check(PortalSignIn.SafeDestination("/Workspace?handler=Snapshot&returnUrl=https://outside.example&tenantId=" + organization + "#usage") == "/Workspace?tenantId=" + organization + "#usage",
            "only organization is forwarded, never handlers or nested return URLs");
        foreach (var value in new[] { null, "", "https://outside.example/", "//outside.example/", "/\\outside.example/", "/%2foutside.example/",
            "%2FWorkspace", "/login?returnUrl=/Workspace", "/logout", "/Workspace/../logout", "/Workspace\r\nLocation: https://outside.example", new string('x', 2049) })
            Check(PortalSignIn.SafeDestination(value) == "/", "external, encoded, recursive or malformed destination rejected");
        foreach (var value in new[] { "", "bad", "00000000-0000-0000-0000-000000000000", organization + "&tenantId=" + organization })
            Check(PortalSignIn.SafeDestination("/Workspace?tenantId=" + value + "#services") == "/Workspace#services",
                "invalid or duplicate organization is not retained");
        Check(PortalSignIn.SafeDestination("/Workspace#https://outside.example") == "/Workspace", "unknown workspace fragment dropped");
        Check(PortalSignIn.SafeDestination("/#usage") == "/", "fragment must belong to the selected page");
        var loginQuery = QueryHelpers.ParseQuery(new Uri("https://portal.example.invalid" + PortalSignIn.LoginUrl("/Workspace?tenantId=" + organization + "#usage")).Query);
        Check(loginQuery["returnUrl"] == "/Workspace?tenantId=" + organization + "#usage", "rendered login URL retains one encoded local destination");

        var metadata = new OpenIdConnectConfiguration
        {
            Issuer = "https://identity.example.invalid/realms/fanasa",
            AuthorizationEndpoint = "https://identity.example.invalid/realms/fanasa/protocol/openid-connect/auth"
        };
        var network = new RejectNetwork();
        var services = new ServiceCollection().AddLogging();
        services.AddDataProtection().UseEphemeralDataProtectionProvider();
        services.AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme).AddCookie().AddOpenIdConnect(options =>
        {
            options.Authority = metadata.Issuer;
            options.ClientId = "synthetic-portal";
            options.SignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.ResponseType = "code";
            options.UsePkce = true;
            options.SaveTokens = false;
            options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata);
            options.Backchannel = new HttpClient(network);
        });
        await using var provider = services.BuildServiceProvider();
        foreach (var (query, expected) in new[]
        {
            ("?returnUrl=" + Uri.EscapeDataString("/Workspace?tenantId=" + organization + "#usage"), "/Workspace?tenantId=" + organization + "#usage"),
            ("?returnUrl=https%3A%2F%2Foutside.example%2F", "/"),
            ("?returnUrl=%2FWorkspace&returnUrl=%2Flogout", "/"),
            ("", "/")
        })
        {
            await using var requestScope = provider.CreateAsyncScope();
            var context = new DefaultHttpContext { RequestServices = requestScope.ServiceProvider };
            context.Request.Scheme = "https";
            context.Request.Host = new HostString("portal.example.invalid");
            context.Request.Path = "/login";
            context.Request.QueryString = new QueryString(query);
            await PortalSignIn.Challenge(context).ExecuteAsync(context);
            var target = new Uri(context.Response.Headers.Location.ToString());
            var fields = QueryHelpers.ParseQuery(target.Query);
            var options = provider.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get(OpenIdConnectDefaults.AuthenticationScheme);
            var properties = options.StateDataFormat.Unprotect(fields["state"].ToString());
            Check(context.Response.StatusCode == 302 && target.GetLeftPart(UriPartial.Path) == metadata.AuthorizationEndpoint,
                "actual challenge goes directly to the identity provider");
            Check(properties?.RedirectUri == expected, "protected OIDC state contains the sanitized return path");
            Check(fields["redirect_uri"] == "https://portal.example.invalid/signin-oidc" && fields["code_challenge_method"] == "S256"
                && fields["response_type"] == "code" && fields["code_challenge"].ToString().Length > 0,
                "callback and PKCE remain fixed independently of the return path");
        }
        Check(network.Calls == 0, "synthetic checks issue no network requests or tokens");
        return checks;
    }

    private sealed class RejectNetwork : HttpMessageHandler
    {
        internal int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("Sign-in return checks must not use the network.");
        }
    }
}
