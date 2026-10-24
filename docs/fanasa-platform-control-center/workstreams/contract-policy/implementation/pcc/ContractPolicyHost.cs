using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Governance;
using Fanasa.AccessManagement.Web.Organization;
using Fanasa.AccessManagement.Web.Persistence;
using Fanasa.AccessManagement.Web.Platform;
using Fanasa.AccessManagement.Web.Security;
using Fanasa.ContractPolicy;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Fanasa.AccessManagement.Web.ContractPolicies;

public sealed class ContractPolicyOrganizationOptions
{
    public bool Enabled { get; init; }
    public string? Environment { get; init; }
    public string? Zone { get; init; }
    public int MaximumSnapshotAgeSeconds { get; init; } = 5;
    public bool IsConfigured => Enabled && IsIdentifier(Environment) && IsIdentifier(Zone)
        && MaximumSnapshotAgeSeconds is > 0 and <= 30;
    private static bool IsIdentifier(string? value) => value is not null
        && Regex.IsMatch(value, "\\A[a-z][a-z0-9.-]{0,63}\\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
}

public static class ContractPolicyHostServices
{
    public static IServiceCollection AddContractPolicyHost(this IServiceCollection services, IConfiguration configuration)
    {
        var module = ContractPolicyModule.FromOutputDirectory();
        services.AddSingleton(module);
        services.AddSingleton<IContractPolicyDecisionEvaluator>(module.Evaluator);
        services.AddSingleton<IContractProjectionProjector>(module.Projector);
        services.AddSingleton(module.Pep);
        services.AddSingleton(configuration.GetSection("ContractPolicy:Organization").Get<ContractPolicyOrganizationOptions>() ?? new());
        services.AddContractProducerSecurity(configuration);
        services.AddSingleton<ContractPolicyStore>();
        services.TryAddSingleton<IContractGatewayObserver, UnconfiguredContractGatewayObserver>();
        services.AddSingleton<ContractGatewayPublicationService>();
        services.AddSingleton<FabricOrganizationAccessSnapshotSource>();
        services.AddSingleton<IAccessAttributeSnapshotSource>(sp => sp.GetRequiredService<FabricOrganizationAccessSnapshotSource>());
        services.AddSingleton(sp => new TrustedAccessSnapshotProvider(sp.GetRequiredService<IAccessAttributeSnapshotSource>(),
            module.Projections, sp.GetRequiredService<TimeProvider>(),
            TimeSpan.FromSeconds(Math.Clamp(sp.GetRequiredService<ContractPolicyOrganizationOptions>().MaximumSnapshotAgeSeconds, 1, 30))));
        services.AddSingleton<OrganizationContractPolicyDecisionService>();
        services.TryAddSingleton<IContractPolicyObligationExecutor, UnconfiguredContractPolicyObligationExecutor>();
        services.TryAddSingleton<IOrganizationUnitSaveAdmission, ContractPolicyOrganizationAdmission>();
        return services;
    }
}

/// <summary>Reads Fabric records in one synchronous transaction. The central registry has a separate database.</summary>
public sealed class FabricOrganizationAccessSnapshotSource(FabricDatabase database, PersistentAccessManagement access,
    PlatformRegistry registry, ContractPolicyStore store, AccessPolicyStore legacyPolicies, TimeProvider time)
    : IAccessAttributeSnapshotSource
{
    public ValueTask<AccessAttributeSnapshotData?> ReadAsync(TrustedAccessSnapshotQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = database.Transaction<AccessAttributeSnapshotData?>(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!Guid.TryParseExact(query.Resource.ConsumerTenantId, "D", out var consumer)
                || query.Resource.ProductId != OrganizationContractPolicyDecisionService.Product
                || query.Action != OrganizationContractPolicyDecisionService.Action) return null;
            var projection = store.Find(consumer);
            if (projection is null || projection.SchemaVersion != "2.0.0") return null;
            var products = registry.Products().Where(x => x.Key == projection.ProductId).ToArray();
            if (products.Length != 1 || products[0].Audience != query.AuthenticatedSubject.Audience
                || !Guid.TryParseExact(projection.OwnerTenantId, "D", out var owner)
                || products[0].TenantId != owner) return null;
            var tenants = access.GetTenants();
            var tenant = tenants.SingleOrDefault(x => x.Id == consumer);
            if (tenant is null || !tenants.Any(x => x.Id == owner && x.IsActive)) return null;
            var member = access.GetUsers(consumer).SingleOrDefault(x => x.KeycloakSubject == query.AuthenticatedSubject.SubjectId);
            var assignment = store.ReadRoleAssignment(consumer, query.AuthenticatedSubject.SubjectId);
            if (member is null || assignment is null) return null;
            var role = access.GetRoles(consumer, projection.ProductId).SingleOrDefault(x => x.Id == assignment.RoleId);
            if (role is null || role.Key != "ApiInvoker" || !role.Permissions.Contains(OrganizationBearerSecurity.Permission, StringComparer.Ordinal)) return null;
            var policy = store.ReadPolicy(consumer);
            if (policy is null || !policy.Active || !Equivalent(policy.Binding, projection.Policy)) return null;
            var allocation = store.ReadAllocation(projection.Limits.AllocationId);
            if (allocation is null || !Equivalent(allocation, projection.Limits)) return null;
            var now = time.GetUtcNow();
            var direct = access.GetGrants(member.KeycloakSubject).SingleOrDefault(x => x.TenantId == consumer
                && x.Permission == OrganizationBearerSecurity.Permission);
            // Published Deny/error remains a veto. A legacy Permit never supplies a missing grant/role.
            var legacy = tenant.IsActive ? legacyPolicies.Evaluate(consumer, member.KeycloakSubject,
                OrganizationBearerSecurity.Permission, now) : null;
            var active = assignment.Active && assignment.ValidFrom <= now && now < assignment.ValidTo
                && direct is not null && (!direct.ExpiresAt.HasValue || direct.ExpiresAt > now)
                && legacy is { Decision: "Permit", Obligations.Length: 0 };
            var grantEnd = direct?.ExpiresAt is { } expiry && expiry < assignment.ValidTo ? expiry : assignment.ValidTo;
            var freshnessEnd = now.AddSeconds(30);
            // Membership has no source expiry: evidence bounds this observation.
            return new(projection, new(projection.ConsumerTenantId, tenant.IsActive),
                new(member.KeycloakSubject, projection.ConsumerTenantId, member.IsActive && member.JoinedAt <= now, Format(member.JoinedAt), Format(freshnessEnd)),
                new("role-" + role.Id.ToString("N"), member.KeycloakSubject, projection.ConsumerTenantId,
                    projection.ProductId, role.Key, [OrganizationContractPolicyDecisionService.Action], active,
                    Format(assignment.ValidFrom), Format(grantEnd)),
                new(Format(freshnessEnd), projection.ContractVersion, policy.Binding.PolicyVersion), now);
        });
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(result);
    }

    internal static bool Equivalent<T>(T first, T second) => JsonSerializer.Serialize(first, ContractJson.Options)
        == JsonSerializer.Serialize(second, ContractJson.Options);
    internal static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}

