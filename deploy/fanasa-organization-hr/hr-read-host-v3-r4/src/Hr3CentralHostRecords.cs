using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Fanasa.AccessManagement.Web.Application.Tenancy;
using Fanasa.AccessManagement.Web.Domain.Access;
using Fanasa.AccessManagement.Web.Governance;
using Fanasa.AccessManagement.Web.Organization;
using Fanasa.AccessManagement.Web.Persistence;
using Fanasa.AccessManagement.Web.Platform;
using Fanasa.AccessManagement.Web.Security;
using Fanasa.ContractPolicy;
using Fanasa.HrReadCandidate;
using Fanasa.HrReadPdp;

namespace Fanasa.HrReadHost;

/// <summary>
/// Request-scoped result of independent authenticated introspection AND an authoritative
/// Gateway delegation check. It is not a body/JWT tenant claim or a caller-supplied boolean.
/// Only the host assembly that completes those checks can construct it. The issuer must
/// retain a stable observation and check its current revocation/delegation revision on
/// every ReadCurrent call; a successful earlier HTTP introspection is not a Permit cache.
/// </summary>
public sealed class Hr3RetainedAuthenticationProof
{
    internal Hr3RetainedAuthenticationProof(string gatewaySubjectId, string gatewayClientId,
        string gatewayHeaderSha256, string subjectId, string subjectClientId,
        string subjectTokenSha256, DateTimeOffset subjectTokenExpiresAt, Guid consumerTenantId,
        ProjectionSelector selector, string action, IReadOnlyList<string> scopes,
        string authorityRevision, DateTimeOffset observedAt, DateTimeOffset validUntil,
        string gatewayRequiredScope, string requestId)
    {
        GatewaySubjectId = gatewaySubjectId; GatewayClientId = gatewayClientId;
        GatewayHeaderSha256 = gatewayHeaderSha256; SubjectId = subjectId;
        SubjectClientId = subjectClientId; SubjectTokenSha256 = subjectTokenSha256;
        SubjectTokenExpiresAt = subjectTokenExpiresAt; ConsumerTenantId = consumerTenantId;
        Selector = selector with { }; Action = action; Scopes = Array.AsReadOnly(scopes.ToArray());
        AuthorityRevision = authorityRevision; ObservedAt = observedAt; ValidUntil = validUntil;
        GatewayRequiredScope = gatewayRequiredScope; RequestId = requestId;
    }

    public string GatewaySubjectId { get; }
    public string GatewayClientId { get; }
    public string GatewayHeaderSha256 { get; }
    public string SubjectId { get; }
    public string SubjectClientId { get; }
    public string SubjectTokenSha256 { get; }
    public DateTimeOffset SubjectTokenExpiresAt { get; }
    public Guid ConsumerTenantId { get; }
    public ProjectionSelector Selector { get; }
    public string Action { get; }
    public IReadOnlyList<string> Scopes { get; }
    public string AuthorityRevision { get; }
    public DateTimeOffset ObservedAt { get; }
    public DateTimeOffset ValidUntil { get; }
    public string GatewayRequiredScope { get; }
    public string RequestId { get; }
}

public interface IHr3RetainedAuthenticationSource
{
    /// <summary>
    /// Synchronous current read of previously completed asynchronous authentication.
    /// Return null if either token, the unique server-owned consumer mapping, delegation,
    /// client or revocation revision is unavailable/changed. Never perform HTTP here,
    /// block an async task, derive consumer from request/claims, or return a stale proof.
    /// </summary>
    Hr3RetainedAuthenticationProof? ReadCurrent(TrustedAccessSnapshotQuery query,
        AuthenticatedHrReadPdpCaller caller, CancellationToken cancellationToken);
}

public sealed class UnavailableHr3RetainedAuthenticationSource : IHr3RetainedAuthenticationSource
{
    public Hr3RetainedAuthenticationProof? ReadCurrent(TrustedAccessSnapshotQuery query,
        AuthenticatedHrReadPdpCaller caller, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); return null; }
}

