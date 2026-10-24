using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Fanasa.ContractPolicy;

/// <summary>Schema and semantic boundary for the explicitly supported profiles.</summary>
public sealed class ContractProjectionValidator(ContractSchemaValidator schemas)
{
    public ContractSchemaValidator Schemas { get; } = schemas;
    internal static readonly Regex Id = new("\\A[a-z][a-z0-9.-]{0,63}\\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Identity = new("\\A(?:[a-z][a-z0-9.-]{0,63}|[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})\\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    private static readonly Regex Month = new("\\A[0-9]{4}-(0[1-9]|1[0-2])\\z", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public IReadOnlyList<string> Validate(ContractAccessProjection? projection)
    {
        try { return ValidateJson(ContractJson.ToNode(projection)); }
        catch (Exception e) when (e is JsonException or ArgumentException or NotSupportedException)
        { return Array.AsReadOnly(new[] { "Projection cannot be represented as supported JSON." }); }
    }

    public IReadOnlyList<string> ValidateJson(JsonNode? projection)
    {
        if (!ContractJson.TrySnapshot(projection, out projection))
            return Array.AsReadOnly(new[] { "Projection cannot be represented as supported JSON." });
        var version = VersionOf(projection);
        if (version == 0) return Array.AsReadOnly(new[] { "Unsupported projection schemaVersion." });
        var errors = Schemas.Validate(Schema("contract-access-projection", version), projection).ToList();
        if (errors.Count != 0) return errors.AsReadOnly();
        var p = projection!.Deserialize<ContractAccessProjection>(ContractJson.Options)!;
        if (Instant(p.ValidFrom) >= Instant(p.ValidTo)) errors.Add("validFrom must precede validTo.");
        if (p.Status != "Ended" && Instant(p.EffectiveAt) >= Instant(p.ValidTo)) errors.Add("effectiveAt must precede validTo for nonterminal snapshots.");
        if (p.ConsumerTenantId != p.Limits.Scope.ConsumerTenantId) errors.Add("Allocation consumer mismatch.");
        if (!p.Limits.Scope.ProductIds.Contains(p.ProductId, StringComparer.Ordinal)) errors.Add("Product outside allocation.");
        if (CanonicalScope(p.Limits.Scope) != p.Limits.CanonicalScope) errors.Add("Noncanonical allocation scope.");
        foreach (var values in new[] { p.Limits.Scope.ProductIds, p.Limits.Scope.Environments, p.Limits.Scope.OperationGroups })
            if (!values.SequenceEqual(values.Order(StringComparer.Ordinal), StringComparer.Ordinal)) errors.Add("Allocation scope must be sorted.");
        if (p.FeatureEntitlements.Any(x => !SupportsAction(version, x))) errors.Add("Unsupported feature.");
        if (p.EligibleRoles.Any(x => x != RoleFor(version))) errors.Add("Unsupported eligible role.");
        if (p.Policy.ExplicitDenyActions.Any(x => !SupportsAction(version, x))) errors.Add("Unsupported Deny action.");
        if (version == 3)
        {
            var ownership = p.Ownership;
            static bool CanonicalUuid(string value) => Guid.TryParseExact(value, "D", out var id)
                && id != Guid.Empty && value == id.ToString("D");
            if (ownership is null || !CanonicalUuid(ownership.RegisteredProductId)
                || (ownership.Kind == "Platform"
                    ? ownership.Owner != "platform" || p.OwnerTenantId is not null
                    : ownership.Kind != "Tenant" || !CanonicalUuid(ownership.Owner)
                      || p.OwnerTenantId != ownership.Owner))
                errors.Add("HR ownership must agree with its explicit registry ownership kind.");
        }
        if (version == 3 && (p.Limits.Scope.OperationGroups.Count != 1
            || p.Limits.Scope.OperationGroups[0] != HrReadProfile.OperationGroup))
            errors.Add("HR read allocation requires the HR read operation group exclusively.");
        return errors.AsReadOnly();
    }

    public IReadOnlyList<string> ValidateEvent(ContractProjectionEvent? projectionEvent)
    {
        try
        {
            var node = ContractJson.ToNode(projectionEvent);
            var version = VersionOf(node);
            return version == 0 ? Array.AsReadOnly(new[] { "Unsupported event schemaVersion." })
                : Schemas.Validate(Schema("projection-event", version), node);
        }
        catch (Exception e) when (e is JsonException or ArgumentException or NotSupportedException)
        { return Array.AsReadOnly(new[] { "Event cannot be represented as supported JSON." }); }
    }

    public static string CanonicalScope(AllocationScope? scope)
    {
        if (scope?.ConsumerTenantId is not string tenant || !Identity.IsMatch(tenant)) throw new ArgumentException("Invalid scope tenant.", nameof(scope));
        static string Dimension(IReadOnlyList<string>? values)
        {
            if (values is null || values.Count == 0 || values.Any(x => x is null || !Id.IsMatch(x)) || values.Distinct(StringComparer.Ordinal).Count() != values.Count)
                throw new ArgumentException("Invalid scope dimension.");
            return string.Join(',', values.Order(StringComparer.Ordinal));
        }
        return $"tenant={tenant};products={Dimension(scope.ProductIds)};environments={Dimension(scope.Environments)};groups={Dimension(scope.OperationGroups)}";
    }

    public string CounterKey(AllocationLimits? limits, string kind, string? periodId = null)
    {
        JsonNode? node;
        try { node = ContractJson.ToNode(limits); }
        catch (Exception e) when (e is JsonException or ArgumentException or NotSupportedException)
        { throw new ArgumentException("Invalid allocation.", nameof(limits)); }
        if (limits is null || Schemas.Validate(Schema("limits", limits.ConfigRevision.HasValue ? 2 : 1), node).Count != 0
            || CanonicalScope(limits.Scope) != limits.CanonicalScope) throw new ArgumentException("Invalid allocation.", nameof(limits));
        if (kind is not ("rate" or "quota")) throw new ArgumentException("Unsupported limit kind.", nameof(kind));
        if (kind == "quota" && (periodId is null || !Month.IsMatch(periodId))) throw new ArgumentException("Quota requires a UTC calendar month.", nameof(periodId));
        if (kind == "rate" && periodId is not null) throw new ArgumentException("Rate has no period.", nameof(periodId));
        return $"fanasa:allocation:{limits.AllocationId}:{limits.CanonicalScope}:{kind}" + (kind == "quota" ? ":" + periodId : "");
    }

    internal static int VersionOf(JsonNode? value) => Text(Property(value, "schemaVersion")) switch
        { "1.0.0" => 1, "2.0.0" => 2, "3.0.0" => 3, _ => 0 };
    internal static string Schema(string kind, int version) => $"{kind}.v{version}.schema.json";
    internal static string ActionFor(int version) => version == 2 ? "organization.unit.save" : "report.read";
    internal static bool SupportsAction(int version, string action) => version == 3
        ? HrReadProfile.IsAction(action) : action == ActionFor(version);
    internal static string ScopeFor(int version, string action) => version == 3 ? HrReadProfile.RequiredScope : action;
    internal static string RoleFor(int version) => version == 3 ? HrReadProfile.Role : "ApiInvoker";
    internal static string ProfileFor(int version) => version == 3 ? HrReadProfile.ProfileId : $"fanasa.contract-policy/{version}";
    internal static int FreshnessSeconds(int version) => version switch { 3 => 5, 2 => 30, _ => 60 };
    internal static string AudienceFor(int version) => version switch
        { 3 => HrReadProfile.Audience, 2 => "fanasa-organization-fabric-api", _ => "fanasa-club-api" };
    internal static JsonNode? Property(JsonNode? value, string key) => value is JsonObject obj ? obj[key] : null;
    internal static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    internal static DateTimeOffset Instant(string text) => DateTimeOffset.ParseExact(text, "yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    internal static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
