using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;

namespace Fanasa.HrReadCandidate.Http;

/// <summary>Explicit opt-in mapping only. This package never changes an existing host or command verifier.</summary>
public static class HrReadHttpEndpoint
{
    public static IEndpointRouteBuilder MapHrReadCandidate(this IEndpointRouteBuilder endpoints,
        HrReadReceiverSettings settings, IHrReadFabricAdapter? fabric = null, IHrReadTrustedKeySource? keys = null,
        IHrReadHttpAuthoritySource? authority = null, IHrReadNativeCertificateValidator? certificates = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints); ArgumentNullException.ThrowIfNull(settings);
        async Task Handle(HttpContext context) => await ExecuteAsync(context, settings, fabric, keys, authority, certificates, time).ConfigureAwait(false);
        endpoints.MapGet("/api/organization-hr/v1/tenants/{tenantId}/chart", Handle);
        endpoints.MapGet("/api/organization-hr/v1/tenants/{tenantId}/history/{revision}", Handle);
        return endpoints;
    }

    public static async Task ExecuteAsync(HttpContext context, HrReadReceiverSettings settings,
        IHrReadFabricAdapter? fabric = null, IHrReadTrustedKeySource? keys = null,
        IHrReadHttpAuthoritySource? authority = null, IHrReadNativeCertificateValidator? certificates = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(context); ArgumentNullException.ThrowIfNull(settings);
        if (context.Response.HasStarted) throw new InvalidOperationException("The HR receiver requires an unstarted response.");
        context.Response.Headers.CacheControl = "no-store";
        var writer = new ResponseWriter(context);
        try
        {
            if (context.Features.Get<IHttpRequestFeature>()?.RawTarget is not string rawTarget)
            { Deny(context); return; }
            var receiver = new HrReadHttpAuthority(context, settings, keys, authority, certificates, time);
            var coordinator = new HrReadCoordinator(fabric ?? new UnavailableFabric(), receiver, time);
            var outcome = await coordinator.ExecuteAsync(context.Request.Method, rawTarget, context.Request.Body,
                writer, context.RequestAborted).ConfigureAwait(false);
            if (outcome == HrReadOutcome.Denied) Deny(context);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { throw; }
        catch (Exception) when (!writer.SendAttempted && !context.Response.HasStarted) { Deny(context); }
    }

    private static void Deny(HttpContext context)
    {
        if (context.Response.HasStarted) throw new InvalidOperationException("A started response cannot become an HR denial.");
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        context.Response.ContentType = null; context.Response.ContentLength = 0;
        context.Response.Headers.CacheControl = "no-store";
    }
    private sealed class UnavailableFabric : IHrReadFabricAdapter
    {
        public bool TryClaimNonceInTransaction(HrReadAdmission admission, CancellationToken cancellationToken) => false;
        public MaterializedHrRead ReadInSnapshot(HrReadAdmission admission, CancellationToken cancellationToken) => throw new UnauthorizedAccessException();
        public bool RevalidateInNewSnapshot(HrReadAdmission admission, CancellationToken cancellationToken) => false;
    }
    private sealed class ResponseWriter(HttpContext context) : IHrReadResponseWriter
    {
        public bool SendAttempted { get; private set; }
        public async ValueTask SendAsync(int statusCode, ReadOnlyMemory<byte> bytes, string cacheControl, CancellationToken cancellationToken)
        {
            if (statusCode is not (200 or 404) || cacheControl != "no-store" || context.Response.HasStarted)
                throw new InvalidOperationException("Invalid HR response state.");
            context.Response.StatusCode = statusCode; context.Response.Headers.CacheControl = "no-store";
            context.Response.ContentType = statusCode == 200 ? "application/json" : null;
            context.Response.ContentLength = bytes.Length;
            cancellationToken.ThrowIfCancellationRequested();
            SendAttempted = true;
            await context.Response.StartAsync(cancellationToken).ConfigureAwait(false);
            if (!bytes.IsEmpty) await context.Response.Body.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
    }
}