/// <summary>
/// Read-only adapter over the EXISTING central registry and the EXISTING Fabric access
/// services. Host integration compiles this file with those services; it must not load a
/// second copy of FabricDatabase or construct a database, registry or access service here.
/// The supplied database is identity-checked and must already be in the source's query-only
/// snapshot. The external registry is sampled twice because it has a separate DB boundary.
/// No Authorize call, outbox event, grant, membership, role or policy is created.
/// </summary>
public sealed class Hr3CentralHostRecords : IHrReadPdpHostRecords
{
    private sealed record Capture(HrReadPdpHostRecordSnapshot Snapshot, string AuthorityRevision);
    private static readonly JsonSerializerOptions StableJson = new() { PropertyNameCaseInsensitive = false };
    private readonly FabricDatabase database;
    private readonly PlatformRegistry registry;
    private readonly PersistentAccessManagement access;
    private readonly AccessPolicyStore legacyPolicies;
    private readonly IHr3RetainedAuthenticationSource authentication;
    private readonly TimeProvider time;
    private readonly TemporalAccessGate? temporalAccess;

    public Hr3CentralHostRecords(FabricDatabase existingDatabase, PlatformRegistry existingRegistry,
        PersistentAccessManagement existingAccess, AccessPolicyStore existingLegacyPolicies,
        IHr3RetainedAuthenticationSource? authentication = null, TimeProvider? time = null, TemporalAccessGate? temporalAccess = null)
    {
        database = existingDatabase ?? throw new ArgumentNullException(nameof(existingDatabase));
        registry = existingRegistry ?? throw new ArgumentNullException(nameof(existingRegistry));
        access = existingAccess ?? throw new ArgumentNullException(nameof(existingAccess));
        legacyPolicies = existingLegacyPolicies ?? throw new ArgumentNullException(nameof(existingLegacyPolicies));
        this.authentication = authentication ?? new UnavailableHr3RetainedAuthenticationSource();
        this.time = time ?? TimeProvider.System;
        this.temporalAccess = temporalAccess;
    }

