using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Neo.AgentOrchestration.Application.Delivery;
using Neo.AgentOrchestration.Application.Runs;
using Neo.AgentOrchestration.Application.Work;
using Neo.AgentOrchestration.Domain.Runs;

namespace Neo.AgentOrchestration.Infrastructure.Runs;

// Called only AFTER the Neo execution lease is acquired and BEFORE the final
// SQL transaction begins. Neither HTTP nor secrets enter the work session.
public sealed class HttpHarnessTransport(HttpClient http, IWorkspaceWorkStore store,
    ConfiguredRunProviders providers, IHarnessSecrets secrets)
{
    public async Task<string?> SendAsync(WorkDeliveryExecution operation, CancellationToken ct)
    {
        var snapshot = await store.ExecuteAsync(operation.Scope, async (session, token) =>
        {
            var s = session as IRunSession ?? throw new InvalidOperationException("Run session required.");
            var run = await s.GetRunAsync(operation.AgentRunId ?? Guid.Empty, token) ?? throw new KeyNotFoundException();
            if (run.WorkItemId != operation.WorkItemId || run.ProjectId != operation.ProjectId ||
                !run.AllowExternalExecution || run.HarnessPayload is null || run.Provider == "fake")
                throw new InvalidOperationException("Invalid harness snapshot.");
            if (run.Status is AgentRunStatus.Succeeded or AgentRunStatus.Failed or AgentRunStatus.NeedsInput) return null;
            if (run.Status != AgentRunStatus.AwaitingResult || await s.GetProjectAsync(run.ProjectId, token) is not { IsEnabled: true } ||
                await s.GetRoleAsync(run.RoleId, token) is not { IsEnabled: true } ||
                !(await s.GetAgentsAsync(token)).Any(x => x.Id == run.AgentProfileId && x.IsEnabled))
                throw new InvalidOperationException("Run configuration is unavailable.");
            return new Snapshot(run.Id, run.Provider, run.HarnessBinding!, run.HarnessPayload);
        }, ct);
        if (snapshot is null) return null; // Callback already committed: do not send again.
        try
        {
            var connection = providers.Resolve(snapshot.Provider, operation.Scope);
            if (snapshot.Binding != connection.Fingerprint) return "harness-configuration-changed-reconcile-before-replay";
            using var request = new HttpRequestMessage(HttpMethod.Post, connection.Endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secrets.Resolve(connection.DispatchSecretRef));
            request.Headers.Add("Idempotency-Key", snapshot.RunId.ToString("N"));
            request.Content = new StringContent(snapshot.Payload, Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (response.StatusCode == HttpStatusCode.Accepted) return null;
            if ((int)response.StatusCode >= 500 || response.StatusCode is HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests)
                throw new InvalidOperationException("Harness transport temporarily unavailable.");
            // No redirect, raw provider body, or automatic replay of a permanent/
            // ambiguous rejection. Do not pretend the external operation failed.
            return "harness-response-requires-reconciliation";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException && !(ex is OperationCanceledException && ct.IsCancellationRequested))
        { throw new InvalidOperationException("Harness transport unavailable; reconcile by run identifier before replay."); }
    }
    private sealed record Snapshot(Guid RunId, string Provider, string Binding, string Payload);
}
