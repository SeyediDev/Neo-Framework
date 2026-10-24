using System.Globalization;
using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Platform;
using Fanasa.AccessManagement.Web.Security;
using Fanasa.ContractPolicy;

namespace Fanasa.AccessManagement.Web.ContractPolicies;

/// <summary>
/// Receives identity assertions only from the separately authenticated and registered Gateway.
/// Tenant, grant, role, policy and allocation remain authoritative local PIP reads.
/// </summary>
public sealed class ContractGatewayDecisionService(ContractPolicyOrganizationOptions settings,
    OrganizationBearerOptions bearer, PersistentAccessManagement access, PlatformRegistry registry,
    TrustedAccessSnapshotProvider snapshots, ContractPolicyModule module, TimeProvider time)
{
    public async ValueTask<PolicyDecision> EvaluateAsync(ContractGatewayPolicyRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ContractGatewayPolicyRequestJson.TryParse(System.Text.Json.JsonSerializer.Serialize(request,
                ContractJson.Options), out _)
            || !settings.IsConfigured || !bearer.IsConfigured
            || bearer.Audience != OrganizationContractPolicyDecisionService.Audience
            || bearer.Scope != OrganizationContractPolicyDecisionService.Action
            || request.SchemaVersion != "fanasa.gateway-pdp.request.v2"
            || request.ProductId != OrganizationContractPolicyDecisionService.Product
            || request.Action != OrganizationContractPolicyDecisionService.Action
            || request.Audience != bearer.Audience || request.ClientId != bearer.ClientId
            || request.Scope != bearer.Scope || request.Environment != settings.Environment
            || request.OperationGroup != "organization"
            || !NonemptyGuid(request.SubjectId) || !NonemptyGuid(request.ConsumerTenantId)
            || !NonemptyGuid(request.OwnerTenantId) || !IsCurrent(request))
            return Failure(request.RequestId, "gateway-binding-denied");

        try
        {
            var products = registry.Products().Where(x => x.Key == request.ProductId
                && x.Audience == request.Audience).ToArray();
            if (products.Length != 1 || products[0].TenantId is not { } owner
                || !Guid.TryParseExact(request.OwnerTenantId, "D", out var requestedOwner) || owner != requestedOwner
                || !IsClientCurrent(products[0].Id, request.ClientId, request.Scope))
                return Failure(request.RequestId, "client-denied");

            var query = new TrustedAccessSnapshotQuery(request.RequestId,
                new(request.SubjectId, true, true, request.Audience, [request.Scope]),
                new(request.ProductId, request.ConsumerTenantId, settings.Environment!, "organization"),
                request.Action, settings.Zone!, Format(DateTimeOffset.FromUnixTimeSeconds(request.TokenExpiresAt)));
            var snapshot = await snapshots.ReadAsync(query, cancellationToken);
            if (!snapshot.Succeeded) return Failure(request.RequestId, snapshot.Status);
            if (!IsCurrent(request)
                || !registry.Products().Any(x => x.Id == products[0].Id && x.Key == request.ProductId
                    && x.Audience == request.Audience && x.TenantId == products[0].TenantId)
                || !IsClientCurrent(products[0].Id, request.ClientId, request.Scope))
                return Failure(request.RequestId, "client-denied");

            // A route cannot select another owner's contract or narrow the shared allocation.
            if (snapshot.Projection!.OwnerTenantId != request.OwnerTenantId
                || snapshot.Projection.Limits.AllocationId != request.AllocationId
                || snapshot.Projection.Limits.CanonicalScope != request.AllocationScope)
                return Failure(request.RequestId, "allocation-binding-denied");
            return module.Evaluator.Evaluate(snapshot.Projection, snapshot.Context);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return Failure(request.RequestId, "source-unavailable"); }
    }

    private bool IsClientCurrent(Guid product, string client, string scope)
        => access.GetClients(product).Any(x => x.Key == client && x.IsActive
            && x.AllowedScopes.Contains(scope, StringComparer.Ordinal));

    private bool IsCurrent(ContractGatewayPolicyRequest request)
    {
        var now = (time.GetUtcNow().UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks)
            / (decimal)TimeSpan.TicksPerSecond;
        return request.RequestedAt >= 0 && request.RequestedAt <= now
            && now - request.RequestedAt <= settings.MaximumSnapshotAgeSeconds
            && request.TokenExpiresAt > now && request.TokenExpiresAt <= 253402300799;
    }

    private static bool NonemptyGuid(string value)
        => Guid.TryParseExact(value, "D", out var parsed) && parsed != Guid.Empty;

    private static string Format(DateTimeOffset value)
        => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private PolicyDecision Failure(string requestId, string reason) => new()
    {
        SchemaVersion = "2.0.0", ProfileId = "fanasa.contract-policy/2", RequestId = requestId,
        Decision = "Indeterminate", ReasonCode = reason, EvaluatedAt = Format(time.GetUtcNow()),
        ValidUntil = null, PolicyReferences = [], ProjectionReference = null,
        AllocationReference = null, Obligations = []
    };
}
