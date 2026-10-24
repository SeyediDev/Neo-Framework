using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Governance;
using Fanasa.AccessManagement.Web.Persistence;
using Fanasa.AccessManagement.Web.Platform;
using Fanasa.ContractPolicy;
using Fanasa.HrReadCandidate;
using Fanasa.HrReadCandidate.Http;
using Fanasa.HrReadPdp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Fanasa.HrReadHost;

public interface IHr3HostPdpServiceFactory
{
    HrReadPdpService Create(IHr3RetainedAuthenticationSource authentication);
}

/// <summary>Uses only the existing host services. Authentication is retained per request, never in a singleton source.</summary>
public sealed class ExistingHostHr3PdpServiceFactory(FabricDatabase database, PlatformRegistry registry,
    PersistentAccessManagement access, AccessPolicyStore legacyPolicies, ContractPolicyModule module,
    TimeProvider time) : IHr3HostPdpServiceFactory
{
    public HrReadPdpService Create(IHr3RetainedAuthenticationSource authentication)
    {
        ArgumentNullException.ThrowIfNull(authentication);
        var records = new Hr3CentralHostRecords(database, registry, access, legacyPolicies, authentication, time);
        var source = new ExistingFabricHrReadPdpSourceFactory(database, module.Projections, records, time);
        return new(module, source, time);
    }
}

/// <summary>
/// Actual-host PDP boundary: native TLS and two named JWT handlers, async authenticated introspection,
/// then query-only Fabric snapshots. Register this route OR MapHrReadPdp, never both.
/// This does not register/replace the existing HR chart/history or command routes.
/// </summary>
public static class Hr3HostPdpEndpoint
{
    public static IEndpointConventionBuilder MapHr3HostPdp(this IEndpointRouteBuilder endpoints,
        HrReadPdpCallerSettings gateway, HrReadReceiverSettings subject)
    {
        var gatewaySnapshot = gateway with { }; var subjectSnapshot = subject with { };
        return endpoints.MapPost(HrReadPdpWire.EndpointPath, async (HttpContext http) =>
        {
            _ = await ExecuteAsync(http, gatewaySnapshot, subjectSnapshot,
                http.RequestServices.GetService<IHr3AuthenticationPreparer>(),
                http.RequestServices.GetService<IHr3HostPdpServiceFactory>(),
                http.RequestServices.GetService<IHrReadNativeCertificateValidator>(),
                http.RequestServices.GetService<TimeProvider>(), http.RequestAborted).ConfigureAwait(false);
        }).AllowAnonymous(); // Authentication happens exclusively through the two explicit named schemes below.
    }

    public static async ValueTask<bool> ExecuteAsync(HttpContext http, HrReadPdpCallerSettings gateway,
        HrReadReceiverSettings subject, IHr3AuthenticationPreparer? preparation = null,
        IHr3HostPdpServiceFactory? services = null, IHrReadNativeCertificateValidator? certificates = null,
        TimeProvider? time = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(http); ArgumentNullException.ThrowIfNull(gateway); ArgumentNullException.ThrowIfNull(subject);
        time ??= TimeProvider.System;
        if (http.Response.HasStarted) throw new InvalidOperationException("HR3 PDP response has already started.");
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5), time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, http.RequestAborted, budget.Token);
        var writer = new HttpWriter(http, time, linked.Token);
        try
        {
            return await ExecuteWithinBudgetAsync(http, gateway, subject, preparation, services,
                certificates, time, writer, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested && !http.RequestAborted.IsCancellationRequested)
        {
            if (http.Response.HasStarted || writer.SendAttempted) throw;
            return Deny(http, 403);
        }
    }

    private static async ValueTask<bool> ExecuteWithinBudgetAsync(HttpContext http, HrReadPdpCallerSettings gateway,
        HrReadReceiverSettings subject, IHr3AuthenticationPreparer? preparation, IHr3HostPdpServiceFactory? services,
        IHrReadNativeCertificateValidator? certificates, TimeProvider time, HttpWriter writer, CancellationToken ct)
    {
        http.Response.Headers.CacheControl = "no-store";
        ct.ThrowIfCancellationRequested();
        if (!HrReadPdpAuthentication.IsEndpoint(http)
            || !http.Request.Headers.TryGetValue("Content-Type", out var type) || type.Count != 1
            || !string.Equals(type[0], "application/json", StringComparison.OrdinalIgnoreCase)
            || http.Request.Headers.ContainsKey("Content-Encoding") || http.Request.Headers.ContainsKey("Transfer-Encoding")
            || !HrReadPdpAuthentication.Compatible(gateway, subject)) return Deny(http, 403);
        if (http.Request.ContentLength is > HrReadPdpWire.MaximumRequestBytes) return Deny(http, 413);
        var buffer = new byte[HrReadPdpWire.MaximumRequestBytes + 1]; var count = 0;
        while (count < buffer.Length)
        {
            var read = await http.Request.Body.ReadAsync(buffer.AsMemory(count), ct).AsTask().WaitAsync(ct).ConfigureAwait(false);
            if (read == 0) break;
            count += read;
        }
        ct.ThrowIfCancellationRequested();
        if (count > HrReadPdpWire.MaximumRequestBytes) return Deny(http, 413);
        if (http.Request.ContentLength is { } length && length != count) return Deny(http, 400);
        var request = HrReadPdpWire.Parse(buffer.AsMemory(0, count), subject, time.GetUtcNow());
        if (request is null) return Deny(http, 400);
        var caller = await HrReadPdpAuthentication.AuthenticateAsync(http, gateway, subject, request,
            certificates, time, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (caller is null || preparation is null || services is null) return Deny(http, 403);
        // HTTP work completes before any Fabric snapshot. A missing mapping/credential/revision denies.
        var authentication = await preparation.PrepareAsync(request, caller, ct).AsTask().WaitAsync(ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        if (authentication is null || !caller.RevalidateCurrent()) return Deny(http, 403);
        var service = services.Create(authentication);
        var sent = await service.ExecuteAsync(request, caller, writer, ct).AsTask().WaitAsync(ct).ConfigureAwait(false);
        if (!sent && writer.SendAttempted) throw new InvalidOperationException("HR3 cannot deny after attempting a response write.");
        return sent || Deny(http, 403);
    }

    private static bool Deny(HttpContext http, int status)
    {
        if (http.Response.HasStarted) throw new InvalidOperationException("HR3 cannot replace a started response.");
        http.Response.StatusCode = status; http.Response.ContentLength = 0; return false;
    }

    private sealed class HttpWriter(HttpContext http, TimeProvider time, CancellationToken executionCancellation) : IHrReadPdpResponseWriter
    {
        private int attempted;
        public bool SendAttempted => Volatile.Read(ref attempted) != 0;
        public async ValueTask SendAsync(byte[] bytes, DateTimeOffset validUntil, CancellationToken ct)
        {
            Interlocked.Exchange(ref attempted, 1);
            executionCancellation.ThrowIfCancellationRequested(); ct.ThrowIfCancellationRequested();
            if (time.GetUtcNow() >= validUntil) throw new InvalidOperationException("HR3 response expired before send.");
            http.Response.StatusCode = 200; http.Response.ContentType = "application/json";
            http.Response.ContentLength = bytes.Length; http.Response.Headers.CacheControl = "no-store";
            await http.Response.Body.WriteAsync(bytes, ct).AsTask().WaitAsync(ct).ConfigureAwait(false);
        }
    }
}
