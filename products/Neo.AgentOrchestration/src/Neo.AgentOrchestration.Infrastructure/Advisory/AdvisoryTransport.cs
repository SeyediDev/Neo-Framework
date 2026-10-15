using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Neo.AgentOrchestration.Application.Delivery;
using Neo.AgentOrchestration.Domain.Advisory;

namespace Neo.AgentOrchestration.Infrastructure.Advisory;

// Worker only. Persisted intent precedes HTTP; no provider idempotency is assumed.
public sealed class AdvisoryTransport(HttpClient http, SqlAdvisoryRequests store, IConfiguration config, TimeProvider clock)
{
    public async Task SendAsync(WorkDeliveryExecution operation, CancellationToken ct)
    {
        var row = await store.BeginAsync(operation, ct);
        if (row is null) return;
        AdvisoryDraft? draft = null;
        var reason = "submission-uncertain";
        var rejected = false;
        try
        {
            var endpoint = Endpoint(config);
            // systemd LoadCredential path: token is not stored in API/Web config or delivery payloads.
            var path = config["AgnoAdvisory:TokenFile"] ?? throw new InvalidOperationException();
            var secret = (await File.ReadAllTextAsync(path, ct)).Trim();
            if (secret.Length is < 16 or > 8192 || secret.Any(char.IsControl)) throw new InvalidOperationException();
            var remaining = row.DeadlineUtc - clock.GetUtcNow();
            if (remaining <= TimeSpan.Zero) throw new InvalidOperationException();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(remaining);
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            request.Content = new FormUrlEncodedContent(new Dictionary<string, string> {
                ["message"] = row.Brief, ["stream"] = "false", ["background"] = "false",
                ["session_id"] = "advisory-" + row.Id.ToString("N") });
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            // These statuses are issued before model execution by the private runtime/auth middleware.
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or
                HttpStatusCode.RequestEntityTooLarge or HttpStatusCode.TooManyRequests)
            { rejected = true; reason = "runtime-rejected-before-execution"; }
            else if (response.StatusCode == HttpStatusCode.OK)
            {
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                using var bytes = new MemoryStream();
                var buffer = new byte[4096]; int read;
                while ((read = await stream.ReadAsync(buffer, timeout.Token)) != 0)
                {
                    if (bytes.Length + read > 65536) throw new InvalidOperationException();
                    bytes.Write(buffer, 0, read);
                }
                draft = Parse(bytes.ToArray(), "advisory-" + row.Id.ToString("N"));
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        { /* Never persist provider bodies, credentials, endpoint errors or raw exceptions. */ }
        // Cancellation must not erase the outcome of an already submitted POST.
        using var recording = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await store.FinishAsync(operation.Scope, row.Id, draft, reason, rejected, recording.Token);
    }

    public static Uri Endpoint(IConfiguration config)
    {
        if (!Uri.TryCreate(config["AgnoAdvisory:BaseUrl"], UriKind.Absolute, out var uri) ||
            uri.Scheme != "http" || !uri.IsLoopback || uri.AbsolutePath != "/" ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            throw new InvalidOperationException("A private loopback Agno base URL is required.");
        return new Uri(uri, "agents/task-planner/runs");
    }

    public static AdvisoryDraft Parse(byte[] bytes, string sessionId)
    {
        using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 24 });
        var root = doc.RootElement;
        var status = root.GetProperty("status").GetString();
        if (status is not ("COMPLETED" or "completed" or "RunStatus.completed") ||
            root.GetProperty("session_id").GetString() != sessionId ||
            root.GetProperty("content").GetString() is not { Length: > 0 and <= 12000 } text ||
            root.GetProperty("run_id").GetString() is not { Length: > 0 and <= 200 } runId)
            throw new InvalidOperationException("Advisory result requires reconciliation.");
        string? model = null;
        if (root.TryGetProperty("model", out var m) && m.ValueKind == JsonValueKind.String) model = m.GetString();
        long? input = null, output = null;
        if (root.TryGetProperty("metrics", out var metrics) && metrics.ValueKind == JsonValueKind.Object)
        {
            input = Counter(metrics, "input_tokens"); output = Counter(metrics, "output_tokens");
        }
        if (model?.Length > 200 || runId.Any(char.IsControl)) throw new InvalidOperationException();
        return new(text, model, input, output, runId);
    }
    private static long? Counter(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var field) || field.ValueKind == JsonValueKind.Null) return null;
        if (!field.TryGetInt64(out var count) || count < 0) throw new InvalidOperationException("Invalid provider counter.");
        return count;
    }
}

public static class AdvisoryWorkerRegistration
{
    public static IServiceCollection AddAgnoAdvisoryWorker(this IServiceCollection services)
    {
        services.AddHttpClient<AdvisoryTransport>(http => http.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false });
        return services;
    }
}
