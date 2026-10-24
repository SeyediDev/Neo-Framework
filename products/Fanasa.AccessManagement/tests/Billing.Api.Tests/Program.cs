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

if (args.Contains("--preview"))
{
    await using var fixture = await Fixture.Start();
    fixture.Access.SetGrant(new(fixture.Consumer,fixture.Subject,"billing.write",null),"synthetic","local UI fixture");
    var at = new DateTimeOffset(2026,1,15,0,0,0,TimeSpan.Zero);
    var charge = new LedgerEntry(Guid.NewGuid(),"synthetic-ui-charge","usage","سرویس آزمایشی","unit.save",1,
        9007199254740993m,at,fixture.Subject,"Synthetic browser fixture",UnitPrice:9007199254740993m);
    fixture.Database.Transaction(()=> { fixture.Database.Put("accounting",fixture.Consumer.ToString(),
        new BillingAccount(fixture.Consumer,1,"postpaid","IRR",10000000000000000m,[],[charge],[])); return true; });
    fixture.Accounting.IssueInvoice(fixture.Consumer,new(1,"synthetic-ui-invoice","2026-01","Synthetic UI fixture"),fixture.Subject);
    Console.WriteLine("BILLING_PREVIEW_URL="+fixture.Client.BaseAddress);
    await Task.Delay(TimeSpan.FromMinutes(15));
    return;
}

