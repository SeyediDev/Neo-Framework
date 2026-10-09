using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Neo.AgentOrchestration.Web;
using Xunit;

namespace Neo.AgentOrchestration.Tests;

public sealed class WebSessionLifetimeTests
{
    [Theory]
    [InlineData(null, 30)]
    [InlineData("15", 15)]
    [InlineData("720", 720)]
    [InlineData("1440", 1440)]
    public void Lifetime_is_bounded_and_keeps_default(string? value, int minutes)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["WebAuthentication:SessionLifetimeMinutes"] = value }).Build();
        Assert.Equal(TimeSpan.FromMinutes(minutes), WebIdentity.SessionLifetime(config));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("14")]
    [InlineData("1441")]
    [InlineData("-1")]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData("NaN")]
    public void Invalid_configuration_is_not_silently_accepted(string value)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["WebAuthentication:SessionLifetimeMinutes"] = value }).Build();
        Assert.Throws<InvalidOperationException>(() => WebIdentity.SessionLifetime(config));
    }

    [Fact]
    public async Task Cookie_and_oidc_ticket_share_the_configured_absolute_deadline()
    {
        await using var web = new WebApplicationFactory<WebHost>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Testing");
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WebAuthentication:Authority"] = "https://identity.example.test/realms/fanasa",
                ["WebAuthentication:ClientId"] = "web",
                ["WebAuthentication:SessionLifetimeMinutes"] = "720"
            }));
        });
        var cookie = web.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>().Get("Cookies");
        Assert.Equal(TimeSpan.FromHours(12), cookie.ExpireTimeSpan);
        Assert.False(cookie.SlidingExpiration);
        Assert.NotNull(cookie.SessionStore);
        var oidc = web.Services.GetRequiredService<IOptionsMonitor<OpenIdConnectOptions>>().Get("OpenIdConnect");
        var context = new TicketReceivedContext(new DefaultHttpContext(),
            new AuthenticationScheme("OpenIdConnect", null, typeof(OpenIdConnectHandler)), oidc,
            new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "owner")], "oidc")),
                new AuthenticationProperties(), "Cookies"));
        // AuthenticationProperties serializes its expiry with whole-second precision.
        var before = DateTimeOffset.UtcNow.AddHours(12).AddSeconds(-1);
        await oidc.Events.TicketReceived(context);
        Assert.InRange(context.Properties!.ExpiresUtc!.Value, before, DateTimeOffset.UtcNow.AddHours(12));
        Assert.Equal(WebIdentity.ChatId(oidc.Authority!, context.Principal!), context.Properties.Items[WebIdentity.ChatKey]);
    }
}
