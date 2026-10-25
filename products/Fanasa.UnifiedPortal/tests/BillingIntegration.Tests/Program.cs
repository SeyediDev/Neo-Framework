using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Fanasa.UnifiedPortal.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

await OidcScopeChecks.RunAsync();
if (args.Contains("--oidc-scopes")) return;

var now = DateTimeOffset.Parse("2026-10-24T10:00:00Z", CultureInfo.InvariantCulture);
var clock = new Clock(now); var checks = 0;
const string subject = "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee", session = "11111111-2222-4333-8444-555555555555";
var consumer = Guid.Parse("66666666-7777-4888-8999-aaaaaaaaaaaa");
void Check(bool valid, string name) { if (!valid) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
{
    ["ConsumerBilling:Enabled"] = "true", ["ConsumerBilling:GatewayOrigin"] = "https://api.fanasa.net.local/",
    ["ConsumerBilling:Audience"] = "fanasa-api", ["ConsumerBilling:Scope"] = "billing.read",
    ["Authentication:ClientId"] = "unified-portal", ["Authentication:Authority"] = "https://identity.example.invalid/realms/fanasa",
    ["PlatformControlCenter:BaseUrl"] = "https://pcc.example.invalid/"
}).Build();
var options = ConsumerBillingOptions.Read(configuration);
Check(options.IsConfigured, "explicit financial options use exact Gateway origin");
Check(!ConsumerBillingOptions.Read(new ConfigurationBuilder().Build()).IsConfigured, "feature defaults disabled");
foreach (var origin in new[] { "https://pcc.example.invalid/", "http://gateway.example.invalid/", "https://user:pass@gateway.example.invalid/", "https://gateway.example.invalid/path", "https://gateway.example.invalid/?q=1", "https://gateway.example.invalid:8443/" })
{
    configuration["ConsumerBilling:GatewayOrigin"] = origin;
    Check(!ConsumerBillingOptions.Read(configuration).IsConfigured, "direct PCC and invalid Gateway origin are unconfigured");
}
configuration["ConsumerBilling:GatewayOrigin"] = "https://api.fanasa.net.local/";
using var rsa = RSA.Create(2048); var key = new RsaSecurityKey(rsa) { KeyId = "offline-test-key" };
var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", subject), new Claim("sid", session)], "OpenIdConnect"));
string Token(string? audience = null, string? tokenSubject = null, string? tokenSession = null, string? scope = null, string? client = null, DateTimeOffset? expires = null, string? grantType = null)
{
    var claims = new List<Claim>
    {
        new("sub", tokenSubject ?? subject), new("sid", tokenSession ?? session), new("azp", client ?? options.ClientId),
        new("scope", scope ?? options.Scope), new("auth_time", now.AddMinutes(-1).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64)
    };
    if (grantType is not null) claims.Add(new("grant_type", grantType));
    return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(options.Authority, audience ?? options.Audience, claims,
        now.AddMinutes(-1).UtcDateTime, (expires ?? now.AddMinutes(2)).UtcDateTime, new SigningCredentials(key, SecurityAlgorithms.RsaSha256)));
}
var token = Token();
Check(ConsumerBillingOidc.ValidateToken(token, subject, session, options, [key], clock) == now.AddMinutes(2), "signed human token validates issuer audience client session scope and lifetime");
foreach (var bad in new[] { Token(audience: "catalog"), Token(tokenSubject: Guid.NewGuid().ToString()), Token(tokenSession: Guid.NewGuid().ToString()), Token(scope: "platform.catalog"), Token(client: "service-client"), Token(expires: now), Token(grantType: "client_credentials"), token[..^4] + "xxxx", "opaque-token" })
    Check(ConsumerBillingOidc.ValidateToken(bad, subject, session, options, [key], clock) is null, "foreign forged service expired or unapproved token rejected");
var vault = new ConsumerBillingSessionVault(clock);
var services = new ServiceCollection().AddSingleton(options).AddSingleton<TimeProvider>(clock).AddSingleton(vault).BuildServiceProvider();
var metadata = new OpenIdConnectConfiguration { Issuer = options.Authority }; metadata.SigningKeys.Add(key);
var oidc = new OpenIdConnectOptions { ClientId = options.ClientId, Authority = options.Authority, SaveTokens = false,
    ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata) };
var properties = new AuthenticationProperties();
var http = new DefaultHttpContext { RequestServices = services, User = principal };
var validated = new TokenValidatedContext(http, new AuthenticationScheme("OpenIdConnect", null, typeof(OpenIdConnectHandler)), oidc, principal, properties)
    { TokenEndpointResponse = new OpenIdConnectMessage { AccessToken = token } };
