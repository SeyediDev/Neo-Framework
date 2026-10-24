using System.Security.Claims;
using System.Text.Json;
using Fanasa.UnifiedPortal.Web;
using Fanasa.UnifiedPortal.Web.Pages;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;

const string subject = "aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee";
var org = new PlatformOrganizationView(Guid.Parse("11111111-2222-4333-8444-555555555555"), "سازمان آزمون", "one");
var now = DateTimeOffset.Parse("2026-10-24T12:00:00Z"); var from = new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);
var usage = new ConsumerUsageView(org.Id, "ready", from, now, "دورهٔ آزمون", now.ToString("O"), "USD", "9007199254740993.10",
    [new("service", "اسناد", "pages", "1", "page", "1.25", "9007199254740993.10", 1)]);
var account = new ConsumerAccountView(org.Id, "ready", now.ToString("O"), "payg", "USD", "9007199254740993.10", "0", "0", []);
var reader = new BillingStub(new("ready", usage, account));
var catalog = new CatalogStub(new(CatalogStatus.Ready, [org], [], org.Id));
var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["PlatformControlCenter:BaseUrl"] = "https://pcc.example.invalid" }).Build();
var checks = 0;
void Check(bool valid, string name) { if (!valid) throw new Exception(name); checks++; Console.WriteLine("PASS " + name); }
WorkspaceModel Model(IWorkspaceJourneySource? journey = null, bool signedIn = true)
{
    var model = new WorkspaceModel(catalog, journey ?? new NotConnectedWorkspaceJourneySource(), config, billing: reader)
        { PageContext = new() { HttpContext = new DefaultHttpContext() } };
    if (signedIn) model.HttpContext.User = new(new ClaimsIdentity([new Claim("sub", subject)], "Cookies"));
    return model;
}
var page = Model(); await page.OnGetAsync(org.Id, default);
using (var json = JsonDocument.Parse(page.BootstrapJson))
{
    var root = json.RootElement;
    Check(reader.Calls == 1 && reader.Subject == subject && reader.Consumer == org.Id, "financial reader receives only authenticated subject and selected authorized organization");
    Check(root.GetProperty("consumerUsage").GetProperty("totalAmount").GetString() == "9007199254740993.10"
        && root.GetProperty("consumerAccount").GetProperty("walletBalance").GetString() == "9007199254740993.10", "Workspace serializes exact report and account strings into bootstrap");
    Check(root.GetProperty("billingReadStatus").GetString() == "ready" && root.GetProperty("mode").GetString() == "connected", "connected bootstrap distinguishes financial read state");
    Check(root.GetProperty("journeyStatus").GetString() == "notConnected" && root.GetProperty("requests").GetArrayLength() == 0
        && root.GetProperty("consumerUsage").GetProperty("status").GetString() == "ready",
        "ready financial report does not turn disconnected request history into an empty successful history");
    Check(!page.BootstrapJson.Contains(subject) && !root.TryGetProperty("subject", out _), "bootstrap omits raw subject and credential");
    Check(page.Response.Headers.CacheControl == "no-store", "financial page response is no-store");
}
var failedHistory = Model(new FailedJourney()); await failedHistory.OnGetAsync(org.Id, default);
using (var json = JsonDocument.Parse(failedHistory.BootstrapJson))
{
    var root = json.RootElement;
    Check(root.GetProperty("journeyStatus").GetString() == "unavailable" && root.GetProperty("catalog").GetArrayLength() == 0
        && root.GetProperty("requests").GetArrayLength() == 0 && root.GetProperty("publications").GetArrayLength() == 0,
        "history failure retains unknown state and discards stale history independently of billing");
    Check(root.GetProperty("consumerAccount").GetProperty("status").GetString() == "ready"
        && root.GetProperty("billingReadStatus").GetString() == "ready", "failed journey transport does not erase independently authorized billing");
}
var calls = reader.Calls; await page.OnGetAsync(Guid.NewGuid(), default);
using (var json = JsonDocument.Parse(page.BootstrapJson))
    Check(reader.Calls == calls && json.RootElement.GetProperty("consumerUsage").ValueKind == JsonValueKind.Null
        && json.RootElement.GetProperty("consumerAccount").ValueKind == JsonValueKind.Null,
        "organization switch to unauthorized consumer clears both old reports before reading");
var anonymous = Model(signedIn: false); await anonymous.OnGetAsync(org.Id, default);
Check(reader.Calls == calls && !anonymous.BootstrapJson.Contains("9007199254740993"), "anonymous page never invokes billing reader");
reader.Value = new("ready", usage with { OrganizationId = Guid.NewGuid() }, account);
var foreign = Model(); await foreign.OnGetAsync(org.Id, default);
using (var json = JsonDocument.Parse(foreign.BootstrapJson))
    Check(json.RootElement.GetProperty("billingReadStatus").GetString() == "unavailable"
        && json.RootElement.GetProperty("consumerUsage").ValueKind == JsonValueKind.Null
        && json.RootElement.GetProperty("consumerAccount").ValueKind == JsonValueKind.Null, "foreign usage drops both financial views");
reader.Value = new("ready", usage, account with { OrganizationId = Guid.NewGuid() });
var foreignAccount = Model(); await foreignAccount.OnGetAsync(org.Id, default);
Check(!foreignAccount.BootstrapJson.Contains("9007199254740993"), "foreign account cannot leak an otherwise valid usage report");
calls = reader.Calls; var preview = Model(new PreviewJourney()); await preview.OnGetAsync(org.Id, default);
Check(reader.Calls == calls && preview.IsPreview, "synthetic preview never invokes real financial reader");
reader.Value = new("notConfigured", null, null); var disabled = Model(); await disabled.OnGetAsync(org.Id, default);
using (var json = JsonDocument.Parse(disabled.BootstrapJson))
    Check(json.RootElement.GetProperty("billingReadStatus").GetString() == "notConfigured"
        && json.RootElement.GetProperty("consumerAccount").ValueKind == JsonValueKind.Null,
        "missing approved configuration remains explicit unknown financial data");
Console.WriteLine($"PASS {checks} PageModel checks; no network, real sessions or financial writes.");

sealed class BillingStub(WorkspaceConsumerBilling value) : IWorkspaceConsumerBillingReader
{
    public WorkspaceConsumerBilling Value { get; set; } = value;
    public int Calls { get; private set; } public string? Subject { get; private set; } public Guid Consumer { get; private set; }
    public Task<WorkspaceConsumerBilling> ReadAsync(string subject, Guid consumer, CancellationToken cancellationToken)
    { Calls++; Subject = subject; Consumer = consumer; return Task.FromResult(Value); }
}
sealed class CatalogStub(PlatformWorkspace workspace) : IPlatformCatalogClient
{
    public Task<PlatformWorkspace> GetWorkspaceAsync(string subject, CancellationToken cancellationToken, Guid? organizationId = null) => Task.FromResult(workspace);
}
sealed class PreviewJourney : IWorkspaceJourneySource
{
    public bool IsSyntheticPreview => true;
    public Task<WorkspaceJourneyData> GetAsync(string subject, Guid organizationId, CancellationToken cancellationToken)
        => Task.FromResult(new WorkspaceJourneyData([], [], [], WorkspaceUsage.NotConnected, organizationId, subject));
}
sealed class FailedJourney : IWorkspaceJourneySource
{
    public bool IsSyntheticPreview => false;
    public Task<WorkspaceJourneyData> GetAsync(string subject, Guid organizationId, CancellationToken cancellationToken)
        => throw new HttpRequestException("Synthetic history failure");
}
