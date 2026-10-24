using System.Text.Json;
using Fanasa.AccessManagement.Web.Persistence;
using Fanasa.HrReadPdp;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Fanasa.HrReadHost.Tests;

/// <summary>Runtime registration smoke checks only; no listener, token endpoint or live network is started.</summary>
public static class HostServicesTests
{
    public static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool condition, string label)
        { count++; if (!condition) throw new InvalidOperationException("HR3 host services test failed: " + label); }

        using (var fixture = await CentralHostRecordsTests.CreateFixtureAsync())
        {
            var services = Baseline(fixture); var descriptors = services.ToArray();
            var disabled = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { [Hr3HostServices.ConfigurationSection + ":Enabled"] = "false" }).Build();
            services.AddHr3HostPdp(disabled);
            Check(services.SequenceEqual(descriptors), "disabled runtime opt-in adds no service or authentication registration");
            using var provider = services.BuildServiceProvider();
            var schemes = provider.GetRequiredService<IAuthenticationSchemeProvider>();
            Check((await schemes.GetAllSchemesAsync()).Select(x => x.Name).Order(StringComparer.Ordinal)
                .SequenceEqual(BaselineSchemes.Order(StringComparer.Ordinal)), "disabled runtime opt-in adds no named scheme");
            Check(provider.GetService<IHr3AuthenticationPreparer>() is null
                && provider.GetService<IHr3HostPdpServiceFactory>() is null
                && provider.GetService<IHr3IntrospectionAuthority>() is null,
                "disabled runtime has no PDP service, preparer or native authority");
            var endpoints = new Routes(provider); endpoints.MapHr3HostPdp();
            Check(endpoints.DataSources.Count == 0, "disabled runtime maps no PDP route");
            Check((await schemes.GetDefaultAuthenticateSchemeAsync())?.Name == CookieAuthenticationDefaults.AuthenticationScheme
                && (await schemes.GetDefaultChallengeSchemeAsync())?.Name == OpenIdConnectDefaults.AuthenticationScheme,
                "disabled runtime preserves the existing human defaults");
        }

        using (var fixture = await CentralHostRecordsTests.CreateFixtureAsync())
        {
            var services = Baseline(fixture); var before = services.ToArray(); var threw = false;
            var invalid = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
                { [Hr3HostServices.ConfigurationSection + ":Enabled"] = "true" }).Build();
            try { services.AddHr3HostPdp(invalid); } catch (InvalidOperationException) { threw = true; }
            Check(threw && services.SequenceEqual(before), "enabled incomplete configuration fails before partial registration");
        }

        using (var fixture = await CentralHostRecordsTests.CreateFixtureAsync())
        {
            var services = Baseline(fixture);
            // This JSON contains only the existing synthetic transport intent. Credential
            // fields are deliberately absent, and no environment configuration is loaded.
            var payload = JsonSerializer.SerializeToUtf8Bytes(new { Fanasa = new { Hr3 = new { Pdp = new
            {
                Enabled = true, Gateway = fixture.Transport.CallerSettings, Receiver = fixture.Transport.Binding,
                Introspection = new Hr3IntrospectionSettings
                {
                    Enabled = true, Issuer = fixture.Transport.Binding.Issuer,
                    GatewayAudience = fixture.Transport.CallerSettings.Audience,
                    GatewayRequiredScope = fixture.Transport.CallerSettings.RequiredScope,
                    MaximumWitnessSeconds = 5
                }
            } } } });
            using var json = new MemoryStream(payload);
            var configuration = new ConfigurationBuilder().AddJsonStream(json).Build();
            var before = fixture.Fingerprint(); services.AddHr3HostPdp(configuration);
            using var provider = services.BuildServiceProvider();
            var schemes = provider.GetRequiredService<IAuthenticationSchemeProvider>();
            var registered = (await schemes.GetAllSchemesAsync()).ToArray();
            var added = registered.Select(x => x.Name).Except(BaselineSchemes, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            Check(registered.Length == BaselineSchemes.Length + 2 && added.SequenceEqual(new[]
                { HrReadPdpAuthentication.CallerScheme, HrReadPdpAuthentication.SubjectScheme }.Order(StringComparer.Ordinal)),
                "compatible enabled configuration adds exactly the two distinct named HR3 bearer schemes");
            Check((await schemes.GetSchemeAsync("OrganizationHrBearer"))?.HandlerType == typeof(JwtBearerHandler),
                "existing OrganizationHrBearer remains its original bearer handler");
            Check((await schemes.GetDefaultAuthenticateSchemeAsync())?.Name == CookieAuthenticationDefaults.AuthenticationScheme
                && (await schemes.GetDefaultChallengeSchemeAsync())?.Name == OpenIdConnectDefaults.AuthenticationScheme,
                "enabled HR3 registration preserves default human cookie and OIDC challenge schemes");
            Check(ReferenceEquals(provider.GetRequiredService<FabricDatabase>(), fixture.Transport.Database)
                && ReferenceEquals(provider.GetRequiredService<TimeProvider>(), fixture.Transport.Time),
                "runtime factory receives the preexisting Fabric database and clock instances");
            var authority = provider.GetRequiredService<IHr3IntrospectionAuthority>();
            Check(authority is Hr3CentralIntrospectionAuthority
                && authority.ReadCurrent(fixture.Transport.Request, fixture.Transport.Caller, CancellationToken.None)?.ConsumerTenantId == fixture.Tenant.Id,
                "DI resolves the concrete native authority and reads the existing central fixture records");
            var factory = provider.GetRequiredService<IHr3HostPdpServiceFactory>();
            var writer = new Writer();
            var sent = await factory.Create(fixture.ProofSource).ExecuteAsync(fixture.Transport.Request,
                fixture.Transport.Caller, writer, CancellationToken.None);
            Check(factory is ExistingHostHr3PdpServiceFactory && sent && writer.Writes == 1 && writer.Bytes is { Length: > 0 },
                "resolved actual host factory executes the real PDP over the same preexisting native/Fabric data");
            var credentials = provider.GetRequiredService<IHr3IntrospectionCredentials>();
            Check(await credentials.ReadAsync(CancellationToken.None) is null,
                "default runtime credentials stay unavailable when secret configuration is missing");
            var preparation = provider.GetRequiredService<IHr3AuthenticationPreparer>();
            Check(preparation is Hr3KeycloakIntrospectionPreparer
                && await preparation.PrepareAsync(fixture.Transport.Request, fixture.Transport.Caller, CancellationToken.None) is null,
                "resolved real preparer denies missing credentials before any introspection HTTP work");
            var endpoints = new Routes(provider); endpoints.MapHr3HostPdp();
            var mapped = endpoints.DataSources.SelectMany(x => x.Endpoints).OfType<RouteEndpoint>().ToArray();
            Check(mapped.Length == 1 && mapped[0].RoutePattern.RawText == HrReadPdpWire.EndpointPath
                && mapped[0].Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.SequenceEqual(new[] { "POST" }) == true,
                "enabled runtime maps exactly the distinct internal POST PDP route");
            Check(fixture.Fingerprint() == before,
                "registration, DI resolution, native reads and PDP execution create no database/registry/outbox writes");
        }
        return count;
    }

    private static readonly string[] BaselineSchemes = [CookieAuthenticationDefaults.AuthenticationScheme,
        OpenIdConnectDefaults.AuthenticationScheme, "OrganizationHrBearer"];

    private static ServiceCollection Baseline(CentralHostRecordsTests.Fixture fixture)
    {
        var services = new ServiceCollection(); services.AddLogging(); services.AddRouting();
        services.AddSingleton(fixture.Transport.Database); services.AddSingleton(fixture.Registry);
        services.AddSingleton(fixture.Access); services.AddSingleton(fixture.Legacy);
        services.AddSingleton(fixture.Transport.Module); services.AddSingleton<TimeProvider>(fixture.Transport.Time);
        services.AddAuthentication(options =>
        {
            options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
            options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
        }).AddCookie().AddOpenIdConnect(options =>
        {
            options.Authority = fixture.Transport.Binding.Issuer; options.ClientId = "isolated-human-client";
        }).AddJwtBearer("OrganizationHrBearer", options =>
        {
            options.Authority = fixture.Transport.Binding.Issuer; options.Audience = "existing-organization-api";
        });
        return services;
    }

    private sealed class Routes(IServiceProvider serviceProvider) : IEndpointRouteBuilder
    {
        public IServiceProvider ServiceProvider { get; } = serviceProvider;
        public ICollection<EndpointDataSource> DataSources { get; } = new List<EndpointDataSource>();
        public IApplicationBuilder CreateApplicationBuilder() => new ApplicationBuilder(ServiceProvider);
    }
    private sealed class Writer : IHrReadPdpResponseWriter
    {
        public int Writes { get; private set; }
        public byte[]? Bytes { get; private set; }
        public ValueTask SendAsync(byte[] bytes, DateTimeOffset validUntil, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Writes++; Bytes = bytes.ToArray(); return ValueTask.CompletedTask; }
    }
}