await ConsumerBillingOidc.CaptureAsync(validated);
Check(properties.Items.TryGetValue(ConsumerBillingSessionVault.TicketHandle, out var handle) && handle?.Length == 64, "synthetic validated OIDC callback captures random handle");
Check(!JsonSerializer.Serialize(properties).Contains(token) && !properties.GetTokens().Any(), "protected ticket contains no token");
Check(vault.Read(handle, subject, session)?.BearerToken == token && vault.Read(handle, subject, Guid.NewGuid().ToString()) is null
    && vault.Read(handle, Guid.NewGuid().ToString(), session) is null, "vault binds subject and OIDC session");
Check(!oidc.SaveTokens, "SaveTokens stays false");
var ticket = AuthenticateResult.Success(new AuthenticationTicket(principal, properties, CookieAuthenticationDefaults.AuthenticationScheme));
var auth = new OfflineAuthentication { Result = ticket };
http.RequestServices = new ServiceCollection().AddSingleton<IAuthenticationService>(auth).AddSingleton(vault).BuildServiceProvider();
var accessor = new HttpContextAccessor { HttpContext = http };
var provider = new SessionConsumerUsageCredentialProvider(accessor, options, vault);
Check((await provider.GetAsync(default))?.BearerToken == token, "provider reads only authenticated protected-cookie handle");
var periodFrom = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
var usageEnvelope = JsonSerializer.Serialize(new { subject, report = new
{
    consumerTenantId = consumer, from = periodFrom, to = now, asOf = now, status = "ready", basis = "posted-ledger",
    currency = "USD", totalAmount = "9007199254740993.10", items = new[] { new
    {
        productKey = "synthetic-service", meterId = "requests", meterVersion = "1", unit = "request", quantity = "1",
        amount = "9007199254740993.10", chargeCount = 1
    } }
} });
var offline = new OfflineGateway(usageEnvelope);
using (var reader = new ConsumerUsageReportClient(options.GatewayOrigin!, provider, offline, clock: clock))
{
    var result = await reader.ReadAsync(consumer, periodFrom, now);
    Check(result.Status == ConsumerUsageReadStatus.Success && result.View.TotalAmount == "9007199254740993.10",
        "synthetic signed OIDC capture through session provider and Gateway HTTP handler preserves usage projection");
    Check(offline.Target?.GetLeftPart(UriPartial.Authority) == options.GatewayOrigin!.GetLeftPart(UriPartial.Authority)
        && offline.Bearer == token && offline.Target.AbsolutePath == "/api/accounting/v1/consumers/" + consumer + "/usage-report"
        && !offline.Target.Query.Contains(subject, StringComparison.Ordinal), "offline Gateway request uses only bound human bearer and no subject substitution");
    Check(!JsonSerializer.Serialize(result).Contains(token) && !JsonSerializer.Serialize(result).Contains(subject), "projected usage result is safe for bootstrap JSON");
}
http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", subject), new Claim("sid", Guid.NewGuid().ToString())], "Cookies"));
Check(await provider.GetAsync(default) is null, "provider rejects current session that differs from protected ticket");
http.User = principal;
auth.Result = AuthenticateResult.NoResult();
Check(await provider.GetAsync(default) is null, "provider rejects missing cookie authentication");
auth.Result = ticket;
var disabledProvider = new SessionConsumerUsageCredentialProvider(accessor, options with { Enabled = false }, vault);
Check(await disabledProvider.GetAsync(default) is null, "disabled feature never exposes an existing memory credential");
await ConsumerBillingOidc.PurgeAsync(new CookieSigningOutContext(http,
    new AuthenticationScheme(CookieAuthenticationDefaults.AuthenticationScheme, null, typeof(CookieAuthenticationHandler)),
    new CookieAuthenticationOptions(), new AuthenticationProperties(), new CookieOptions()));
Check(vault.Read(handle, subject, session) is null && !properties.Items.ContainsKey(ConsumerBillingSessionVault.TicketHandle), "logout purge invalidates credential");
var expiring = vault.Capture(subject, session, token, now.AddHours(1)); clock.Now = now.AddMinutes(5);
Check(vault.Read(expiring, subject, session) is null, "vault lifetime capped at five minutes"); clock.Now = now;

