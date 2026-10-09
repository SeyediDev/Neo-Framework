namespace Neo.AgentOrchestration.Infrastructure.ExternalAgents;

// ResponseHeadersRead ends HttpClient's timeout at the headers. The caller must
// use this token for send, stream acquisition and every read without resetting
// the budget on chunks. A longer/infinite client timeout cannot remove the cap.
internal static class HttpRequestDeadline
{
    public static CancellationTokenSource Start(HttpClient http, CancellationToken caller)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(caller);
        var budget = TimeSpan.FromSeconds(30);
        if (http.Timeout != Timeout.InfiniteTimeSpan && http.Timeout < budget) budget = http.Timeout;
        deadline.CancelAfter(budget);
        return deadline;
    }
}
