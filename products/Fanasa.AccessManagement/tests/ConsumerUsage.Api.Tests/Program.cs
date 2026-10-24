using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fanasa.AccessManagement.Web.Accounting;
using Fanasa.AccessManagement.Web.Api;
using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Domain.Access;
using Fanasa.AccessManagement.Web.Metering;
using Fanasa.AccessManagement.Web.Persistence;
using Fanasa.AccessManagement.Web.Platform;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

// All identities, grants, clients and accounts below are isolated synthetic data.
// Real Kestrel, JWT validation, the production controller and Fabric SQLite are used.
var checks = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new Exception("FAILED: " + label);
    checks++;
    Console.WriteLine("PASS consumer usage API: " + label);
}

await using (var f = await Fixture.Start())
{
    async Task Denied(
        string label,
        HttpStatusCode status,
        string? token = null,
        Guid? consumer = null,
        string? query = null,
        bool forgedSubject = false)
    {
        var before = f.Snapshot();
        using var response = await f.Get(token, consumer, query, forgedSubject);
        Check(response.StatusCode == status,
            label + " HTTP status (actual " + (int)response.StatusCode + ")");
        Check(f.Snapshot() == before,
            label + " leaves every Fabric document and outbox row unchanged");
        if (status != HttpStatusCode.Unauthorized)
            Check(response.Headers.CacheControl?.NoStore == true,
                label + " response is no-store");
    }

    await Denied("anonymous", HttpStatusCode.Unauthorized);
    await Denied("invalid signature", HttpStatusCode.Unauthorized,
        f.Token(badSignature: true));
    await Denied("wrong issuer", HttpStatusCode.Unauthorized,
        f.Token(issuer: "https://forged.invalid/realms/fanasa"));
    await Denied("wrong audience", HttpStatusCode.Unauthorized,
        f.Token(audience: "other-api"));
    await Denied("expired bearer", HttpStatusCode.Unauthorized,
        f.Token(expired: true));

    using (var login = await f.Client.GetAsync("/synthetic-login"))
        Check(login.IsSuccessStatusCode, "synthetic administrator cookie established");
    await Denied("administrator cookie without bearer", HttpStatusCode.Unauthorized);

    await Denied("service token without human session", HttpStatusCode.Forbidden,
        f.Token(humanSession: false));
    await Denied("explicit client credentials", HttpStatusCode.Forbidden,
        f.Token(extra: [new Claim("grant_type", "client_credentials")]));
    await Denied("wrong Portal client", HttpStatusCode.Forbidden,
        f.Token(client: "other-client"));
    await Denied("catalog-only scope", HttpStatusCode.Forbidden,
        f.Token(scope: "platform.catalog"));
    await Denied("conflicting client_id", HttpStatusCode.Forbidden,
        f.Token(extra: [new Claim("client_id", "other-client")]));
    await Denied("inactive token", HttpStatusCode.Forbidden,
        f.Token(extra: [new Claim("active", "false")]));
    await Denied("cross-consumer request", HttpStatusCode.Forbidden,
        f.Token(), f.OtherConsumer);
    await Denied("empty consumer identifier", HttpStatusCode.BadRequest,
        f.Token(), Guid.Empty);
    await Denied("anonymous empty consumer identifier", HttpStatusCode.Unauthorized,
        consumer: Guid.Empty);
    await Denied("forged subject header", HttpStatusCode.BadRequest,
        f.Token(), forgedSubject: true);

    foreach (var query in new[]
    {
        "?from=2026-10-01T00:00:00Z&to=2026-10-02T00:00:00Z&subject=forged",
        "?from=2026-10-01T00:00:00Z&to=2026-10-02T00:00:00Z&maxRows=1",
        "?from=2026-10-01T00:00:00Z&from=2026-10-01T00:00:00Z&to=2026-10-02T00:00:00Z",
        "?from=2026-10-01T00:00:00&to=2026-10-02T00:00:00Z",
        "?from=2026-10-01T00:00:00%2B01:00&to=2026-10-02T00:00:00Z",
        "?from=2026-10-02T00:00:00Z&to=2026-10-02T00:00:00Z",
        "?from=2026-10-02T00:00:00Z&to=2026-10-01T00:00:00Z",
        "?from=2026-01-01T00:00:00Z&to=2027-01-02T00:00:00Z"
    })
        await Denied("invalid/unsupported period input", HttpStatusCode.BadRequest,
            f.Token(), query: query);

    f.Access.SetGrant(new(f.Consumer, f.Subject, "billing.read",
        DateTimeOffset.UtcNow.AddMinutes(-1)),
        "synthetic-test", "synthetic grant expiry");
    await Denied("expired durable billing grant", HttpStatusCode.Forbidden, f.Token());
    await Denied("forged token billing grant and admin role", HttpStatusCode.Forbidden,
        f.Token(extra:
        [
            new Claim("tenant_permission", f.Consumer + ":billing.read"),
            new Claim("permission", "platform.admin")
        ]));
    f.RestoreGrant();

    f.Access.SetMembership(f.Consumer, f.Member.Id, false,
        "synthetic-test", "synthetic offboard");
    await Denied("inactive membership", HttpStatusCode.Forbidden, f.Token());
    f.Access.SetMembership(f.Consumer, f.Member.Id, true,
        "synthetic-test", "synthetic restore");
    f.RestoreGrant();

    var tenant = f.Access.GetTenants().Single(x => x.Id == f.Consumer);
    f.Database.Transaction(() =>
    {
        f.Database.Put("tenant", f.Consumer.ToString(), tenant with { IsActive = false });
        return true;
    });
    await Denied("inactive consumer", HttpStatusCode.Forbidden, f.Token());
    f.Database.Transaction(() =>
    {
        f.Database.Put("tenant", f.Consumer.ToString(), tenant);
        return true;
    });

    var portalOwner = f.Access.GetTenants().Single(x => x.Id != f.Consumer && x.Id != f.OtherConsumer);
    f.Database.Transaction(() => { f.Database.Put("tenant", portalOwner.Id.ToString(), portalOwner with { IsActive = false }); return true; });
    await Denied("inactive Portal product owner", HttpStatusCode.Forbidden, f.Token());
    f.Database.Transaction(() => { f.Database.Put("tenant", portalOwner.Id.ToString(), portalOwner); return true; });

    f.Database.Transaction(() =>
    {
        f.Database.Put("client", f.RegisteredClient.Id.ToString(),
            f.RegisteredClient with { IsActive = false });
        return true;
    });
    await Denied("inactive registered Portal client", HttpStatusCode.Forbidden, f.Token());

    f.Database.Transaction(() =>
    {
        f.Database.Put("client", f.RegisteredClient.Id.ToString(),
            f.RegisteredClient with { AllowedScopes = [] });
        return true;
    });
    await Denied("registered scope revoked", HttpStatusCode.Forbidden, f.Token());
    f.Database.Transaction(() =>
    {
        f.Database.Put("client", f.RegisteredClient.Id.ToString(), f.RegisteredClient);
        return true;
    });

    var duplicateClient = f.RegisteredClient with { Id = Guid.NewGuid() };
    f.Database.Transaction(() => { f.Database.Put("client", duplicateClient.Id.ToString(), duplicateClient); return true; });
    await Denied("ambiguous registered Portal client", HttpStatusCode.Forbidden, f.Token());
    f.Database.Transaction(() => { f.Database.Put("client", duplicateClient.Id.ToString(), duplicateClient with { Key = "synthetic-other-client" }); return true; });
    f.Database.Transaction(() => { f.Database.Put("client", f.RegisteredClient.Id.ToString(), f.RegisteredClient with { AllowedScopes = null! }); return true; });
    await Denied("invalid registered scope collection", HttpStatusCode.Forbidden, f.Token());
    f.Database.Transaction(() => { f.Database.Put("client", f.RegisteredClient.Id.ToString(), f.RegisteredClient); return true; });

    var beforeReady = f.Snapshot();
    using (var ready = await f.Get(f.Token()))
    {
        Check(ready.StatusCode == HttpStatusCode.OK, "authorized empty recorded account returns 200");
        Check(ready.Headers.CacheControl?.NoStore == true, "ready response is no-store");
        using var body = JsonDocument.Parse(await ready.Content.ReadAsStringAsync());
        var envelope = body.RootElement;
        var report = envelope.GetProperty("report");
        Check(envelope.GetProperty("subject").GetString() == f.Subject,
            "envelope binds the validated human subject");
        Check(report.GetProperty("consumerTenantId").GetGuid() == f.Consumer
            && report.GetProperty("status").GetString() == "ready"
            && report.GetProperty("basis").GetString() == "posted-ledger",
            "ready response binds consumer and posted-ledger basis");
        Check(report.GetProperty("totalAmount").ValueKind == JsonValueKind.String
            && report.GetProperty("totalAmount").GetString() == "0"
            && report.GetProperty("currency").GetString() == "IRR"
            && report.GetProperty("items").GetArrayLength() == 0,
            "persisted empty ledger returns an exact string zero");
        Check(!report.TryGetProperty("reasonCode", out _)
            && !report.TryGetProperty("entries", out _)
            && !report.TryGetProperty("actor", out _),
            "transport omits internal diagnostics and ledger fields");
    }
    Check(f.Snapshot() == beforeReady, "successful report leaves Fabric unchanged");

    f.RemoveAccount();
    var beforeAbsent = f.Snapshot();
    using (var absent = await f.Get(f.Token()))
    {
        Check(absent.StatusCode == HttpStatusCode.OK, "absent account is a valid report response");
        using var body = JsonDocument.Parse(await absent.Content.ReadAsStringAsync());
        var report = body.RootElement.GetProperty("report");
        Check(report.GetProperty("status").GetString() == "notRecorded"
            && report.GetProperty("currency").ValueKind == JsonValueKind.Null
            && report.GetProperty("totalAmount").ValueKind == JsonValueKind.Null
            && report.GetProperty("items").GetArrayLength() == 0,
            "missing accounting never becomes a manufactured zero");
    }
    Check(f.Snapshot() == beforeAbsent, "absent report leaves Fabric unchanged");

    var guarded = new ConsumerUsageReportStore(f.Database);
    var authorizeCalls = 0;
    var deniedAfterRead = false;
    var beforeGuard = f.Snapshot();
    try
    {
        guarded.Read(f.Consumer, Fixture.From, Fixture.To,
            () => ++authorizeCalls == 1);
    }
    catch (UnauthorizedAccessException)
    {
        deniedAfterRead = true;
    }
    Check(deniedAfterRead && authorizeCalls == 2,
        "guard denies access lost after projection before returning");
    Check(f.Snapshot() == beforeGuard,
        "post-read authorization denial leaves Fabric unchanged");

    // A malformed authoritative tenant document makes the read unavailable,
    // without disclosing storage diagnostics or any partial financial response.
    f.CorruptTenant();
    var beforeOutage = f.Snapshot();
    using (var outage = await f.Get(f.Token()))
    {
        var text = await outage.Content.ReadAsStringAsync();
        Check(outage.StatusCode == HttpStatusCode.ServiceUnavailable,
            "authoritative storage corruption is sanitized 503");
        Check(outage.Headers.CacheControl?.NoStore == true,
            "storage failure response is no-store");
        Check(!text.Contains("SECRET", StringComparison.Ordinal)
            && !text.Contains("Sqlite", StringComparison.Ordinal)
            && !text.Contains("totalAmount", StringComparison.Ordinal),
            "storage failure exposes no diagnostics or partial finance");
    }
    Check(f.Snapshot() == beforeOutage, "storage failure leaves Fabric unchanged");
}

