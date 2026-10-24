using System.Net;
using Fanasa.HrReadCandidate.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Fanasa.HrReadCandidate.Http.Tests;

/// <summary>Real loopback HTTP routing only; no operational TLS or identity acceptance.</summary>
public static class KestrelEndpointTests
{
    public static async Task<int> RunAsync()
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = [] });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        var settings = EnvelopeTests.Settings();
        if (!settings.Enabled || settings.Validate().Count != 0) throw new InvalidOperationException("Invalid routing fixture.");
        app.MapHrReadCandidate(settings);
        await app.StartAsync(deadline.Token);
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient(new SocketsHttpHandler { UseProxy = false })
            { BaseAddress = new Uri(address), Timeout = TimeSpan.FromSeconds(5) };
            var count = 0;
            foreach (var suffix in new[] { "/chart", "/history/9223372036854775807", "/chart?bypass=true" })
            {
                using var request = new HttpRequestMessage(HttpMethod.Get,
                    "/api/organization-hr/v1/tenants/" + settings.ConsumerTenantId + suffix);
                request.Headers.Add("X-Forwarded-Proto", "https");
                request.Headers.Add("X-Forwarded-Client-Cert", "fixture-header-is-not-a-native-peer");
                using var response = await client.SendAsync(request, deadline.Token);
                Require(response.StatusCode == HttpStatusCode.Forbidden, "Untrusted loopback request escaped the receiver."); count++;
                Require(response.Headers.CacheControl?.NoStore == true, "HTTP denial was cacheable."); count++;
                Require((await response.Content.ReadAsByteArrayAsync(deadline.Token)).Length == 0, "HTTP denial leaked a payload."); count++;
            }
            return count;
        }
        finally { await app.StopAsync(CancellationToken.None); }
    }
    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