/// <summary>Signed identity and server route/environment form the query; caller attributes are never PIP input.</summary>
public sealed class OrganizationContractPolicyDecisionService(ContractPolicyOrganizationOptions settings,
    OrganizationBearerOptions bearer, PersistentAccessManagement access, PlatformRegistry registry,
    TrustedAccessSnapshotProvider snapshots, ContractPolicyModule module, TimeProvider time)
{
    public const string Product = "organization-fabric";
    public const string Action = "organization.unit.save";
    public const string Audience = "fanasa-organization-fabric-api";
    public bool IsConfigured => settings.IsConfigured && bearer.IsConfigured && bearer.Scope == Action && bearer.Audience == Audience;

    public async ValueTask<PolicyDecision> EvaluateAsync(Guid tenant, ClaimsPrincipal principal, string requestId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsConfigured || tenant == Guid.Empty || !ContractTokenIdentity.TryRead(principal, time, out var identity, out var subject, out var expires))
            return Failure(requestId, "identity-denied");
        static string? Single(ClaimsIdentity identity, string type)
        {
            var values = identity.Claims.Where(x => x.Type == type).Take(2).ToArray();
            return values.Length == 1 ? values[0].Value : null;
        }
        var scope = Single(identity, "scope")?.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (Single(identity, "iss") != bearer.Authority || Single(identity, "azp") != bearer.ClientId
            || !identity.Claims.Any(x => x.Type == "aud" && x.Value == Audience)
            || identity.Claims.Any(x => x.Type == "client_id" && x.Value != bearer.ClientId)
            || identity.Claims.Any(x => x.Type == "active" && x.Value.Equals("false", StringComparison.OrdinalIgnoreCase))
            || scope is null || !scope.Contains(Action, StringComparer.Ordinal)) return Failure(requestId, "identity-denied");
        try
        {
            var products = registry.Products().Where(x => x.Key == Product && x.Audience == Audience).ToArray();
            if (products.Length != 1 || !access.GetClients(products[0].Id).Any(x => x.Key == bearer.ClientId && x.IsActive
                && x.AllowedScopes.Contains(Action, StringComparer.Ordinal))) return Failure(requestId, "client-denied");
            var query = new TrustedAccessSnapshotQuery(requestId, new(subject, true, true, Audience, [Action]),
                new(Product, tenant.ToString("D"), settings.Environment!, "organization"), Action, settings.Zone!,
                FabricOrganizationAccessSnapshotSource.Format(expires));
            var snapshot = await snapshots.ReadAsync(query, cancellationToken);
            if (!snapshot.Succeeded) return Failure(requestId, snapshot.Status);
            // Registry/client are separate authority: read them again after awaited source work.
            if (!ContractTokenIdentity.TryRead(principal, time, out _, out _, out _)
                || !registry.Products().Any(x => x.Id == products[0].Id && x.Key == Product
                    && x.Audience == Audience && x.TenantId == products[0].TenantId)
                || !access.GetClients(products[0].Id).Any(x => x.Key == bearer.ClientId && x.IsActive
                    && x.AllowedScopes.Contains(Action, StringComparer.Ordinal))) return Failure(requestId, "client-denied");
            return module.Evaluator.Evaluate(snapshot.Projection, snapshot.Context);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return Failure(requestId, "source-unavailable"); }
    }

    private PolicyDecision Failure(string requestId, string reason) => new()
    {
        SchemaVersion = "2.0.0", ProfileId = "fanasa.contract-policy/2", RequestId = requestId,
        Decision = "Indeterminate", ReasonCode = reason, EvaluatedAt = FabricOrganizationAccessSnapshotSource.Format(time.GetUtcNow()),
        ValidUntil = null, PolicyReferences = [], ProjectionReference = null, AllocationReference = null, Obligations = []
    };
}

