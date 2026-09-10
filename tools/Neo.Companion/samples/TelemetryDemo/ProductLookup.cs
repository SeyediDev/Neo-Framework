using System.Diagnostics;
using System.Reflection;
using Neo.Domain.Features.Telementry;

namespace Neo.Companion.TelemetryDemo;

// Only non-sensitive demo data passes through telemetry's request/response logging.
public sealed record LookupRequest(Guid ProductId);
public sealed record LookupResult(Guid ProductId, bool Available);

public interface IProductLookup
{
    [Telemetry("catalog", "lookup", ActivityKind.Internal)]
    Task<LookupResult?> LookupAsync(LookupRequest request, CancellationToken cancellationToken);
}

public sealed class ProductLookup : IProductLookup
{
    public async Task<LookupResult?> LookupAsync(LookupRequest request, CancellationToken cancellationToken)
    {
        await Task.Delay(10, cancellationToken);
        return new LookupResult(request.ProductId, true);
    }
}

public sealed class ManualProductLookup(ProductLookup inner, ITelementryBehaviour telemetry) : IProductLookup
{
    public Task<LookupResult?> LookupAsync(LookupRequest request, CancellationToken cancellationToken) =>
        telemetry.HandleRequestResponse<LookupRequest, LookupResult>(
            inner.LookupAsync, request, "catalog", "lookup", ActivityKind.Internal,
            [new("feature", "product_lookup")], cancellationToken);
}
