namespace Fanasa.UnifiedPortal.Web;

public sealed record WorkspaceConsumerBilling(string Status, ConsumerUsageView? Usage, ConsumerAccountView? Account);
public interface IWorkspaceConsumerBillingReader
{
    Task<WorkspaceConsumerBilling> ReadAsync(string authenticatedSubject, Guid consumer, CancellationToken cancellationToken);
}

public sealed class GatewayWorkspaceConsumerBillingReader : IWorkspaceConsumerBillingReader
{
    private readonly ConsumerBillingOptions options;
    private readonly IConsumerUsageHumanCredentialProvider credentials;
    private readonly TimeProvider clock;
    private readonly Func<HttpMessageHandler> handlers;
    public GatewayWorkspaceConsumerBillingReader(ConsumerBillingOptions options, IConsumerUsageHumanCredentialProvider credentials, TimeProvider clock)
        : this(options, credentials, clock, ConsumerUsageReportClient.CreateHandler) { }
    internal GatewayWorkspaceConsumerBillingReader(ConsumerBillingOptions options, IConsumerUsageHumanCredentialProvider credentials,
        TimeProvider clock, Func<HttpMessageHandler> handlers)
    { this.options = options; this.credentials = credentials; this.clock = clock; this.handlers = handlers; }

    public async Task<WorkspaceConsumerBilling> ReadAsync(string authenticatedSubject, Guid consumer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!options.IsConfigured) return new("notConfigured", null, null);
        var bound = new SubjectCredentialProvider(credentials, authenticatedSubject);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var credential = await bound.GetAsync(deadline.Token);
            if (credential is null || credential.ExpiresAt <= clock.GetUtcNow()) return new("signInRequired", null, null);
            var now = clock.GetUtcNow();
            var from = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
            var to = now > from ? now : from.AddTicks(1);
            using var usage = new ConsumerUsageReportClient(options.GatewayOrigin!, bound, handlers(), clock: clock);
            var report = await usage.ReadAsync(consumer, from, to, deadline.Token);
            var accountCredential = await bound.GetAsync(deadline.Token);
            if (accountCredential is null || accountCredential.BearerToken != credential.BearerToken
                || accountCredential.ExpiresAt <= clock.GetUtcNow()) return new("signInRequired", null, null);
            using var http = new HttpClient(handlers()) { Timeout = Timeout.InfiniteTimeSpan };
            var target = new Uri(options.GatewayOrigin!, "/api/accounting/v1/consumers/" + consumer.ToString("D") + "/account");
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            request.Headers.Authorization = new("Bearer", accountCredential.BearerToken);
            request.Headers.Accept.Add(new("application/json"));
            request.Headers.CacheControl = new() { NoStore = true, NoCache = true };
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            var account = await ConsumerAccountHttpResponse.ReadAsync(response, target, authenticatedSubject, consumer, deadline.Token);
            var current = await bound.GetAsync(deadline.Token);
            if (current is null || current.BearerToken != credential.BearerToken || current.ExpiresAt <= clock.GetUtcNow())
                return new("signInRequired", null, null);
            if (report.Status == ConsumerUsageReadStatus.Unauthorized || account.Status == ConsumerUsageReadStatus.Unauthorized)
                return new("signInRequired", null, null);
            if (report.Status == ConsumerUsageReadStatus.Forbidden || account.Status == ConsumerUsageReadStatus.Forbidden)
                return new("forbidden", null, null);
            return new(report.Status == ConsumerUsageReadStatus.Success && account.Status == ConsumerUsageReadStatus.Success ? "ready" : "unavailable",
                report.View, account.View);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
        { return new("unavailable", null, null); }
    }
    private sealed class SubjectCredentialProvider(IConsumerUsageHumanCredentialProvider inner, string subject) : IConsumerUsageHumanCredentialProvider
    {
        public async ValueTask<ConsumerUsageHumanCredential?> GetAsync(CancellationToken cancellationToken)
        {
            var credential = await inner.GetAsync(cancellationToken);
            return credential?.Subject == subject ? credential : null;
        }
    }
}