await using (var disabled = await Fixture.Start(enabled: false))
{
    var before = disabled.Snapshot();
    using var response = await disabled.Get(disabled.Token());
    Check(response.StatusCode == HttpStatusCode.ServiceUnavailable,
        "default-disabled endpoint fails closed as 503 for authenticated caller");
    Check(disabled.Snapshot() == before,
        "disabled endpoint leaves Fabric unchanged");
}

Console.WriteLine($"PASS consumer usage API: {checks} checks");

sealed class Fixture : IAsyncDisposable
{
    public static readonly DateTimeOffset From =
        new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset To =
        new(2026, 10, 2, 0, 0, 0, TimeSpan.Zero);

    private const string Issuer = "https://synthetic-idp.invalid/realms/fanasa";
    private const string Audience = "synthetic-central-api";
    private const string ClientId = "synthetic-portal-client";
    private const string Scope = "synthetic.consumer.report";
    private readonly string directory;
    private readonly string databasePath;
    private readonly WebApplication app;
    private readonly RSA rsa;
    private readonly RsaSecurityKey key;

    public HttpClient Client { get; private set; } = null!;
    public FabricDatabase Database { get; }
    public PersistentAccessManagement Access { get; }
    public Guid Consumer { get; }
    public Guid OtherConsumer { get; }
    public string Subject { get; } = Guid.NewGuid().ToString("D");
    public TenantUser Member { get; }
    public ProductClient RegisteredClient { get; }

