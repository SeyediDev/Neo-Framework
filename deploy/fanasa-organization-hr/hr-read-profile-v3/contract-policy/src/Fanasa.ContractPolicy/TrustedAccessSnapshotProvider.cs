using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using static Fanasa.ContractPolicy.ContractProjectionValidator;

namespace Fanasa.ContractPolicy;

/// <summary>
/// Host-owned token validation supplies AuthenticatedSubject. The host supplies
/// Resource, Action and Zone from its registered route and server environment.
/// AuthenticationValidUntil is the verified token/client policy expiry.
/// Request body attributes or raw claims are not a trusted query.
/// </summary>
public sealed record TrustedAccessSnapshotQuery(
    string RequestId,
    SubjectAttributes AuthenticatedSubject,
    ResourceAttributes Resource,
    string Action,
    string Zone,
    string AuthenticationValidUntil,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] ProjectionSelector? Selector = null);

/// <summary>
/// A correlated read from an authoritative source. ObservedAt describes when
/// all these attributes were observed together; Evidence bounds that read's
/// expiry and carries the accepted contract and policy versions.
/// </summary>
public sealed record AccessAttributeSnapshotData(
    ContractAccessProjection Projection,
    TenantAttributes Tenant,
    MembershipAttributes Membership,
    GrantAttributes Grant,
    EvidenceAttributes Evidence,
    DateTimeOffset ObservedAt);

/// <summary>
/// The consuming host owns source authentication, storage and consistent reads.
/// This package supplies no network or persistence implementation.
/// </summary>
public interface IAccessAttributeSnapshotSource
{
    ValueTask<AccessAttributeSnapshotData?> ReadAsync(
        TrustedAccessSnapshotQuery query,
        CancellationToken cancellationToken = default);
}

public sealed record TrustedAccessSnapshotResult(
    string Status,
    ContractAccessProjection? Projection,
    AccessEvaluationContext? Context,
    IReadOnlyList<string> Errors)
{
    public bool Succeeded => Status == "ready" && Projection is not null && Context is not null;
}

