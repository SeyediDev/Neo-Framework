using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Domain.Access;
using Fanasa.AccessManagement.Web.Persistence;
using Fanasa.AccessManagement.Web.Platform;
using Fanasa.HrReadCandidate;
using Fanasa.HrReadPdp;

namespace Fanasa.HrReadHost;

/// <summary>
/// Current native authority for the independently introspected token pair. Consumer identity
/// comes from ALL eligible native direct organization.read grants and current memberships,
/// never a selected route/body/tenant claim or the first membership. This is the existing
/// product's native registered-client delegation, not a new grant or delegation database.
/// Role/scoped assignment, HR3 projection and legacy veto remain the PDP source's checks.
/// </summary>
public sealed class Hr3CentralIntrospectionAuthority : IHr3IntrospectionAuthority
{
    private readonly FabricDatabase database;
    private readonly PlatformRegistry registry;
    private readonly PersistentAccessManagement access;
    private readonly HrReadReceiverSettings binding;
    private readonly HrReadPdpCallerSettings gateway;
    private readonly TimeProvider time;

    public Hr3CentralIntrospectionAuthority(FabricDatabase existingDatabase, PlatformRegistry existingRegistry,
        PersistentAccessManagement existingAccess, HrReadReceiverSettings binding,
        HrReadPdpCallerSettings gateway, TimeProvider? time = null)
    {
        database = existingDatabase ?? throw new ArgumentNullException(nameof(existingDatabase));
        registry = existingRegistry ?? throw new ArgumentNullException(nameof(existingRegistry));
        access = existingAccess ?? throw new ArgumentNullException(nameof(existingAccess));
        this.binding = (binding ?? throw new ArgumentNullException(nameof(binding))) with { };
        this.gateway = (gateway ?? throw new ArgumentNullException(nameof(gateway))) with { };
        this.time = time ?? TimeProvider.System;
    }