    private Fixture(bool enabled)
    {
        directory = Path.Combine(Path.GetTempPath(),
            "synthetic-consumer-usage-api-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        databasePath = Path.Combine(directory, "fabric.db");

        var builder = WebApplication.CreateBuilder(
            new WebApplicationOptions { EnvironmentName = "Development" });
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Platform:RegistryPath"] = Path.Combine(directory, "registry.db"),
            ["ConsumerUsageReport:Enabled"] = enabled ? "true" : "false",
            ["ConsumerUsageReport:PortalProductKey"] = "synthetic-portal",
            ["ConsumerUsageReport:PortalClientId"] = ClientId,
            ["ConsumerUsageReport:Scope"] = Scope
        });

        var registry = new PlatformRegistry(builder.Configuration);
        Database = new FabricDatabase(databasePath);
        Access = new PersistentAccessManagement(
            Database, new InMemoryAccessManagement(registry));

        var owner = Access.GetTenants().Single().Id;
        Consumer = Access.CreateTenant("synthetic-a", "Synthetic A").Id;
        OtherConsumer = Access.CreateTenant("synthetic-b", "Synthetic B").Id;
        registry.SetTenantSource(Access.GetTenants);
        registry.SetMembershipSource(Access.GetUsers);

        var product = registry.Register(new(
            Guid.NewGuid(), "synthetic-portal", "Synthetic Portal",
            "synthetic-portal-web", "fanasa.ayin",
            null, null, owner, "synthetic-owner"), "synthetic-test");