/// <summary>
/// Produces a bounded, owned PIP context using server time. Failures contain no
/// usable context or projection and never fall back to a previous Permit.
/// Independent calls are safe in parallel when the injected source is safe.
/// </summary>
public sealed class TrustedAccessSnapshotProvider
{
    private static readonly Regex Identity = new(
        "\\A(?:[a-z][a-z0-9.-]{0,63}|[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})\\z",
        RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private readonly IAccessAttributeSnapshotSource _source;
    private readonly ContractProjectionValidator _validator;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _maximumSnapshotAge;

    public TrustedAccessSnapshotProvider(
        IAccessAttributeSnapshotSource source,
        ContractProjectionValidator validator,
        TimeProvider timeProvider,
        TimeSpan maximumSnapshotAge)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(validator);
        ArgumentNullException.ThrowIfNull(timeProvider);
        if (maximumSnapshotAge <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maximumSnapshotAge), "Snapshot age must be positive.");
        _source = source;
        _validator = validator;
        _timeProvider = timeProvider;
        _maximumSnapshotAge = maximumSnapshotAge;
    }

    public async ValueTask<TrustedAccessSnapshotResult> ReadAsync(
        TrustedAccessSnapshotQuery? query,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!ValidQuery(query))
            return Failure("invalid-query", "A trusted authenticated subject and route binding are required.");

        TrustedAccessSnapshotQuery ownedQuery;
        try { ownedQuery = ContractJson.Copy(query!); }
        catch (Exception error) when (UnsupportedInput(error))
        { return Failure("invalid-query", "Query cannot be represented as supported JSON."); }

        AccessAttributeSnapshotData? data;
        try
        {
            // Keep the retained token/route attributes separate from the source's
            // query graph, including arrays a source might hold after returning.
            data = await _source.ReadAsync(ContractJson.Copy(ownedQuery), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Failure("source-unavailable", "Trusted attributes could not be read.");
        }
        cancellationToken.ThrowIfCancellationRequested();

        if (data is null)
            return Failure("missing-snapshot", "Trusted attributes are missing.");

        try { data = ContractJson.Copy(data); }
        catch (Exception error) when (UnsupportedInput(error))
        { return Failure("invalid-snapshot", "Snapshot cannot be represented as supported JSON."); }

        if (data.Projection is null || data.Tenant is null || data.Membership is null
            || data.Grant is null || data.Evidence is null)
            return Failure("missing-snapshot", "Required trusted attributes are missing.");
        if (_validator.Validate(data.Projection).Count != 0)
            return Failure("invalid-snapshot", "Accepted projection is invalid.");
        if (!Correlated(ownedQuery, data))
            return Failure("correlation-mismatch", "Trusted attributes do not describe the same subject, resource and versions.");
        if (!ContractJson.IsCanonicalUtc(data.Evidence.ValidUntil))
            return Failure("invalid-snapshot", "Evidence expiry must be a canonical UTC instant.");

        // Sample the clock after the read so an asynchronous source cannot make
        // a context fresh by retaining the request's earlier server time.
        var serverNow = _timeProvider.GetUtcNow().ToUniversalTime();
        var now = Milliseconds(serverNow);
        var observedAt = data.ObservedAt.ToUniversalTime();
        var profileTtl = TimeSpan.FromSeconds(FreshnessSeconds(VersionOf(ContractJson.ToNode(data.Projection))));
        var maximumAge = _maximumSnapshotAge < profileTtl ? _maximumSnapshotAge : profileTtl;
        var ageLimit = AddBounded(observedAt, maximumAge);
        var authenticationExpiry = Instant(ownedQuery.AuthenticationValidUntil);
        var sourceExpiry = Instant(data.Evidence.ValidUntil);
        if (observedAt > serverNow || ageLimit <= serverNow || sourceExpiry <= serverNow
            || authenticationExpiry <= serverNow)
            return Failure("stale-snapshot", "Trusted attributes are future-dated or expired.");
        var deadline = Milliseconds(new[]
        {
            authenticationExpiry, sourceExpiry, ageLimit, AddBounded(serverNow, profileTtl)
        }.Min());
        if (deadline <= now)
            return Failure("stale-snapshot", "Trusted attributes have no usable freshness interval.");

        var context = new AccessEvaluationContext
        {
            RequestId = ownedQuery.RequestId,
            Subject = ownedQuery.AuthenticatedSubject,
            Resource = ownedQuery.Resource,
            Action = ownedQuery.Action,
            Environment = new EnvironmentAttributes(Format(now), ownedQuery.Zone),
            Tenant = data.Tenant,
            Membership = data.Membership,
            Grant = data.Grant,
            Evidence = data.Evidence with { ValidUntil = Format(deadline) }
        };
        try
        {
            if (_validator.Schemas.Validate(
                    Schema("request-context", VersionOf(ContractJson.ToNode(data.Projection))),
                    ContractJson.ToNode(context)).Count != 0)
                return Failure("invalid-snapshot", "Trusted context does not satisfy the profile schema.");
        }
        catch (Exception error) when (UnsupportedInput(error))
        { return Failure("invalid-snapshot", "Trusted context cannot be represented as supported JSON."); }

        // Inactive tenant/membership/grant data remains a valid correlated read;
        // the PDP applies those gates and returns Deny.
        return new TrustedAccessSnapshotResult("ready", data.Projection, context, []);
    }

    private static bool Correlated(TrustedAccessSnapshotQuery query, AccessAttributeSnapshotData data)
    {
        var consumer = query.Resource.ConsumerTenantId;
        var product = query.Resource.ProductId;
        var subject = query.AuthenticatedSubject.SubjectId;
        return data.Projection.ConsumerTenantId == consumer
            && data.Projection.ProductId == product
            && (data.Projection.SchemaVersion != HrReadProfile.SchemaVersion || query.Selector is not null)
            && (query.Selector is null || (query.Selector.ConsumerTenantId == consumer
                && query.Selector.ProductId == product && query.Selector.ProfileId == data.Projection.ProfileId
                && query.Selector.ContractId == data.Projection.ContractId))
            && data.Tenant.TenantId == consumer
            && data.Membership.TenantId == consumer
            && data.Membership.SubjectId == subject
            && data.Grant.ConsumerTenantId == consumer
            && data.Grant.ProductId == product
            && data.Grant.SubjectId == subject
            && data.Evidence.ContractVersion == data.Projection.ContractVersion
            && data.Evidence.PolicyVersion == data.Projection.Policy.PolicyVersion;
    }

    private static bool ValidQuery(TrustedAccessSnapshotQuery? query) =>
        query is not null
        && ValidId(query.RequestId)
        && query.AuthenticatedSubject is { AuthenticationValid: true, ClientAuthorized: true } subject
        && subject.SubjectId is not null && Identity.IsMatch(subject.SubjectId)
        && ValidId(subject.Audience)
        && subject.Scopes is { Count: <= 64 } scopes
        && scopes.All(ValidId) && scopes.Distinct(StringComparer.Ordinal).Count() == scopes.Count
        && query.Resource is { } resource
        && resource.ConsumerTenantId is not null && Identity.IsMatch(resource.ConsumerTenantId)
        && ValidId(resource.ProductId) && ValidId(resource.Environment) && ValidId(resource.OperationGroup)
        && ValidId(query.Action) && ValidId(query.Zone)
        && ContractJson.IsCanonicalUtc(query.AuthenticationValidUntil);

    private static bool ValidId(string? value) => value is not null && Id.IsMatch(value);

    private static DateTimeOffset Milliseconds(DateTimeOffset value) =>
        DateTimeOffset.FromUnixTimeMilliseconds(value.ToUnixTimeMilliseconds());

    private static DateTimeOffset AddBounded(DateTimeOffset value, TimeSpan duration) =>
        value.UtcTicks > DateTimeOffset.MaxValue.UtcTicks - duration.Ticks
            ? DateTimeOffset.MaxValue : value.Add(duration);

    private static bool UnsupportedInput(Exception error) =>
        error is JsonException or ArgumentException or InvalidOperationException or NotSupportedException;

    private static TrustedAccessSnapshotResult Failure(string status, string error) =>
        new(status, null, null, Array.AsReadOnly(new[] { error }));
}