    public HrReadPdpHostRecordSnapshot? ReadCurrent(FabricDatabase snapshotDatabase,
        TrustedAccessSnapshotQuery query, AuthenticatedHrReadPdpCaller caller,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            if (!ReferenceEquals(database, snapshotDatabase) || !database.IsInTransaction
                || query is null || caller is null || !caller.RevalidateCurrent()) return null;
            var first = Read(query, caller, cancellationToken);
            if (first is null) return null;
            // Read current registry/authentication again after all native access reads.
            // Stable identity fields must agree; a shorter authority lease is safe.
            var last = Read(query, caller, cancellationToken);
            if (last is null || first.AuthorityRevision != last.AuthorityRevision
                || !Equivalent(first.Snapshot with { ValidUntil = last.Snapshot.ValidUntil }, last.Snapshot)) return null;
            var now = time.GetUtcNow().ToUniversalTime();
            var lease = Min(first.Snapshot.ValidUntil, last.Snapshot.ValidUntil, caller.ValidUntil);
            if (now >= lease || !caller.RevalidateCurrent()) return null;
            cancellationToken.ThrowIfCancellationRequested();
            return last.Snapshot with { ValidUntil = lease };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception) { cancellationToken.ThrowIfCancellationRequested(); return null; }
    }

    private Capture? Read(TrustedAccessSnapshotQuery query,
        AuthenticatedHrReadPdpCaller caller, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var observedAt = time.GetUtcNow().ToUniversalTime();
        if (!caller.RevalidateCurrent() || query.Selector is not { } selector
            || selector.ProfileId != HrReadDesign.ProfileId || selector.ProductId != HrReadDesign.ProductKey
            || selector.ConsumerTenantId != query.Resource.ConsumerTenantId
            || query.Resource.ProductId != HrReadDesign.ProductKey
            || query.Resource.OperationGroup != HrReadDesign.Group || !HrReadProfile.IsAction(query.Action)
            || !CanonicalGuid(selector.ConsumerTenantId, out var tenantId)
            || !CanonicalGuid(query.AuthenticatedSubject.SubjectId, out _)
            || query.AuthenticatedSubject is not { AuthenticationValid: true, ClientAuthorized: true }
            || query.AuthenticatedSubject.Audience != HrReadDesign.Audience
            || !CurrentUtc(query.AuthenticationValidUntil, observedAt, out var queryExpiry)) return null;
        var proof = authentication.ReadCurrent(ContractJson.Copy(query), caller, cancellationToken);
        if (proof is null || !ProofMatches(proof, query, caller, observedAt, tenantId)) return null;

        // Registry metadata, not settings/request ownership. Duplicate keys or UUIDs deny.
        var products = registry.Products();
        var matches = products.Where(x => x.Key == HrReadDesign.ProductKey).Take(2).ToArray();
        if (matches.Length != 1) return null;
        var product = matches[0];
        if (product.Id == Guid.Empty || products.Count(x => x.Id == product.Id) != 1
            || product.Audience != HrReadDesign.Audience || product.Center != "fanasa.ayin"
            || string.IsNullOrWhiteSpace(product.Owner)) return null;
        var registration = new HrReadRegistration(product.Id, product.Key, product.Audience,
            product.Owner, product.TenantId);

        var gatewayClients = access.GetClients(product.Id).Where(x => x.Key == proof!.GatewayClientId).Take(2).ToArray();
        if (gatewayClients.Length != 1 || !gatewayClients[0].IsActive || gatewayClients[0].ProductId != product.Id
            || gatewayClients[0].AllowedScopes is not { Count: > 0 and <= 64 }
            || gatewayClients[0].AllowedScopes.Distinct(StringComparer.Ordinal).Count() != gatewayClients[0].AllowedScopes.Count
            || !gatewayClients[0].AllowedScopes.Contains(proof.GatewayRequiredScope, StringComparer.Ordinal)) return null;

        // Current registered client and complete representable scope set must still agree.
        var clients = access.GetClients(product.Id).Where(x => x.Key == proof!.SubjectClientId).Take(2).ToArray();
        if (clients.Length != 1 || !clients[0].IsActive || clients[0].ProductId != product.Id
            || clients[0].AllowedScopes is null
            || clients[0].AllowedScopes.Count > 64
            || clients[0].AllowedScopes.Distinct(StringComparer.Ordinal).Count() != clients[0].AllowedScopes.Count
            || !clients[0].AllowedScopes.Contains(HrReadDesign.ReadScope, StringComparer.Ordinal)
            || query.AuthenticatedSubject.Scopes.Any(x => !clients[0].AllowedScopes.Contains(x, StringComparer.Ordinal))) return null;

        var tenants = access.GetTenants().Where(x => x.Id == tenantId).Take(2).ToArray();
        if (tenants.Length != 1 || !tenants[0].IsActive) return null;
        var members = access.GetUsers(tenantId)
            .Where(x => x.KeycloakSubject == proof.SubjectId && x.TenantId == tenantId).Take(2).ToArray();
        if (members.Length != 1 || !members[0].IsActive || members[0].Id == Guid.Empty
            || members[0].JoinedAt.Offset != TimeSpan.Zero || members[0].JoinedAt > observedAt) return null;

        // The same scoped assignment is separately validated by HrReadPdpFabricSource.
        // It selects an EXISTING native role; role name alone creates no membership.
        var assignment = database.Read<HrReadPdpProductRoleAssignment>(HrReadPdpFabricSource.RoleAssignmentBucket,
            HrReadPdpFabricSource.RoleAssignmentKey(selector, proof.SubjectId));
        if (assignment is null || !assignment.Active || assignment.TenantId != tenantId
            || assignment.Subject != proof.SubjectId || assignment.RoleId == Guid.Empty
            || assignment.Revision is < 1 or > 9007199254740991L
            || assignment.ValidFrom.Offset != TimeSpan.Zero || assignment.ValidTo.Offset != TimeSpan.Zero
            || assignment.ValidFrom > observedAt || observedAt >= assignment.ValidTo) return null;
        var roles = access.GetRoles(tenantId, product.Key).Where(x => x.Id == assignment.RoleId).Take(2).ToArray();
        if (roles.Length != 1 || roles[0].TenantId != tenantId || roles[0].ProductKey != product.Key
            || roles[0].Key != HrReadDesign.Role || roles[0].Permissions is not { Count: > 0 and <= 64 }
            || roles[0].Permissions.Any(x => !Identifier(x))
            || roles[0].Permissions.Distinct(StringComparer.Ordinal).Count() != roles[0].Permissions.Count
            || !roles[0].Permissions.Contains(HrReadDesign.Permission, StringComparer.Ordinal)) return null;

        // An actual current native direct grant is required in addition to HR3 policy/role.
        // A legacy policy Permit alone is insufficient. The natural persisted grant key is
        // its stable view identity; observation bounds do not invent a historical grant date.
        var grants = access.GetGrants(proof.SubjectId).Where(x => x.Subject == proof.SubjectId
            && x.TenantId == tenantId && x.Permission == HrReadDesign.Permission).Take(2).ToArray();
        if (grants.Length != 1 || grants[0].ExpiresAt is { } expires
            && (expires.Offset != TimeSpan.Zero || expires <= observedAt)) return null;
        var legacy = legacyPolicies.EvaluateForReadOnlyHost(tenantId, proof.SubjectId, HrReadDesign.Permission, observedAt,
            temporalAccess?.IsEnabled(tenantId) == true);
        // This query-only adapter cannot fulfill the native audit obligation. An
        // explicitly enabled calendar rule therefore fails closed until its audit
        // is handled by a separate, reviewed admission integration. Never discard it.
        if (legacy.Decision != "Permit" || legacy.Obligations.Length != 0) return null;

        // A scheduled native deny/appointment boundary may precede the short lease.
        // Bound the witness by ALL known future edges, including nonmatching patterns,
        // so a later applicable rule is never hidden by the prepared direct-grant Permit.
        var nextHour = new DateTimeOffset(observedAt.UtcDateTime.Date.AddHours(observedAt.UtcDateTime.Hour + 1), TimeSpan.Zero);
        var boundaries = new List<DateTimeOffset> { proof.ValidUntil, caller.ValidUntil, queryExpiry,
            assignment.ValidTo, nextHour, grants[0].ExpiresAt ?? DateTimeOffset.MaxValue };
        var state = legacyPolicies.Read(tenantId);
        foreach (var pattern in state.Patterns)
        {
            if ((pattern.CalendarId.HasValue || pattern.Template == "time-guard" || pattern.CalendarMode != "working-time")
                && (pattern.Status != "published" || !pattern.Enforce || temporalAccess?.IsEnabled(tenantId) != true)) continue;
            if (pattern.StartsAt is { } start && start > observedAt) boundaries.Add(start);
            if (pattern.EndsAt is { } end && end > observedAt) boundaries.Add(end);
        }
        var chart = database.Read<OrgState>("organization", tenantId.ToString("D"));
        if (chart is not null) foreach (var appointment in chart.Appointments)
        {
            if (appointment.From > observedAt) boundaries.Add(appointment.From);
            if (appointment.To is { } end && end > observedAt) boundaries.Add(end);
        }
        var until = boundaries.Min();
        if (until <= observedAt) return null;
        cancellationToken.ThrowIfCancellationRequested();
        return new Capture(new HrReadPdpHostRecordSnapshot(proof.GatewaySubjectId, proof.GatewayClientId,
            new SubjectAttributes(proof.SubjectId, true, true, product.Audience, proof.Scopes.ToArray()),
            query.Resource with { }, registration, new TenantAttributes(tenantId.ToString("D"), true),
            new MembershipAttributes(proof.SubjectId, tenantId.ToString("D"), true,
                Format(members[0].JoinedAt), Format(proof.ValidUntil)),
            new HrReadPdpHostRoleSnapshot(roles[0].Id, roles[0].Key,
                roles[0].Permissions.OrderBy(x => x, StringComparer.Ordinal).ToArray()),
            new GrantAttributes(NativeGrantId(tenantId, proof.SubjectId), proof.SubjectId,
                tenantId.ToString("D"), product.Key, HrReadDesign.Role,
                new[] { HrReadDesign.Action(HrReadEndpointKind.Chart), HrReadDesign.Action(HrReadEndpointKind.History) },
                true, Format(proof.ObservedAt), Format(Min(proof.ValidUntil, grants[0].ExpiresAt ?? DateTimeOffset.MaxValue))),
            until, true, true, true, true, true, true), proof.AuthorityRevision);
    }

    private static bool ProofMatches(Hr3RetainedAuthenticationProof? proof,
        TrustedAccessSnapshotQuery query, AuthenticatedHrReadPdpCaller caller,
        DateTimeOffset now, Guid consumerTenantId)
    {
        if (proof is null || proof.ConsumerTenantId != consumerTenantId || proof.ConsumerTenantId == Guid.Empty
            || proof.RequestId != query.RequestId || !Identifier(proof.GatewayRequiredScope)
            || proof.Selector != query.Selector || proof.Action != query.Action
            || proof.SubjectId != query.AuthenticatedSubject.SubjectId
            || proof.SubjectTokenSha256 != caller.SubjectTokenSha256
            || proof.GatewayHeaderSha256 != caller.HeaderSha256
            || proof.SubjectTokenExpiresAt != caller.SubjectValidUntil
            || proof.ObservedAt.Offset != TimeSpan.Zero || proof.ValidUntil.Offset != TimeSpan.Zero
            || proof.ObservedAt > now || now >= proof.ValidUntil || proof.ValidUntil <= proof.ObservedAt
            || proof.ValidUntil > proof.ObservedAt.AddSeconds(HrReadDesign.MaximumWitnessSeconds)
            || proof.ValidUntil > caller.ValidUntil || string.IsNullOrWhiteSpace(proof.AuthorityRevision)
            || proof.AuthorityRevision.Length > 256 || proof.AuthorityRevision.Any(c => c < '!' || c > '~')
            || proof.Scopes is not { Count: > 0 and <= 64 }
            || !proof.Scopes.SequenceEqual(query.AuthenticatedSubject.Scopes, StringComparer.Ordinal)
            || !proof.Scopes.SequenceEqual(HrReadPdpService.AuthorizationScopes(caller), StringComparer.Ordinal)) return false;
        var gateways = caller.CallerPrincipal.Identities.Where(x => x.IsAuthenticated).Take(2).ToArray();
        var subjects = caller.SubjectPrincipal.Identities.Where(x => x.IsAuthenticated).Take(2).ToArray();
        return gateways.Length == 1 && subjects.Length == 1
            && Single(gateways[0], "sub") == proof.GatewaySubjectId
            && Single(gateways[0], "azp") == proof.GatewayClientId
            && Single(subjects[0], "sub") == proof.SubjectId
            && Single(subjects[0], "azp") == proof.SubjectClientId;
    }

    private static string NativeGrantId(Guid tenant, string subject)
        => "native-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            tenant.ToString("D") + ":" + subject + ":" + HrReadDesign.Permission))).ToLowerInvariant()[..57];
    private static string? Single(ClaimsIdentity identity, string type)
    { var values = identity.FindAll(type).Take(2).ToArray(); return values.Length == 1 ? values[0].Value : null; }
    private static bool CanonicalGuid(string? value, out Guid id) => Guid.TryParseExact(value, "D", out id)
        && id != Guid.Empty && id.ToString("D") == value;
    private static bool Identifier(string? value) => value is { Length: > 0 and <= 64 }
        && value[0] is >= 'a' and <= 'z'
        && value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '-');
    private static bool CurrentUtc(string value, DateTimeOffset now, out DateTimeOffset until)
    {
        until = default;
        return ContractJson.IsCanonicalUtc(value) && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind, out until) && until.Offset == TimeSpan.Zero && until > now;
    }
    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
    private static DateTimeOffset Min(params DateTimeOffset[] values) => values.Min();
    private static bool Equivalent<T>(T left, T right) => JsonSerializer.Serialize(left, StableJson) == JsonSerializer.Serialize(right, StableJson);
}