JsonObject Envelope() => new()
{
    ["subject"] = subject, ["account"] = new JsonObject
    {
        ["schemaVersion"] = 1, ["consumerTenantId"] = consumer.ToString(), ["asOf"] = now.ToString("O"), ["status"] = "ready",
        ["mode"] = "postpaid", ["currency"] = "USD", ["walletBalance"] = "0", ["outstanding"] = "9007199254740993.10", ["availableCredit"] = "0.90",
        ["invoices"] = new JsonArray(new JsonObject
        {
            ["id"] = Guid.NewGuid().ToString(), ["period"] = "2026-09", ["amount"] = "9007199254740994.10",
            ["creditedAmount"] = "0.50", ["paidAmount"] = "0.50", ["remainingAmount"] = "9007199254740993.10", ["status"] = "partiallyPaid",
            ["issuedAt"] = "2026-10-01T00:00:00Z", ["from"] = "2026-09-01T00:00:00Z", ["to"] = "2026-10-01T00:00:00Z"
        })
    }
};
ConsumerAccountView Read(JsonObject json) => ConsumerAccountProjection.Read(Encoding.UTF8.GetBytes(json.ToJsonString()), subject, consumer);
var view = Read(Envelope());
Check(view.Status == "ready" && view.Outstanding == "9007199254740993.10" && view.Invoices[0].Amount == "9007199254740994.10", "account and invoice retain exact large strings");
Check(!JsonSerializer.Serialize(view).Contains(subject) && !JsonSerializer.Serialize(view).Contains(token), "browser DTO excludes subject and credentials");
foreach (var mutation in new Action<JsonObject>[]
{
    x => x["subject"] = Guid.NewGuid().ToString(), x => x["account"]!["consumerTenantId"] = Guid.NewGuid().ToString(),
    x => x["account"]!["schemaVersion"] = 2, x => x["account"]!["outstanding"] = 12.5,
    x => x["account"]!["outstanding"] = "-1", x => x["account"]!["availableCredit"] = "1.001", x => x["account"]!["currency"] = "CHF",
    x => x["account"]!["mode"] = "prepaid", x => x["account"]!["status"] = "cached", x => x["account"]!["asOf"] = "2026-10-24T10:00:00+03:30",
    x => x["account"]!["invoices"]![0]!["status"] = "paid",
    x => x["account"]!["invoices"]![0]!["remainingAmount"] = "1", x => x["account"]!["invoices"]![0]!["from"] = null,
    x => x["account"]!["invoices"]![0]!["period"] = "2026-08", x => x["account"]!["invoices"]![0]!["status"] = "unknown"
})
{
    var data = Envelope(); mutation(data); var invalid = Read(data);
    Check(invalid.Status == "unavailable" && invalid.Outstanding is null && invalid.Invoices.Count == 0, "foreign malformed or inconsistent account clears all money");
}
var absent = Envelope(); var a = absent["account"]!.AsObject(); a["status"] = "notRecorded";
foreach (var name in new[] { "mode", "currency", "walletBalance", "outstanding", "availableCredit" }) a[name] = null;
a["invoices"] = new JsonArray();
Check(Read(absent).Status == "notRecorded" && Read(absent).Outstanding is null, "missing account never means zero");
var zero = Envelope(); var z = zero["account"]!.AsObject(); z["walletBalance"] = "0"; z["outstanding"] = "0"; z["availableCredit"] = "0"; z["invoices"] = new JsonArray();
Check(Read(zero).Status == "ready" && Read(zero).Outstanding == "0", "recorded zero is ready");
var payg = Envelope(); payg["account"]!["mode"] = "payg"; payg["account"]!["invoices"]![0]!["status"] = "statement";
Check(Read(payg).Status == "ready" && Read(payg).Invoices[0].Status == "statement", "PAYG uses statement semantics without a second payment obligation");
var duplicate = Envelope().ToJsonString().Replace("\"schemaVersion\":1", "\"schemaVersion\":1,\"schemaVersion\":1", StringComparison.Ordinal);
Check(ConsumerAccountProjection.Read(Encoding.UTF8.GetBytes(duplicate), subject, consumer).Status == "unavailable", "duplicate JSON properties fail closed");
var target = new Uri(options.GatewayOrigin!, "api/accounting/v1/consumers/" + consumer.ToString("D") + "/account");
HttpResponseMessage Response() => new(HttpStatusCode.OK)
{
    Content = new StringContent(Envelope().ToJsonString(), Encoding.UTF8, "application/json"),
    Headers = { CacheControl = new CacheControlHeaderValue { NoStore = true } },
    RequestMessage = new HttpRequestMessage(HttpMethod.Get, target)
};
using (var response = Response()) Check((await ConsumerAccountHttpResponse.ReadAsync(response, target, subject, consumer)).View.Status == "ready", "offline HTTP response projects account behind exact target and no-store boundary");
foreach (var mutate in new Action<HttpResponseMessage>[]
{
    x => x.StatusCode = HttpStatusCode.Found, x => x.StatusCode = HttpStatusCode.Forbidden,
    x => x.Headers.CacheControl = null, x => x.Content.Headers.ContentType = new("text/html"),
    x => x.Content.Headers.ContentEncoding.Add("gzip"), x => x.Content.Headers.ContentLength = ConsumerAccountProjection.MaximumBytes + 1,
    x => x.RequestMessage = new(HttpMethod.Get, "https://foreign.example.invalid/")
})
{
    using var response = Response(); mutate(response); var result = await ConsumerAccountHttpResponse.ReadAsync(response, target, subject, consumer);
    Check(result.Status != ConsumerUsageReadStatus.Success && result.View.Outstanding is null && result.View.Invoices.Count == 0, "redirect forbidden cacheable encoded oversized or foreign HTTP response clears account");
}
properties.Items[ConsumerBillingSessionVault.TicketHandle] = vault.Capture(subject, session, token, now.AddMinutes(2));
var gatewayRequests = new List<(Uri Target, string? Bearer)>();
var gatewayReader = new GatewayWorkspaceConsumerBillingReader(options, provider, clock,
    () => new RoutingGateway(usageEnvelope, Envelope().ToJsonString(), gatewayRequests));