// All identities, signed JWTs, cookies and ledger entries below are synthetic and loopback only.
var checks = 0;
void Check(bool value, string label) { if (!value) throw new Exception("FAILED: " + label); checks++; Console.WriteLine("PASS billing API: " + label); }
await using (var f = await Fixture.Start())
{
    async Task<HttpResponseMessage> Summary(string? token, Guid? tenant = null, string query = "")
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/accounting/v1/consumers/" + (tenant ?? f.Consumer) + "/account" + query);
        if (token is not null) request.Headers.Authorization = new("Bearer", token);
        return await f.Client.SendAsync(request);
    }
    foreach (var item in new (string Name, string? Token, HttpStatusCode Expected)[]
    {
        (Name:"anonymous", Token:(string?)null, Expected:HttpStatusCode.Unauthorized),
        ("bad signature", f.Token(badSignature:true), HttpStatusCode.Unauthorized),
        ("expired", f.Token(expired:true), HttpStatusCode.Unauthorized),
        ("catalog credential", f.Token(scope:"platform.catalog"), HttpStatusCode.Forbidden),
        ("machine credential", f.Token(humanSession:false), HttpStatusCode.Forbidden),
        ("wrong client", f.Token(client:"other"), HttpStatusCode.Forbidden),
        ("client credentials grant", f.Token(extra:[new Claim("gty","client_credentials")]), HttpStatusCode.Forbidden),
        ("wrong audience", f.Token(audience:"other"), HttpStatusCode.Unauthorized)
    })
    {
        var snapshot=f.Snapshot();
        using var result=await Summary(item.Token);
        Check(result.StatusCode==item.Expected,item.Name+" denied");
        Check(snapshot==f.Snapshot(),item.Name+" has no financial effect");
    }
    using(var cross=await Summary(f.Token(),f.OtherConsumer)) Check(cross.StatusCode==HttpStatusCode.Forbidden,"cross tenant denied");
    using(var input=await Summary(f.Token(),query:"?subject=forged")) Check(input.StatusCode==HttpStatusCode.BadRequest,"subject input rejected");
    using(var ready=await Summary(f.Token()))
    {
        using var json=JsonDocument.Parse(await ready.Content.ReadAsStringAsync());
        var account=json.RootElement.GetProperty("account");
        Check(ready.IsSuccessStatusCode && ready.Headers.CacheControl?.NoStore==true,"ready no-store");
        Check(json.RootElement.GetProperty("subject").GetString()==f.Subject,"bound human subject");
        Check(account.GetProperty("status").GetString()=="ready" && account.GetProperty("outstanding").GetString()=="0","recorded zero exact");
        Check(!account.TryGetProperty("entries",out _) && !account.TryGetProperty("actor",out _),"no ledger or actor");
    }
    using(var login=await f.Client.GetAsync("/synthetic-login")) Check(login.IsSuccessStatusCode,"local cookie login");
    using(var cookie=await Summary(null)) Check(cookie.StatusCode==HttpStatusCode.Unauthorized,"cookie cannot substitute for bearer");
    using var csrfResponse=await f.Client.GetAsync("/synthetic-csrf");
    using var csrfJson=JsonDocument.Parse(await csrfResponse.Content.ReadAsStringAsync());
    var csrf=csrfJson.RootElement.GetProperty("token").GetString()!;
    async Task<HttpResponseMessage> Post(string operation,object value,bool token=true,Guid? tenant=null)
    {
        using var request=new HttpRequestMessage(HttpMethod.Post,"/api/accounting/v1/tenants/"+(tenant??f.Consumer)+"/"+operation);
        request.Content=new StringContent(JsonSerializer.Serialize(value),Encoding.UTF8,"application/json");
        if(token)request.Headers.Add("X-CSRF-TOKEN",csrf);
        return await f.Client.SendAsync(request);
    }
    var at=new DateTimeOffset(2026,1,15,0,0,0,TimeSpan.Zero);
    var charge=new LedgerEntry(Guid.NewGuid(),"synthetic-charge","usage","synthetic-product","unit.save",5,10,at,f.Subject,"synthetic backend result",UnitPrice:2);
    f.Database.Transaction(()=>{f.Database.Put("accounting",f.Consumer.ToString(),new BillingAccount(f.Consumer,1,"postpaid","IRR",100,[],[charge],[]));return true;});
    var issue=new BillingInvoiceCommand(1,"synthetic-invoice","2026-01","synthetic finance approval");
    var beforeDenied=f.Snapshot();
    using(var noCsrf=await Post("invoices",issue,token:false))Check(noCsrf.StatusCode==HttpStatusCode.BadRequest,"CSRF required (actual "+(int)noCsrf.StatusCode+")");
    using(var noGrant=await Post("invoices",issue))Check(noGrant.StatusCode==HttpStatusCode.Forbidden,"admin claim does not grant financial write");
    Check(beforeDenied==f.Snapshot(),"denied writes leave ledger/outbox unchanged");
    f.Access.SetGrant(new(f.Consumer,f.Subject,"billing.write",null),"synthetic","synthetic approval");
    foreach(var reserved in new[]{"measured:","organization:","zarinpal:","billing-note:"})
    {
        var beforeReserved=f.Snapshot();
        using var rejected=await Post("invoices",issue with {Key=reserved+"synthetic-invoice"});
        Check(rejected.StatusCode==HttpStatusCode.BadRequest,"invoice rejects internal namespace "+reserved);
        Check(beforeReserved==f.Snapshot(),"reserved invoice identity has no financial effect "+reserved);
    }
    using(var issued=await Post("invoices",issue))Check(issued.IsSuccessStatusCode,"live financial writer issues invoice");
    var invoice=f.Accounting.Read(f.Consumer).Account.Invoices.Single();
    var frozen=JsonSerializer.Serialize(invoice);
    Check(invoice.Amount==10 && invoice.Lines?.Single().UnitPrice==2 && invoice.ContentHash?.Length==64,"invoice freezes historical charge and hash");
    var afterIssue=f.Snapshot();
    using(var replay=await Post("invoices",issue))Check(replay.IsSuccessStatusCode,"identical invoice retry");
    Check(afterIssue==f.Snapshot(),"invoice retry no extra event");
    using(var conflict=await Post("invoices",issue with {Reference="different"}))Check(conflict.StatusCode==HttpStatusCode.Conflict,"different payload conflicts");
    using(var duplicate=await Post("invoices",issue with {ExpectedRevision=2,Key="second-key"}))Check(duplicate.StatusCode==HttpStatusCode.Conflict,"period cannot be reissued with a new key");
    var note=new BillingCreditNoteCommand(2,"synthetic-note",charge.Id,invoice.Id,3,"synthetic correction","synthetic evidence");
    using(var credit=await Post("credit-notes",note))Check(credit.IsSuccessStatusCode,"bounded credit note accepted");
    Check(f.Accounting.Read(f.Consumer).Outstanding==7,"credit lowers outstanding");
    Check(JsonSerializer.Serialize(f.Accounting.Read(f.Consumer).Account.Invoices.Single())==frozen,"issued invoice unchanged");
    var afterNote=f.Snapshot();
    using(var replay=await Post("credit-notes",note))Check(replay.IsSuccessStatusCode,"credit retry accepted");
    Check(afterNote==f.Snapshot(),"credit retry no second financial effect");
    using(var over=await Post("credit-notes",note with {ExpectedRevision=3,Key="over-credit",Amount=8}))Check(over.StatusCode==HttpStatusCode.BadRequest,"credit cannot exceed charge");
    using(var cross=await Post("credit-notes",note with {Key="cross"},tenant:f.OtherConsumer))Check(cross.StatusCode==HttpStatusCode.Forbidden,"cross tenant credit denied");
    using(var summary=await Summary(f.Token()))
    {
        using var json=JsonDocument.Parse(await summary.Content.ReadAsStringAsync());
        var account=json.RootElement.GetProperty("account");var row=account.GetProperty("invoices")[0];
        Check(account.GetProperty("outstanding").GetString()=="7","summary exact corrected debt");
        Check(row.GetProperty("amount").GetString()=="10" && row.GetProperty("creditedAmount").GetString()=="3" && row.GetProperty("remainingAmount").GetString()=="7","summary preserves invoice and shows separate correction");
        Check(row.GetProperty("status").GetString()=="partiallyPaid","summary status matches applications");
    }
    using (var exactRejected = await Post("credit-notes",new {
        expectedRevision=3,key="precision-reject",chargeId=charge.Id,invoiceId=invoice.Id,
        amount="1.000000000000000000000000000000001",reason="synthetic",reference="synthetic"}))
        Check(exactRejected.StatusCode==HttpStatusCode.BadRequest,"decimal input cannot silently lose precision");
    using (var exponentRejected = await Post("credit-notes",new {
        expectedRevision=3,key="exponent-reject",chargeId=charge.Id,invoiceId=invoice.Id,
        amount="1e0",reason="synthetic",reference="synthetic"}))
        Check(exponentRejected.StatusCode==HttpStatusCode.BadRequest,"noncanonical decimal exponent rejected");
    Check(afterNote==f.Snapshot(),"invalid numeric binding has no financial effect");

    f.Access.SetGrant(new(f.Consumer,f.Subject,"billing.read",DateTimeOffset.UtcNow.AddMinutes(-1)),"synthetic","synthetic expiry");
    using(var revoked=await Summary(f.Token()))Check(revoked.StatusCode==HttpStatusCode.Forbidden,"revoked financial read checked live");
    f.RestoreGrant();
    f.Access.SetGrant(new(f.Consumer,f.Subject,"billing.write",DateTimeOffset.UtcNow.AddMinutes(-1)),"synthetic","synthetic expiry");
    using(var revoked=await Post("credit-notes",note with{ExpectedRevision=3,Key="revoked",Amount=1}))Check(revoked.StatusCode==HttpStatusCode.Forbidden,"revoked financial write checked live");
    var calls=0;var denied=false;
    try{ConsumerAccountProjection.Read(f.Database,f.Accounting,f.Consumer,TimeProvider.System,()=>++calls==1);}catch(UnauthorizedAccessException){denied=true;}
    Check(denied && calls==2,"account read rechecks authority after projection");
    f.Access.SetGrant(new(f.Consumer,f.Subject,"billing.meter",null),"synthetic","synthetic legacy meter grant");
    var beforeRawMeter=f.Snapshot();
    using (var rawMeter = new HttpRequestMessage(HttpMethod.Post,"/api/accounting/tenants/"+f.Consumer))
    {
        rawMeter.Headers.Add("X-CSRF-TOKEN",csrf);
        rawMeter.Content=new StringContent(JsonSerializer.Serialize(new BillingCommand(3,"usage","unproven-public-charge",null,null,0,
            charge.ProductKey,charge.Metric,0,1,0,"HTTP success is not backend completion",null,null)),Encoding.UTF8,"application/json");
        using var rejected=await f.Client.SendAsync(rawMeter);
        Check(rejected.StatusCode==HttpStatusCode.BadRequest,"public browser command cannot manufacture billable backend completion");
    }
    Check(beforeRawMeter==f.Snapshot(),"unproven public consumption leaves ledger and outbox unchanged");
    var wrongStoreRejected=false;
    try { ConsumerAccountProjection.Read(f.Database,new AccountingStore(Path.GetTempPath(),f.Access),f.Consumer,TimeProvider.System,()=>true); }
    catch(InvalidOperationException){wrongStoreRejected=true;}
    Check(wrongStoreRejected,"financial projection cannot mix two storage adapters");

    f.RemoveAccount();
    using(var absent=await Summary(f.Token()))
    {
        using var json=JsonDocument.Parse(await absent.Content.ReadAsStringAsync());var account=json.RootElement.GetProperty("account");
        Check(account.GetProperty("status").GetString()=="notRecorded" && account.GetProperty("walletBalance").ValueKind==JsonValueKind.Null && account.GetProperty("invoices").GetArrayLength()==0,"unrecorded account exposes no manufactured zero");
    }
}
Console.WriteLine("PASS billing API: "+checks+" checks");

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
    public AccountingStore Accounting { get; }
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
            new WebApplicationOptions { EnvironmentName = "Development", ContentRootPath = Path.GetFullPath("products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web") });
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

        Accounting = new AccountingStore(directory, Access, TimeProvider.System, Database);
        builder.Services.AddSingleton(Accounting);
        builder.Services.AddAntiforgery(options => options.HeaderName = "X-CSRF-TOKEN");
        builder.Services.AddSingleton(Database);
        builder.Services.AddSingleton(Access);
        builder.Services.AddSingleton(registry);
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<IAccessManagement>(Access);
        builder.Services.AddSingleton(new Fanasa.AccessManagement.Web.Payments.ZarinpalOptions());
        builder.Services.AddTransient<IClaimsTransformation, Fanasa.AccessManagement.Web.Security.FabricClaimsTransformation>();
        builder.Services.AddRazorPages().AddApplicationPart(typeof(ConsumerUsageReportApi).Assembly);
        builder.Services.AddControllersWithViews()
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
        app.UseStaticFiles();
        app.UseRouting();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapRazorPages();
        app.MapControllers().RequireAuthorization();
        app.MapGet("/synthetic-csrf", (HttpContext context, Microsoft.AspNetCore.Antiforgery.IAntiforgery anti) => Results.Json(new { token = anti.GetAndStoreTokens(context).RequestToken })).RequireAuthorization();
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