/// <summary>Host-owned Gateway adapter returns definitive completion for this exact operation/payload.
/// No caller/header completion is accepted. The default implementation is unavailable.</summary>
public interface IContractPolicyObligationExecutor
{
    bool IsConfigured { get; }
    ValueTask<IReadOnlyCollection<string>?> FulfillAsync(Guid tenant, string actor, OrgCommand command,
        PolicyDecision decision, CancellationToken cancellationToken);
}

internal sealed class UnconfiguredContractPolicyObligationExecutor : IContractPolicyObligationExecutor
{
    public bool IsConfigured => false;
    public ValueTask<IReadOnlyCollection<string>?> FulfillAsync(Guid tenant, string actor, OrgCommand command,
        PolicyDecision decision, CancellationToken cancellationToken) => ValueTask.FromResult<IReadOnlyCollection<string>?>(null);
}

public sealed class ContractPolicyOrganizationAdmission(OrganizationContractPolicyDecisionService decisions,
    IContractPolicyObligationExecutor executor, ContractPolicyModule module, TimeProvider time) : IOrganizationUnitSaveAdmission
{
    public bool IsConfigured => decisions.IsConfigured && executor.IsConfigured;
    public async ValueTask<bool> AdmitAsync(Guid tenant, string actor, OrgCommand command, ClaimsPrincipal principal, CancellationToken cancellationToken)
    {
        if (!IsConfigured || command.Operation != "unit.save" || !Guid.TryParseExact(command.RequestId, "D", out var request)
            || request == Guid.Empty || !ContractTokenIdentity.TryRead(principal, time, out _, out var subject, out _) || subject != actor) return false;
        var requestId = "r-" + request.ToString("N");
        var decision = await decisions.EvaluateAsync(tenant, principal, requestId, cancellationToken);
        if (decision.Decision != "Permit") return false;
        string[]? completed;
        try { completed = (await executor.FulfillAsync(tenant, actor, command, ContractJson.Copy(decision), cancellationToken))?.ToArray(); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { return false; }
        // Completion must identify exactly the obligations issued for this decision.
        if (completed is null || completed.Length != decision.Obligations.Count
            || completed.Distinct(StringComparer.Ordinal).Count() != completed.Length
            || completed.Any(id => !decision.Obligations.Any(obligation => obligation.Id == id))) return false;
        var fresh = await decisions.EvaluateAsync(tenant, principal, requestId, cancellationToken);
        if (fresh.Decision != "Permit" || !FabricOrganizationAccessSnapshotSource.Equivalent(
            fresh with { EvaluatedAt = null, ValidUntil = null }, decision with { EvaluatedAt = null, ValidUntil = null })) return false;
        return module.Pep.CanExecute(decision, completed, FabricOrganizationAccessSnapshotSource.Format(time.GetUtcNow()));
    }
}