var summary = await gatewayReader.ReadAsync(subject, consumer, default);
Check(summary.Status == "ready" && summary.Usage?.TotalAmount == "9007199254740993.10"
    && summary.Account?.Outstanding == "9007199254740993.10", "full server billing reader uses signed synthetic OIDC session and two actual financial projections");
Check(gatewayRequests.Count == 2 && gatewayRequests.All(x => x.Target.Host == "api.fanasa.net.local" && x.Bearer == token)
    && gatewayRequests[1].Target.AbsolutePath == "/api/accounting/v1/consumers/" + consumer + "/account"
    && gatewayRequests[1].Target.Query.Length == 0, "both read routes use approved Gateway and bearer header without subject or catalog credential");
Check(!JsonSerializer.Serialize(summary).Contains(token) && !JsonSerializer.Serialize(summary).Contains(subject), "financial reader bootstrap data excludes subject and token");
var beforeRequests = gatewayRequests.Count;
Check((await gatewayReader.ReadAsync(Guid.NewGuid().ToString(), consumer, default)).Status == "signInRequired"
    && gatewayRequests.Count == beforeRequests, "tampered caller subject stops before any HTTP handler");
var disabled = new GatewayWorkspaceConsumerBillingReader(options with { Enabled = false }, provider, clock, () => throw new Exception("disabled network"));
Check((await disabled.ReadAsync(subject, consumer, default)).Status == "notConfigured", "disabled feature returns explicit configuration state without network");
Check(!(options with { GatewayOrigin = new Uri("https://elsewhere.example.invalid/") }).IsConfigured,
    "even injected options cannot authorize an arbitrary Gateway host");
vault.Remove(properties);
Check((await gatewayReader.ReadAsync(subject, consumer, default)).Status == "signInRequired"
    && gatewayRequests.Count == beforeRequests, "logout removes billing capability before the next request");
Console.WriteLine($"PASS {checks} checks; synthetic signed OIDC and accounting fixtures; no network or live identity changes.");
sealed class Clock(DateTimeOffset now) : TimeProvider { public DateTimeOffset Now { get; set; } = now; public override DateTimeOffset GetUtcNow() => Now; }
sealed class OfflineAuthentication : IAuthenticationService
{
    public AuthenticateResult Result { get; set; } = AuthenticateResult.NoResult();
    public Task<AuthenticateResult> AuthenticateAsync(HttpContext context, string? scheme) => Task.FromResult(Result);
    public Task ChallengeAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
    public Task ForbidAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
    public Task SignInAsync(HttpContext context, string? scheme, ClaimsPrincipal principal, AuthenticationProperties? properties) => Task.CompletedTask;
    public Task SignOutAsync(HttpContext context, string? scheme, AuthenticationProperties? properties) => Task.CompletedTask;
}
sealed class OfflineGateway(string body) : HttpMessageHandler
{
    public Uri? Target { get; private set; }
    public string? Bearer { get; private set; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Target = request.RequestUri; Bearer = request.Headers.Authorization?.Parameter;
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request, Content = new StringContent(body, Encoding.UTF8, "application/json"),
            Headers = { CacheControl = new CacheControlHeaderValue { NoStore = true } }
        });
    }
}
sealed class RoutingGateway(string usage, string account, List<(Uri Target, string? Bearer)> requests) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        requests.Add((request.RequestUri!, request.Headers.Authorization?.Parameter));
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StringContent(request.RequestUri!.AbsolutePath.EndsWith("/account", StringComparison.Ordinal) ? account : usage, Encoding.UTF8, "application/json"),
            Headers = { CacheControl = new CacheControlHeaderValue { NoStore = true } }
        });
    }
}