        RegisteredClient = Access.RegisterClient(product.Id, new(
            ClientId, "Synthetic Portal client", "public", [], [Scope]));
        Member = Access.AddUser(new(Consumer, Subject, "Synthetic human"));
        RestoreGrant();

        Database.Transaction(() =>
        {
            Database.Put("accounting", Consumer.ToString(),
                new BillingAccount(Consumer, 0, "postpaid", "IRR", 0, [], [], []));
            Database.Put("accounting", OtherConsumer.ToString(),
                new BillingAccount(OtherConsumer, 0, "postpaid", "IRR", 0, [], [], []));
            return true;
        });

        rsa = RSA.Create(2048);
        key = new RsaSecurityKey(rsa) { KeyId = "synthetic-usage-test-key" };
        var oidc = new OpenIdConnectConfiguration { Issuer = Issuer };
        oidc.SigningKeys.Add(key);

        builder.Services.AddSingleton(Database);
        builder.Services.AddSingleton(Access);
        builder.Services.AddSingleton(registry);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddControllers()
            .AddApplicationPart(typeof(ConsumerUsageReportApi).Assembly);
        builder.Services.AddAuthentication(
                CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options => options.Cookie.Name = "synthetic-usage-cookie")
            .AddJwtBearer("PlatformBearer", options =>
            {
                options.MapInboundClaims = false;
                options.RequireHttpsMetadata = true;
                options.IncludeErrorDetails = false;
                options.Configuration = oidc;
                options.TokenValidationParameters = new()
                {
                    ValidateIssuer = true,
                    ValidIssuer = Issuer,
                    ValidateAudience = true,
                    ValidAudience = Audience,
                    ValidateIssuerSigningKey = true,
                    RequireSignedTokens = true,
                    ValidateLifetime = true,
                    RequireExpirationTime = true,
                    ClockSkew = TimeSpan.Zero,
                    ValidTypes = ["JWT", "at+jwt"],
                    NameClaimType = "sub"
                };
            });
        builder.Services.AddAuthorization();

