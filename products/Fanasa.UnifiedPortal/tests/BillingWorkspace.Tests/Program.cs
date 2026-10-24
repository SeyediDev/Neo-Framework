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
WorkspaceModel Model(IWorkspaceJourneySource? journey = null, bool signedIn = true,
    IPlatformCatalogClient? workspaceCatalog = null, IWorkspaceConsumerBillingReader? financial = null, TimeProvider? clock = null)
{
    var model = new WorkspaceModel(workspaceCatalog ?? catalog, journey ?? new NotConnectedWorkspaceJourneySource(), config,
        billing: financial ?? reader, timeProvider: clock)
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

// Fake monotonic time covers the complete catalog-to-billing sequence without
// sleeping for real request deadlines. The reader deliberately ignores its token.
var clock = new ManualClock(now);
var pendingBilling = new PendingBilling();
var bounded = Model(workspaceCatalog: new TimedCatalog(clock, TimeSpan.FromSeconds(45), new(CatalogStatus.Ready, [org], [], org.Id)),
    financial: pendingBilling, clock: clock);
var pendingPage = bounded.OnGetAsync(org.Id, default);
Check(pendingBilling.Calls == 1 && !pendingBilling.Cancellation.IsCancellationRequested && !pendingPage.IsCompleted,
    "authorized catalog completes before financial work starts with the remaining request budget");
clock.UtcShift = TimeSpan.FromDays(3650);
clock.Advance(TimeSpan.FromSeconds(2));
Check(!pendingPage.IsCompleted, "the remaining financial wait uses monotonic time even when UTC jumps forward");
clock.UtcShift = -TimeSpan.FromDays(3650);
clock.Advance(TimeSpan.FromSeconds(1));
await pendingPage.WaitAsync(TimeSpan.FromSeconds(5));
using (var json = JsonDocument.Parse(bounded.BootstrapJson))
{
    var root = json.RootElement;
    Check(root.GetProperty("catalogStatus").GetString() == "ready" && root.GetProperty("organizationId").GetGuid() == org.Id,
        "45-second catalog plus noncooperative financial reader still returns the authorized ready catalog at 48 seconds");
    Check(root.GetProperty("billingReadStatus").GetString() == "unavailable"
        && root.GetProperty("consumerUsage").ValueKind == JsonValueKind.Null && root.GetProperty("consumerAccount").ValueKind == JsonValueKind.Null,
        "financial deadline returns unknown data rather than erasing the catalog or returning stale money");
}
Check(pendingBilling.Cancellation.IsCancellationRequested, "shared deadline cancels the downstream financial operation");
var finishedSnapshot = bounded.BootstrapJson;
pendingBilling.Completion.SetResult(new("ready", usage, account));
await Task.Yield();
Check(bounded.BootstrapJson == finishedSnapshot, "late financial completion cannot modify the returned bootstrap");

var exhaustedClock = new ManualClock(now);
var skippedBilling = new BillingStub(new("ready", usage, account));
var exhausted = Model(workspaceCatalog: new TimedCatalog(exhaustedClock, TimeSpan.FromSeconds(48), new(CatalogStatus.Ready, [org], [], org.Id)),
    financial: skippedBilling, clock: exhaustedClock);
await exhausted.OnGetAsync(org.Id, default);
using (var json = JsonDocument.Parse(exhausted.BootstrapJson))
    Check(skippedBilling.Calls == 0 && json.RootElement.GetProperty("catalogStatus").GetString() == "ready"
        && json.RootElement.GetProperty("billingReadStatus").GetString() == "unavailable",
        "an exhausted catalog budget skips financial work while preserving the ready catalog");

var abortClock = new ManualClock(now);
var abortedBilling = new PendingBilling();
var abortedPage = Model(workspaceCatalog: new TimedCatalog(abortClock, TimeSpan.FromSeconds(45), new(CatalogStatus.Ready, [org], [], org.Id)),
    financial: abortedBilling, clock: abortClock);
using (var caller = new CancellationTokenSource())
{
    var aborted = abortedPage.OnGetAsync(org.Id, caller.Token);
    caller.Cancel();
    var propagated = false;
    try { await aborted.WaitAsync(TimeSpan.FromSeconds(5)); }
    catch (OperationCanceledException error) { propagated = error.CancellationToken == caller.Token; }
    Check(propagated && abortedBilling.Cancellation.IsCancellationRequested,
        "caller cancellation propagates with its token and cancels a noncooperative financial reader");
    abortedBilling.Completion.SetResult(new("ready", usage, account));
    await Task.Yield();
    Check(abortedPage.BootstrapJson == "{}", "an aborted request never publishes a late financial bootstrap");
}

var fastClock = new ManualClock(now);
var fastBilling = new BillingStub(new("ready", usage, account));
var fast = Model(workspaceCatalog: new TimedCatalog(fastClock, TimeSpan.FromSeconds(45), new(CatalogStatus.Ready, [org], [], org.Id)),
    financial: fastBilling, clock: fastClock);
await fast.OnGetAsync(org.Id, default);
using (var json = JsonDocument.Parse(fast.BootstrapJson))
    Check(fastBilling.Calls == 1 && json.RootElement.GetProperty("billingReadStatus").GetString() == "ready"
        && json.RootElement.GetProperty("consumerUsage").GetProperty("totalAmount").GetString() == "9007199254740993.10",
        "fast authorized financial data survives a slow catalog within the remaining budget");
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
sealed class TimedCatalog(ManualClock clock, TimeSpan duration, PlatformWorkspace workspace) : IPlatformCatalogClient
{
    public Task<PlatformWorkspace> GetWorkspaceAsync(string subject, CancellationToken cancellationToken, Guid? organizationId = null)
    { clock.Advance(duration); return Task.FromResult(workspace); }
}
sealed class PendingBilling : IWorkspaceConsumerBillingReader
{
    public TaskCompletionSource<WorkspaceConsumerBilling> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int Calls { get; private set; }
    public CancellationToken Cancellation { get; private set; }
    public Task<WorkspaceConsumerBilling> ReadAsync(string subject, Guid consumer, CancellationToken cancellationToken)
    { Calls++; Cancellation = cancellationToken; return Completion.Task; }
}
sealed class ManualClock(DateTimeOffset now) : TimeProvider
{
    private long timestamp;
    private readonly List<ManualTimer> timers = [];
    public TimeSpan UtcShift { get; set; }
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    public override long GetTimestamp() => timestamp;
    public override DateTimeOffset GetUtcNow() => now.AddTicks(timestamp).Add(UtcShift);
    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }
    public void Advance(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(elapsed));
        timestamp += elapsed.Ticks;
        foreach (var timer in timers.ToArray()) timer.Fire(timestamp);
    }
    private sealed class ManualTimer(ManualClock clock, TimerCallback callback, object? state) : ITimer
    {
        private long? next;
        private TimeSpan period;
        private bool disposed;
        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            if (disposed) return false;
            this.period = period;
            next = dueTime == Timeout.InfiniteTimeSpan ? null : clock.GetTimestamp() + dueTime.Ticks;
            return true;
        }
        public void Fire(long current)
        {
            if (disposed || next is not long due || due > current) return;
            next = period > TimeSpan.Zero ? current + period.Ticks : null;
            callback(state);
        }
        public void Dispose() { disposed = true; next = null; }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
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
