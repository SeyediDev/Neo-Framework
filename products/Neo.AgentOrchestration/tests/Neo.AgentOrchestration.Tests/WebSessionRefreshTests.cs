using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using Neo.AgentOrchestration.Web;
using Xunit;

namespace Neo.AgentOrchestration.Tests;

public sealed class WebSessionRefreshTests
{
    private const string Issuer = "https://identity.example.test/realms/fanasa";
    private static readonly SymmetricSecurityKey Key = new(new byte[32].Select((_, i) => (byte)(i + 1)).ToArray());
    private static string Jwt(string subject = "owner", string audience = "api", string issuer = Issuer,
        SecurityKey? key = null, bool expired = false) => new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer, Audience = audience,
            Subject = new ClaimsIdentity([new Claim("sub", subject)]),
            IssuedAt = DateTime.UtcNow.AddMinutes(-2), NotBefore = DateTime.UtcNow.AddMinutes(-2),
            Expires = DateTime.UtcNow.AddMinutes(expired ? -1 : 5),
            SigningCredentials = new SigningCredentials(key ?? Key, SecurityAlgorithms.HmacSha256)
        });

    private static AuthenticationTicket Ticket(bool fresh = false)
    {
        var properties = new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(20) };
        properties.Items[WebIdentity.ChatKey] = "stable-chat";
        properties.StoreTokens([
            new() { Name = "access_token", Value = "old-access" },
            new() { Name = "refresh_token", Value = "old-refresh" },
            new() { Name = "expires_at", Value = DateTimeOffset.UtcNow.AddMinutes(fresh ? 5 : -1).ToString("o") }
        ]);
        return new(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "owner")], "oidc")), properties, "Cookies");
    }

    private static HttpResponseMessage Response(string? token = null, string? id = null) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["access_token"] = token ?? Jwt(), ["refresh_token"] = "rotated-refresh",
            ["token_type"] = "Bearer", ["expires_in"] = 300,
        }.Concat(id is null ? [] : new[] { KeyValuePair.Create<string, object?>("id_token", id) }).ToDictionary(x => x.Key, x => x.Value)))
    };

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> reply) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        { Interlocked.Increment(ref Calls); return reply(request); }
    }
    private sealed class Factory(Handler handler) : IHttpClientFactory
    { public HttpClient CreateClient(string name) => new(handler, false); }
    private static WebTokenRefresher Refresher(Handler handler, string endpoint = Issuer + "/token", string? audience = "api")
    {
        var services = new ServiceCollection();
        services.AddOptions<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme).Configure(o =>
        {
            o.Authority = Issuer; o.ClientId = "web";
            var metadata = new OpenIdConnectConfiguration { Issuer = Issuer, TokenEndpoint = endpoint };
            metadata.SigningKeys.Add(Key);
            o.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata);
        });
        var monitor = services.BuildServiceProvider().GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>();
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["WebAuthentication:ApiAudience"] = audience }).Build();
        return new(new Factory(handler), monitor, config, TimeProvider.System);
    }

    [Fact]
    public async Task Concurrent_requests_refresh_once_rotate_tokens_and_keep_absolute_deadline_and_actor()
    {
        var handler = new Handler(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(Issuer + "/token", request.RequestUri!.ToString());
            var form = await request.Content!.ReadAsStringAsync();
            Assert.Contains("grant_type=refresh_token", form); Assert.Contains("refresh_token=old-refresh", form);
            await Task.Delay(20); return Response();
        });
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var store = new WebTicketStore(cache, Refresher(handler));
        var original = Ticket(); var key = await store.StoreAsync(original);
        var results = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => store.RetrieveAsync(key)));
        Assert.Equal(1, handler.Calls);
        Assert.All(results, ticket =>
        {
            Assert.NotNull(ticket); Assert.Equal(original.Properties.ExpiresUtc, ticket.Properties.ExpiresUtc);
            Assert.Equal("rotated-refresh", ticket.Properties.GetTokenValue("refresh_token"));
            Assert.Equal("stable-chat", ticket.Properties.Items[WebIdentity.ChatKey]);
        });
        await store.RenewAsync(key, original); // late stale renewal cannot roll back rotation
        Assert.Equal("rotated-refresh", (await store.RetrieveAsync(key))!.Properties.GetTokenValue("refresh_token"));
        await store.RemoveAsync(key); await store.RenewAsync(key, original);
        Assert.Null(await store.RetrieveAsync(key));
    }

    [Fact]
    public async Task Fresh_access_token_does_not_call_provider_and_returned_tickets_are_isolated()
    {
        var handler = new Handler(_ => throw new InvalidOperationException());
        using var cache = new MemoryCache(new MemoryCacheOptions()); var store = new WebTicketStore(cache, Refresher(handler));
        var key = await store.StoreAsync(Ticket(true)); var first = await store.RetrieveAsync(key);
        first!.Properties.UpdateTokenValue("access_token", "request-local-change");
        Assert.Equal("old-access", (await store.RetrieveAsync(key))!.Properties.GetTokenValue("access_token"));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("subject")]
    [InlineData("audience")]
    [InlineData("issuer")]
    [InlineData("signature")]
    [InlineData("expired")]
    [InlineData("id-subject")]
    [InlineData("revoked")]
    [InlineData("network")]
    [InlineData("malformed")]
    public async Task Invalid_or_revoked_refresh_fails_closed_without_retry(string failure)
    {
        var handler = new Handler(_ => Task.FromResult(failure switch
        {
            "subject" => Response(Jwt(subject: "other")),
            "audience" => Response(Jwt(audience: "other")),
            "issuer" => Response(Jwt(issuer: "https://evil.example.test")),
            "signature" => Response(Jwt(key: new SymmetricSecurityKey(new byte[32]))),
            "expired" => Response(Jwt(expired: true)),
            "id-subject" => Response(id: Jwt(subject: "other", audience: "web")),
            "revoked" => new HttpResponseMessage(HttpStatusCode.BadRequest),
            "network" => throw new HttpRequestException("unavailable"),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("not json") }
        }));
        using var cache = new MemoryCache(new MemoryCacheOptions()); var store = new WebTicketStore(cache, Refresher(handler));
        var key = await store.StoreAsync(Ticket());
        Assert.Null(await store.RetrieveAsync(key)); Assert.Null(await store.RetrieveAsync(key));
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("https://evil.example.test/token", "api")]
    [InlineData("http://identity.example.test/token", "api")]
    [InlineData(Issuer + "/token", null)]
    public async Task Unsafe_endpoint_or_missing_audience_never_receives_refresh_token(string endpoint, string? audience)
    {
        var handler = new Handler(_ => throw new InvalidOperationException());
        Assert.False(await Refresher(handler, endpoint, audience).RefreshIfNeededAsync(Ticket()));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Logout_during_refresh_does_not_resurrect_session()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new Handler(async _ => { entered.SetResult(); await release.Task; return Response(); });
        using var cache = new MemoryCache(new MemoryCacheOptions()); var store = new WebTicketStore(cache, Refresher(handler));
        var key = await store.StoreAsync(Ticket()); var retrieval = store.RetrieveAsync(key);
        await entered.Task; await store.RemoveAsync(key); release.SetResult();
        Assert.Null(await retrieval); Assert.Null(await store.RetrieveAsync(key));
    }

    [Theory]
    [InlineData("deadline")]
    [InlineData("refresh")]
    [InlineData("expiry")]
    public async Task Expired_session_or_missing_refresh_metadata_cannot_be_extended(string missing)
    {
        var handler = new Handler(_ => throw new InvalidOperationException());
        var ticket = Ticket();
        if (missing == "deadline") ticket.Properties.ExpiresUtc = DateTimeOffset.UtcNow.AddSeconds(-1);
        if (missing == "refresh") ticket.Properties.UpdateTokenValue("refresh_token", "");
        if (missing == "expiry") ticket.Properties.UpdateTokenValue("expires_at", "invalid");
        Assert.False(await Refresher(handler).RefreshIfNeededAsync(ticket));
        Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_cookie_pipeline_refreshes_server_ticket_or_challenges_revoked_session(bool revoked)
    {
        var handler = new Handler(_ => Task.FromResult(revoked
            ? new HttpResponseMessage(HttpStatusCode.BadRequest) : Response()));
        await using var web = new WebApplicationFactory<WebHost>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WebAuthentication:Authority"] = Issuer, ["WebAuthentication:ClientId"] = "web",
                ["WebAuthentication:ApiAudience"] = "api"
            }));
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IHttpClientFactory>(new Factory(handler));
                services.AddSingleton<IStartupFilter>(new TestSignIn());
                services.PostConfigure<OpenIdConnectOptions>(OpenIdConnectDefaults.AuthenticationScheme, o =>
                {
                    var metadata = new OpenIdConnectConfiguration { Issuer = Issuer, TokenEndpoint = Issuer + "/token" };
                    metadata.SigningKeys.Add(Key);
                    o.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata);
                });
            });
        });
        using var client = web.CreateClient(new() { AllowAutoRedirect = false, BaseAddress = new("https://localhost") });
        var signIn = await client.GetAsync("/test-only-signin", TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, signIn.StatusCode);
        var cookie = string.Join(";", signIn.Headers.GetValues("Set-Cookie"));
        Assert.DoesNotContain("old-access", cookie); Assert.DoesNotContain("old-refresh", cookie);
        var page = await client.GetAsync("/SessionRestored", TestContext.Current.CancellationToken);
        Assert.Equal(revoked ? HttpStatusCode.Redirect : HttpStatusCode.OK, page.StatusCode);
        if (revoked) Assert.Contains("/Login", page.Headers.Location!.OriginalString);
        else
        {
            var html = await page.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            Assert.Contains("session-restored.js", html);
            Assert.DoesNotContain("rotated-refresh", html); Assert.DoesNotContain("old-access", html);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/SessionRestored", TestContext.Current.CancellationToken)).StatusCode);
        }
        Assert.Equal(1, handler.Calls);
    }

    // Fixture only: never compiled into the deployed Web product.
    private sealed class TestSignIn : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                if (context.Request.Path == "/test-only-signin")
                {
                    var ticket = Ticket();
                    await context.SignInAsync("Cookies", ticket.Principal, ticket.Properties);
                    context.Response.StatusCode = 200;
                }
                else await continuation();
            });
            next(app);
        };
    }
}