        app = builder.Build();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers().RequireAuthorization();
        app.MapGet("/synthetic-login", async (HttpContext context) =>
        {
            await context.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                new ClaimsPrincipal(new ClaimsIdentity(
                [
                    new Claim("sub", Subject),
                    new Claim("permission", "platform.admin")
                ], "synthetic-cookie")));
            return Results.NoContent();
        }).AllowAnonymous();
    }

    public static async Task<Fixture> Start(bool enabled = true)
    {
        var fixture = new Fixture(enabled);
        await fixture.app.StartAsync();
        var addresses = fixture.app.Services
            .GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!;
        fixture.Client = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = true
        })
        {
            BaseAddress = new Uri(addresses.Addresses.Single())
        };
        return fixture;
    }

    public void RestoreGrant() => Access.SetGrant(
        new(Consumer, Subject, "billing.read", null),
        "synthetic-test", "synthetic report read");

    public string Token(
        string? issuer = null,
        string? audience = null,
        string? client = null,
        string? scope = null,
        bool humanSession = true,
        bool expired = false,
        bool badSignature = false,
        Claim[]? extra = null)
    {
        var now = DateTime.UtcNow;
        var claims = new List<Claim>
        {
            new("sub", Subject),
            new("azp", client ?? ClientId),
            new("scope", scope ?? Scope)
        };
        if (humanSession)
        {
            claims.Add(new("sid", Guid.NewGuid().ToString("D")));
            claims.Add(new("auth_time",
                new DateTimeOffset(now.AddMinutes(-3)).ToUnixTimeSeconds()
                    .ToString(CultureInfo.InvariantCulture),
                ClaimValueTypes.Integer64));
        }
        if (extra is not null) claims.AddRange(extra);

        RSA? other = badSignature ? RSA.Create(2048) : null;
        try
        {
            var signingKey = other is null ? key
                : new RsaSecurityKey(other) { KeyId = key.KeyId };
            var jwt = new JwtSecurityToken(
                issuer ?? Issuer,
                audience ?? Audience,
                claims,
                notBefore: now.AddMinutes(-2),
                expires: expired ? now.AddMinutes(-1) : now.AddMinutes(5),
                signingCredentials: new SigningCredentials(
                    signingKey, SecurityAlgorithms.RsaSha256));
            return new JwtSecurityTokenHandler().WriteToken(jwt);
        }
        finally
        {
            other?.Dispose();
        }
    }

    public async Task<HttpResponseMessage> Get(
        string? token,
        Guid? consumer = null,
        string? query = null,
        bool forgedSubject = false)
    {
        var path = "/api/accounting/v1/consumers/"
            + (consumer ?? Consumer).ToString("D")
            + "/usage-report"
            + (query ?? "?from=2026-10-01T00:00:00Z&to=2026-10-02T00:00:00Z");
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (token is not null)
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
        if (forgedSubject)
            request.Headers.TryAddWithoutValidation("X-Subject", Subject);
        return await Client.SendAsync(request);
    }

    public string Snapshot()
    {
        using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Pooling = false
            }.ToString());
        connection.Open();
        var rows = new List<string[]>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT kind,key,json FROM fabric_documents ORDER BY kind,key";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                rows.Add([reader.GetString(0), reader.GetString(1), reader.GetString(2)]);
        }
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT CAST(id AS TEXT),key,kind,payload,recorded FROM fabric_outbox ORDER BY id";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                rows.Add([
                    reader.GetString(0), reader.GetString(1),
                    reader.GetString(2), reader.GetString(3), reader.GetString(4)
                ]);
        }
        return JsonSerializer.Serialize(rows);
    }

    public void RemoveAccount()
    {
        using var connection = OpenRaw();
        using var command = connection.CreateCommand();
        command.CommandText =
            "DELETE FROM fabric_documents WHERE kind='accounting' AND key=$key";
        command.Parameters.AddWithValue("$key", Consumer.ToString());
        command.ExecuteNonQuery();
    }

    public void CorruptTenant()
    {
        using var connection = OpenRaw();
        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE fabric_documents SET json=$json WHERE kind='tenant' AND key=$key";
        command.Parameters.AddWithValue("$json", "{SECRET");
        command.Parameters.AddWithValue("$key", Consumer.ToString());
        command.ExecuteNonQuery();
    }

    private SqliteConnection OpenRaw()
    {
        var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                Pooling = false
            }.ToString());
        connection.Open();
        return connection;
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        Database.Dispose();
        rsa.Dispose();
        SqliteConnection.ClearAllPools();

        var target = Path.GetFullPath(directory);
        var temp = Path.GetFullPath(Path.GetTempPath());
        if (!target.StartsWith(temp, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(target).StartsWith(
                "synthetic-consumer-usage-api-", StringComparison.Ordinal))
            throw new InvalidOperationException("Synthetic cleanup escaped its temporary directory.");
        Directory.Delete(target, recursive: true);
    }
}
