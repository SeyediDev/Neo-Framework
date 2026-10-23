using System.Security.Claims;
using System.Text.Json;
using Fanasa.UnifiedPortal.Web;
using Fanasa.UnifiedPortal.Web.Pages;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

static class WorkspaceTests
{
    public static async Task<int> RunAsync()
    {
        var count = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception(name);
            Console.WriteLine("PASS service workspace: " + name);
            count++;
        }
        var orgA = new PlatformOrganizationView(Guid.NewGuid(), "اول", "first");
        var orgB = new PlatformOrganizationView(Guid.NewGuid(), "دوم", "second");
        var producer = Guid.NewGuid();
        var product = new PlatformProductView(Guid.NewGuid(), "work", "</script><script>alert(1)</script>", "app", "fanasa.rayan",
            "https://services.example.test/work", null, producer, "producer", null);
        var source = new JourneyStub(orgA.Id, orgB.Id);
        var reader = new CatalogStub(new(CatalogStatus.Ready, [orgA, orgB], [product], orgA.Id));
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["PlatformControlCenter:BaseUrl"] = "https://control.example.test/pcc"
        }).Build();
        WorkspaceModel Model(IPlatformCatalogClient catalog, IWorkspaceJourneySource journey, bool admin = false,
            string subject = "private-subject", bool mappedIdentifier = false, IServiceDiscoveryClient? discovery = null)
        {
            var model = new WorkspaceModel(catalog, journey, configuration, discovery) { PageContext = new() { HttpContext = new DefaultHttpContext() } };
            var claims = new List<Claim> { new(mappedIdentifier ? ClaimTypes.NameIdentifier : "sub", subject), new(ClaimTypes.Name, "user") };
            if (admin) claims.Add(new(ClaimTypes.Role, "PlatformAdmin"));
            model.HttpContext.User = new(new ClaimsIdentity(claims, "Cookies"));
            return model;
        }
        JsonElement Bootstrap(WorkspaceModel model) => JsonDocument.Parse(model.BootstrapJson).RootElement.Clone();
        bool JourneyHidden(JsonElement data) => data.GetProperty("catalog").GetArrayLength() == 0
            && data.GetProperty("requests").GetArrayLength() == 0 && data.GetProperty("publications").GetArrayLength() == 0
            && data.GetProperty("usage").GetProperty("status").GetString() == "unavailable"
            && data.GetProperty("usage").GetProperty("totalAmount").GetString() == ""
            && data.GetProperty("usage").GetProperty("items").GetArrayLength() == 0;
        var page = Model(reader, source);
        await page.OnGetAsync(orgA.Id, default);
        var json = Bootstrap(page);
        Check(typeof(WorkspaceModel).GetCustomAttributes(typeof(AuthorizeAttribute), true).Length == 1, "page requires authentication");
        Check(page.Response.Headers.CacheControl == "no-store", "personal data is not cached");
        Check(source.Calls == 1 && source.Subject == "private-subject" && source.Organization == orgA.Id, "journey reader receives trusted subject and verified consumer organization");
        Check(json.GetProperty("activeServices").GetArrayLength() == 1 && producer != orgA.Id, "authorized products from another producer remain visible");
        Check(!page.BootstrapJson.Contains("private-subject") && json.GetProperty("subjectKey").GetString()!.Length == 64, "storage key hides raw identity");
        Check(!page.BootstrapJson.Contains("</script>") && json.GetProperty("activeServices")[0].GetProperty("name").GetString() == product.DisplayName, "bootstrap cannot terminate JSON script element");
        Check(json.GetProperty("requests").GetArrayLength() == 1 && json.GetProperty("publications").GetArrayLength() == 1, "foreign organization requests and publications excluded");
        Check(json.GetProperty("requests")[0].GetProperty("id").GetString() == source.OwnRequestId
            && json.GetProperty("publications")[0].GetProperty("id").GetString() == source.OwnPublicationId,
            "same organization rows with case-only foreign subjects are excluded");
        Check(!json.GetProperty("requests")[0].TryGetProperty("requestedBySubject", out _)
            && !json.GetProperty("publications")[0].TryGetProperty("submittedBySubject", out _),
            "row ownership subjects are never serialized into browser bootstrap");
        Check(json.GetProperty("publications")[0].GetProperty("url").GetString() == "", "unsafe supplier launch URL rejected");
        Check(json.GetProperty("usage").GetProperty("totalAmount").GetString() == "9007199254740993.0100", "exact money string retained");
        Check(!page.IsAdmin, "ordinary consumer has no preview review controls");
        Check(page.AccountingUrl is null, "synthetic preview never exposes a real central accounting link");
        var admin = Model(reader, source, true);
        await admin.OnGetAsync(orgA.Id, default);
        Check(admin.IsAdmin && Bootstrap(admin).GetProperty("isAdmin").GetBoolean(), "explicit platform role enables synthetic review");
        var oldCalls = source.Calls;
        await page.OnGetAsync(Guid.NewGuid(), default);
        json = Bootstrap(page);
        Check(page.ActiveOrganizationId is null && json.GetProperty("catalog").GetArrayLength() == 0 && json.GetProperty("activeServices").GetArrayLength() == 0 && source.Calls == oldCalls, "tampered organization cannot load journey or products");
        await page.OnGetAsync(orgB.Id, default);
        Check(page.Status == CatalogStatus.Unavailable && Bootstrap(page).GetProperty("activeServices").GetArrayLength() == 0 && source.Calls == oldCalls, "wrong consumer product envelope cannot load journey");
        page.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
        await page.OnGetAsync(orgA.Id, default);
        Check(page.Organizations.Count == 0 && page.ActiveOrganizationId is null && Bootstrap(page).GetProperty("subjectKey").GetString() == "", "anonymous state clears prior personal information");
        var production = Model(reader, new NotConnectedWorkspaceJourneySource(), true);
        await production.OnGetAsync(orgA.Id, default);
        json = Bootstrap(production);
        Check(json.GetProperty("mode").GetString() == "connected" && !production.IsAdmin, "production never enables synthetic review");
        Check(json.GetProperty("catalog").GetArrayLength() == 0 && json.GetProperty("requests").GetArrayLength() == 0 && json.GetProperty("usage").GetProperty("status").GetString() == "notConnected", "missing central contracts stay explicit, never fake zero consumption");
        Check(json.GetProperty("activeServices").GetArrayLength() == 1, "production retains authorized service launches");
        var accountingUrl = "https://control.example.test/pcc/Accounting?tenant=" + orgA.Id;
        Check(production.AccountingUrl == accountingUrl && producer != orgA.Id,
            "production accounting link targets the verified consumer organization instead of the producer");
        Check(WorkspaceModel.CentralAccountingUrl("https://control.example.test/pcc/", orgA.Id) == accountingUrl,
            "central accounting URL preserves base path and normalizes trailing slash");
        Check(WorkspaceModel.CentralAccountingUrl("https://control.example.test", orgA.Id)
            == "https://control.example.test/Accounting?tenant=" + orgA.Id,
            "central accounting URL supports a host without a base path");
        Check(WorkspaceModel.CentralAccountingUrl("http://127.0.0.1:5204/pcc/", orgA.Id)
            == "http://127.0.0.1:5204/pcc/Accounting?tenant=" + orgA.Id,
            "central accounting URL supports an explicit HTTP base");
        Check(WorkspaceModel.CentralAccountingUrl("https://control.example.test/pcc", Guid.Empty) is null,
            "central accounting URL rejects an empty consumer organization");
        string?[] invalidAccountingBases = [null, "", "control.example.test/pcc", "/pcc", "ftp://control.example.test/pcc",
            "javascript:alert(1)", "https://user:password@control.example.test/pcc", "https://control.example.test/pcc?subject=other",
            "https://control.example.test/pcc#fragment"];
        Check(invalidAccountingBases.All(value => WorkspaceModel.CentralAccountingUrl(value, orgA.Id) is null),
            "central accounting URL rejects absent configuration, credentials, unsupported schemes, query and fragment");
        await production.OnGetAsync(Guid.NewGuid(), default);
        Check(production.AccountingUrl is null && production.ActiveOrganizationId is null,
            "reused production model clears accounting link for an untrusted organization");
        await production.OnGetAsync(orgA.Id, default);
        production.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
        await production.OnGetAsync(orgA.Id, default);
        Check(production.AccountingUrl is null && production.Organizations.Count == 0 && production.ActiveOrganizationId is null,
            "reused production model clears accounting link when session becomes anonymous");
        var mismatched = Model(reader, new JourneyStub(orgB.Id, orgA.Id));
        await mismatched.OnGetAsync(orgA.Id, default);
        json = Bootstrap(mismatched);
        Check(json.GetProperty("catalog").GetArrayLength() == 0 && json.GetProperty("usage").GetProperty("status").GetString() == "unavailable", "wrong usage envelope rejects every journey datum");
        var wrongSubject = Model(reader, new JourneyStub(orgA.Id, orgB.Id, "another-subject"));
        await wrongSubject.OnGetAsync(orgA.Id, default);
        json = Bootstrap(wrongSubject);
        Check(JourneyHidden(json), "same organization with wrong subject envelope hides every journey datum");
        Check(wrongSubject.ActiveOrganizationId == orgA.Id && json.GetProperty("activeServices").GetArrayLength() == 1
            && json.GetProperty("activeServices")[0].GetProperty("url").GetString() == product.Url,
            "wrong journey subject keeps independently authorized active services");
        var wrongCase = Model(reader, new JourneyStub(orgA.Id, orgB.Id, "PRIVATE-SUBJECT"));
        await wrongCase.OnGetAsync(orgA.Id, default);
        Check(JourneyHidden(Bootstrap(wrongCase)) && Bootstrap(wrongCase).GetProperty("activeServices").GetArrayLength() == 1,
            "case-only subject envelope mismatch is rejected ordinally");
        var mappedSource = new JourneyStub(orgA.Id, orgB.Id);
        var mapped = Model(reader, mappedSource, subject: "mapped-cookie-subject", mappedIdentifier: true);
        await mapped.OnGetAsync(orgA.Id, default);
        json = Bootstrap(mapped);
        Check(mappedSource.Subject == "mapped-cookie-subject" && json.GetProperty("requests").GetArrayLength() == 1
            && json.GetProperty("usage").GetProperty("status").GetString() == "ready",
            "mapped NameIdentifier fallback supplies the trusted journey subject");
        var reusedSource = new JourneyStub(orgA.Id, orgB.Id);
        var reused = Model(reader, reusedSource, subject: "first-cookie-subject");
        await reused.OnGetAsync(orgA.Id, default);
        var firstSubjectKey = Bootstrap(reused).GetProperty("subjectKey").GetString();
        var firstRequestId = Bootstrap(reused).GetProperty("requests")[0].GetProperty("id").GetString();
        reused.HttpContext.User = new(new ClaimsIdentity([new Claim("sub", "second-cookie-subject")], "Cookies"));
        await reused.OnGetAsync(orgA.Id, default);
        json = Bootstrap(reused);
        Check(reusedSource.Calls == 2 && reusedSource.Subject == "second-cookie-subject"
            && json.GetProperty("subjectKey").GetString() != firstSubjectKey
            && json.GetProperty("requests").GetArrayLength() == 1
            && json.GetProperty("requests")[0].GetProperty("id").GetString() == reusedSource.OwnRequestId
            && json.GetProperty("requests")[0].GetProperty("id").GetString() != firstRequestId,
            "reused model reloads rows and storage identity for the second authenticated subject");
        var staleSource = new JourneyStub(orgA.Id, orgB.Id, "first-cookie-subject");
        var stale = Model(reader, staleSource, subject: "first-cookie-subject");
        await stale.OnGetAsync(orgA.Id, default);
        Check(Bootstrap(stale).GetProperty("requests").GetArrayLength() == 1, "first subject loads its matching journey envelope");
        stale.HttpContext.User = new(new ClaimsIdentity([new Claim("sub", "second-cookie-subject")], "Cookies"));
        await stale.OnGetAsync(orgA.Id, default);
        json = Bootstrap(stale);
        Check(staleSource.Calls == 2 && staleSource.Subject == "second-cookie-subject" && JourneyHidden(json)
            && json.GetProperty("activeServices").GetArrayLength() == 1,
            "reused model clears previous-subject journey when the provider returns a stale envelope");
        foreach (var status in new[] { CatalogStatus.Forbidden, CatalogStatus.Unavailable, CatalogStatus.NotConfigured })
        {
            var failing = Model(new CatalogStub(new(status, [orgA], [product], orgA.Id)), source);
            oldCalls = source.Calls;
            await failing.OnGetAsync(orgA.Id, default);
            Check(Bootstrap(failing).GetProperty("activeServices").GetArrayLength() == 0 && source.Calls == oldCalls, status + " hides products and suppresses journey requests");
            var disconnected = Model(new CatalogStub(new(status, [orgA], [product], orgA.Id)), new NotConnectedWorkspaceJourneySource());
            await disconnected.OnGetAsync(orgA.Id, default);
            Check(disconnected.AccountingUrl is null, status + " cannot expose a central accounting link");
        }
        var published = new DiscoveryStub(new(CatalogStatus.Ready,
        [new(product.Id, product.Key, product.DisplayName, product.Center, "معرفی خدمت", "https://catalog.example.test/details", "https://catalog.example.test/request", true),
         new(Guid.NewGuid(), "other", "خدمت بدون مجوز", "fanasa.cloud", "توضیح", null, "https://catalog.example.test/request", true)]));
        var discoveryPage = Model(reader, new NotConnectedWorkspaceJourneySource(), discovery: published);
        await discoveryPage.OnGetAsync(orgA.Id, default);
        json = Bootstrap(discoveryPage);
        var services = json.GetProperty("publishedCatalog").GetProperty("services");
        Check(services.GetArrayLength() == 2 && published.Calls == 1, "workspace shows all published services, not only allowed products");
        Check(services[0].GetProperty("accessState").GetString() == "allowed"
            && services[0].GetProperty("launchUrl").GetString() == product.Url, "launch comes only from independently verified consumer product");
        Check(services[1].GetProperty("accessState").GetString() == "requestRequired"
            && services[1].GetProperty("launchUrl").ValueKind == JsonValueKind.Null
            && services[1].GetProperty("requestAccessUrl").GetString() == "https://catalog.example.test/request", "ungranted service has only metadata and the official request link");
        Check(json.GetProperty("centers").EnumerateArray().Any(x => x.GetProperty("id").GetString() == "fanasa.cloud")
            && !json.GetProperty("centers").EnumerateArray().Any(x => x.GetProperty("id").GetString() == "fanasa.fanasa-cloud"), "cloud center uses the same canonical identity as central discovery");
        var snapshot = (Microsoft.AspNetCore.Mvc.ContentResult)await discoveryPage.OnGetSnapshotAsync(orgA.Id, default);
        Check(snapshot.ContentType == "application/json; charset=utf-8" && discoveryPage.Response.Headers.CacheControl == "no-store", "snapshot is JSON and no-store");
        Check(!snapshot.Content!.Contains("private-subject") && !snapshot.Content.Contains("</script>"), "snapshot does not expose raw identity or unescaped HTML");
        Check(Bootstrap(discoveryPage).GetProperty("accountingUrl").GetString() == accountingUrl, "snapshot retains only the verified consumer accounting destination");
        await discoveryPage.OnGetSnapshotAsync(Guid.NewGuid(), default);
        json = Bootstrap(discoveryPage);
        Check(json.GetProperty("activeServices").GetArrayLength() == 0
            && json.GetProperty("accountingUrl").ValueKind == JsonValueKind.Null
            && json.GetProperty("publishedCatalog").GetProperty("services").EnumerateArray().All(x => x.GetProperty("launchUrl").ValueKind == JsonValueKind.Null),
            "untrusted organization clears every launch and financial link while retaining public descriptions");
        var failedDiscovery = Model(reader, new NotConnectedWorkspaceJourneySource(), discovery: new DiscoveryStub(new(CatalogStatus.Unavailable, [])));
        await failedDiscovery.OnGetAsync(orgA.Id, default);
        json = Bootstrap(failedDiscovery);
        Check(json.GetProperty("activeServices").GetArrayLength() == 1
            && json.GetProperty("publishedCatalog").GetProperty("status").GetString() == "Unavailable", "discovery outage cannot erase independently authorized services or turn into successful zero catalog");
        return count;
    }

    private sealed class CatalogStub(PlatformWorkspace data) : IPlatformCatalogClient
    {
        public Task<PlatformWorkspace> GetWorkspaceAsync(string subject, CancellationToken cancellationToken, Guid? organizationId = null) => Task.FromResult(data);
    }
    private sealed class DiscoveryStub(ServiceDiscovery data) : IServiceDiscoveryClient
    {
        public int Calls { get; private set; }
        public Task<ServiceDiscovery> GetAsync(CancellationToken cancellationToken) { Calls++; return Task.FromResult(data); }
    }
    private sealed class JourneyStub(Guid envelope, Guid foreign, string? envelopeSubject = null) : IWorkspaceJourneySource
    {
        public bool IsSyntheticPreview => true;
        public int Calls { get; private set; }
        public string? Subject { get; private set; }
        public Guid Organization { get; private set; }
        public string? OwnRequestId { get; private set; }
        public string? OwnPublicationId { get; private set; }
        public Task<WorkspaceJourneyData> GetAsync(string subject, Guid organizationId, CancellationToken cancellationToken)
        {
            Calls++;
            Subject = subject;
            Organization = organizationId;
            WorkspaceRequest Request(Guid org, string rowSubject) => new(Guid.NewGuid().ToString(), "work", "کار", "monthly", "ماهانه", org, "pending", "2026-10-12T10:00:00Z", "نیاز سازمان", [], rowSubject);
            WorkspacePublication Publication(Guid org, string rowSubject) => new(Guid.NewGuid().ToString(), "سرویس", "fanasa.rayan", "توضیح", "javascript:alert(1)", org, "pending", "2026-10-12T10:00:00Z", [], rowSubject);
            var foreignSubject = subject.ToUpperInvariant();
            WorkspaceRequest[] requests = [Request(envelope, subject), Request(foreign, subject), Request(envelope, foreignSubject)];
            WorkspacePublication[] publications = [Publication(envelope, subject), Publication(foreign, subject), Publication(envelope, foreignSubject)];
            OwnRequestId = requests[0].Id;
            OwnPublicationId = publications[0].Id;
            return Task.FromResult(new WorkspaceJourneyData([new("work", "work", "کار", "fanasa.rayan", "توضیح", [])],
                requests, publications,
                new("ready", "مهر ۱۴۰۵", "2026-10-12T10:00:00Z", "IRR", "9007199254740993.0100", []), envelope, envelopeSubject ?? subject));
        }
    }
}