    public Hr3IntrospectionAuthoritySnapshot? ReadCurrent(HrReadPdpRequest request,
        AuthenticatedHrReadPdpCaller caller, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (!MatchesTransport(request, caller, time.GetUtcNow().ToUniversalTime())) return null;
            // When invoked by retained proof inside Hr3CentralHostRecords, the caller
            // already owns the SAME query-only Fabric snapshot. Before/after HTTPS calls
            // have no ambient context and open a short independent read snapshot here.
            // No asynchronous work, HTTP, nested transaction or write occurs in either.
            var result = database.IsInTransaction
                ? ReadSnapshot(request, caller, cancellationToken)
                : database.ReadSnapshot(() => ReadSnapshot(request, caller, cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
            var now = time.GetUtcNow().ToUniversalTime();
            return result is not null && now < result.ValidUntil && MatchesTransport(request, caller, now)
                ? result : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { cancellationToken.ThrowIfCancellationRequested(); return null; }
    }

    private Hr3IntrospectionAuthoritySnapshot? ReadSnapshot(HrReadPdpRequest request,
        AuthenticatedHrReadPdpCaller caller, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = time.GetUtcNow().ToUniversalTime();
        if (!MatchesTransport(request, caller, now)) return null;
        var firstProduct = Product();
        if (firstProduct is null) return null;
        var clients = access.GetClients(firstProduct.Id).ToArray();
        var gatewayClients = clients.Where(x => x.Key == gateway.ClientId).Take(2).ToArray();
        var subjectClients = clients.Where(x => x.Key == binding.ClientId).Take(2).ToArray();
        var scopes = HrReadPdpService.AuthorizationScopes(caller);
        if (gatewayClients.Length != 1 || subjectClients.Length != 1
            || !CurrentClient(gatewayClients[0], firstProduct.Id, clients, [gateway.RequiredScope])
            || !CurrentClient(subjectClients[0], firstProduct.Id, clients, scopes)
            || !scopes.Contains(HrReadDesign.ReadScope, StringComparer.Ordinal)) return null;

        // Enumerate by authenticated subject only. Do not use request.ConsumerTenantId
        // to filter candidates: a second eligible consumer makes the whole proof ambiguous.
        var grants = access.GetGrants(binding.SubjectId)
            .Where(x => x.Permission == HrReadDesign.Permission).OrderBy(x => x.TenantId).ToArray();
        if (grants.Length == 0 || grants.Length > 1000
            || grants.Any(x => x.Subject != binding.SubjectId || x.TenantId == Guid.Empty
                || x.ExpiresAt is { Offset: var offset } && offset != TimeSpan.Zero)
            || grants.GroupBy(x => x.TenantId).Any(x => x.Count() != 1)) return null;
        var tenants = access.GetTenants().ToArray();
        var eligible = new List<(Tenant Tenant, TenantUser Member, TenantGrant Grant)>();
        var relevantNativeRecords = new List<object>();
        foreach (var grant in grants)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var tenantRows = tenants.Where(x => x.Id == grant.TenantId).Take(2).ToArray();
            if (tenantRows.Length > 1) return null;
            var memberRows = access.GetUsers(grant.TenantId)
                .Where(x => x.KeycloakSubject == binding.SubjectId).OrderBy(x => x.Id).ToArray();
            if (memberRows.Length > 1) return null;
            relevantNativeRecords.Add(new { Grant = grant, Tenant = tenantRows.SingleOrDefault(), Members = memberRows });
            if (tenantRows.Length != 1 || !tenantRows[0].IsActive || memberRows.Length != 1 || !memberRows[0].IsActive
                || grant.ExpiresAt is { } expiration && expiration <= now) continue;
            var tenant = tenantRows[0]; var member = memberRows[0];
            if (tenant.CreatedAt.Offset != TimeSpan.Zero || tenant.CreatedAt > now
                || member.Id == Guid.Empty || member.TenantId != tenant.Id
                || member.JoinedAt.Offset != TimeSpan.Zero || member.JoinedAt > now) return null;
            eligible.Add((tenant, member, grant));
        }
        if (eligible.Count != 1) return null;
        var selected = eligible[0];
        // Compare only AFTER independent unique selection, never choose a requested tenant.
        if (selected.Tenant.Id.ToString("D") != request.ConsumerTenantId) return null;
        var lastProduct = Product();
        if (lastProduct is null || !Equivalent(ProductAuthority(firstProduct), ProductAuthority(lastProduct))) return null;

        var revisionPayload = new
        {
            Schema = "fanasa.hr3.native-introspection-authority/1",
            Product = ProductAuthority(lastProduct),
            GatewaySubjectId = gateway.SubjectId, GatewayClient = ClientAuthority(gatewayClients[0]),
            GatewayRequiredScope = gateway.RequiredScope,
            SubjectId = binding.SubjectId, SubjectClient = ClientAuthority(subjectClients[0]),
            SubjectScopes = scopes, NativeRecords = relevantNativeRecords
        };
        var revision = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(revisionPayload))));
        var until = new[] { now.AddSeconds(HrReadDesign.MaximumWitnessSeconds), caller.ValidUntil,
            request.ValidUntil, selected.Grant.ExpiresAt ?? DateTimeOffset.MaxValue }.Min();
        cancellationToken.ThrowIfCancellationRequested();
        return until > now ? new(selected.Tenant.Id, gateway.SubjectId, gateway.ClientId,
            binding.SubjectId, binding.ClientId, revision, until) : null;
    }

    private PlatformProduct? Product()
    {
        var rows = registry.Products();
        var product = rows.Where(x => x.Key == HrReadDesign.ProductKey).Take(2).ToArray();
        if (product.Length != 1 || rows.Count(x => x.Id == product[0].Id) != 1
            || product[0].Center != "fanasa.ayin") return null;
        var value = product[0];
        return binding.MatchesCurrentRegistration(new(value.Id, value.Key, value.Audience, value.Owner, value.TenantId))
            ? value : null;
    }

    private bool MatchesTransport(HrReadPdpRequest? request, AuthenticatedHrReadPdpCaller? caller, DateTimeOffset now)
        => request is not null && caller is not null && binding.Enabled && binding.Validate().Count == 0
            && gateway.Validate().Count == 0 && gateway.Issuer == binding.Issuer && request.Binding == binding
            && caller.IsFor(request) && caller.RevalidateCurrent() && now < request.ValidUntil && now < caller.ValidUntil
            && request.SubjectId == binding.SubjectId && request.ClientId == binding.ClientId
            && request.Audience == binding.Audience && request.ProductId == binding.ProductKey
            && request.TokenSha256 == caller.SubjectTokenSha256
            && request.TokenExpiresAt == caller.SubjectValidUntil.ToUnixTimeSeconds()
            && HrReadPdpAuthentication.PrincipalMatches(caller.CallerPrincipal, gateway.Issuer, gateway.Audience,
                gateway.ClientId, gateway.SubjectId, gateway.RequiredScope, now, out var gatewayExpiry)
            && gatewayExpiry == caller.CallerValidUntil
            && HrReadPdpAuthentication.PrincipalMatches(caller.SubjectPrincipal, binding.Issuer, binding.Audience,
                binding.ClientId, binding.SubjectId, HrReadDesign.ReadScope, now, out var subjectExpiry)
            && subjectExpiry == caller.SubjectValidUntil;

    private static bool CurrentClient(ProductClient client, Guid productId, ProductClient[] all,
        IReadOnlyList<string> requiredScopes)
        => client.Id != Guid.Empty && client.IsActive && client.ProductId == productId
            && all.Count(x => x.Id == client.Id) == 1 && client.AllowedScopes is { Count: > 0 and <= 64 }
            && client.AllowedScopes.Distinct(StringComparer.Ordinal).Count() == client.AllowedScopes.Count
            && requiredScopes.Count is > 0 and <= 64
            && requiredScopes.All(x => client.AllowedScopes.Contains(x, StringComparer.Ordinal));

    private static object ProductAuthority(PlatformProduct p) => new { p.Id, p.Key, p.Audience, p.Center, p.Owner, p.TenantId };
    private static object ClientAuthority(ProductClient c) => new { c.Id, c.ProductId, c.Key, c.ClientType, c.IsActive,
        AllowedScopes = c.AllowedScopes.OrderBy(x => x, StringComparer.Ordinal).ToArray() };
    private static bool Equivalent(object left, object right) => JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);
}
