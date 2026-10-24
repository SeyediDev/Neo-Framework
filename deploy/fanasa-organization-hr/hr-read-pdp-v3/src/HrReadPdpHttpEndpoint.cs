using Fanasa.HrReadCandidate;
using Fanasa.HrReadCandidate.Http;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Fanasa.HrReadPdp;

/// <summary>Explicit route registration only. Existing command PDP and HR API routes are never changed.</summary>
public static class HrReadPdpHttpEndpoint
{
    public static IEndpointConventionBuilder MapHrReadPdp(this IEndpointRouteBuilder endpoints,
        HrReadPdpCallerSettings callerSettings, HrReadReceiverSettings binding)
    {
        ArgumentNullException.ThrowIfNull(endpoints); ArgumentNullException.ThrowIfNull(callerSettings); ArgumentNullException.ThrowIfNull(binding);
        var callerSnapshot = callerSettings with { }; var bindingSnapshot = binding with { };
        return endpoints.MapPost(HrReadPdpWire.EndpointPath, async (HttpContext http) =>
        {
            _ = await ExecuteAsync(http, callerSnapshot, bindingSnapshot,
                http.RequestServices.GetService<HrReadPdpService>(), http.RequestServices.GetService<IHrReadNativeCertificateValidator>(),
                http.RequestServices.GetService<TimeProvider>(), http.RequestAborted).ConfigureAwait(false);
        });
    }

    public static async ValueTask<bool> ExecuteAsync(HttpContext http, HrReadPdpCallerSettings callerSettings,
        HrReadReceiverSettings binding, HrReadPdpService? service = null, IHrReadNativeCertificateValidator? certificates = null,
        TimeProvider? time = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http); ArgumentNullException.ThrowIfNull(callerSettings); ArgumentNullException.ThrowIfNull(binding);
        time ??= TimeProvider.System;
        if (http.Response.HasStarted) throw new InvalidOperationException("HR PDP response has already started.");
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5), time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, http.RequestAborted, budget.Token);
        var writer = new HttpWriter(http, time, linked.Token);
        try { return await ExecuteWithinBudgetAsync(http, callerSettings, binding, service, certificates, time, writer, linked.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (budget.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested && !http.RequestAborted.IsCancellationRequested)
        {
            // A bounded timeout can become an empty denial only while no response has begun.
            if (http.Response.HasStarted || writer.SendAttempted) throw;
            return Deny(http, 403);
        }
    }

    private static async ValueTask<bool> ExecuteWithinBudgetAsync(HttpContext http, HrReadPdpCallerSettings callerSettings,
        HrReadReceiverSettings binding, HrReadPdpService? service, IHrReadNativeCertificateValidator? certificates,
        TimeProvider time, HttpWriter writer, CancellationToken cancellationToken)
    {
        http.Response.Headers.CacheControl = "no-store";
        cancellationToken.ThrowIfCancellationRequested();
        if (!HrReadPdpAuthentication.IsEndpoint(http) || !JsonContentType(http)
            || http.Request.Headers.ContainsKey("Content-Encoding") || http.Request.Headers.ContainsKey("Transfer-Encoding")
            || !HrReadPdpAuthentication.Compatible(callerSettings, binding)) return Deny(http, 403);
        if (http.Request.ContentLength is > HrReadPdpWire.MaximumRequestBytes) return Deny(http, 413);
        var buffer = new byte[HrReadPdpWire.MaximumRequestBytes + 1];
        var count = 0;
        while (count < buffer.Length)
        {
            var read = await http.Request.Body.ReadAsync(buffer.AsMemory(count), cancellationToken)
                .AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            count += read;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (count > HrReadPdpWire.MaximumRequestBytes) return Deny(http, 413);
        if (http.Request.ContentLength is { } length && length != count) return Deny(http, 400);
        var request = HrReadPdpWire.Parse(buffer.AsMemory(0, count), binding, time.GetUtcNow());
        if (request is null) return Deny(http, 400);
        var proof = await HrReadPdpAuthentication.AuthenticateAsync(http, callerSettings, binding, request,
            certificates, time, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (proof is null || service is null) return Deny(http, 403);
        var sent = await service.ExecuteAsync(request, proof, writer, cancellationToken)
            .AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
        if (!sent && writer.SendAttempted) throw new InvalidOperationException("HR PDP cannot deny after attempting a response write.");
        return sent || Deny(http, 403);
    }

    private static bool JsonContentType(HttpContext http) => http.Request.Headers.TryGetValue("Content-Type", out var values)
        && values.Count == 1 && string.Equals(values[0], "application/json", StringComparison.OrdinalIgnoreCase);
    private static bool Deny(HttpContext http, int status)
    {
        if (http.Response.HasStarted) throw new InvalidOperationException("HR PDP cannot replace an already started response.");
        http.Response.StatusCode = status; http.Response.ContentLength = 0; return false;
    }
    private sealed class HttpWriter(HttpContext http, TimeProvider time, CancellationToken executionCancellation) : IHrReadPdpResponseWriter
    {
        private int attempted;
        public bool SendAttempted => Volatile.Read(ref attempted) != 0;
        public async ValueTask SendAsync(byte[] bytes, DateTimeOffset validUntil, CancellationToken cancellationToken)
        {
            Interlocked.Exchange(ref attempted, 1);
            // Also check the endpoint token directly: cancellation callbacks to a service's child token may still be running.
            executionCancellation.ThrowIfCancellationRequested();
            cancellationToken.ThrowIfCancellationRequested();
            if (time.GetUtcNow() >= validUntil) throw new InvalidOperationException("HR PDP response expired before send.");
            http.Response.StatusCode = 200; http.Response.ContentType = "application/json";
            http.Response.ContentLength = bytes.Length; http.Response.Headers.CacheControl = "no-store";
            // A send failure propagates; a partially transmitted response is never replaced with a success or denial.
            await http.Response.Body.WriteAsync(bytes, cancellationToken).AsTask().WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
