namespace Fanasa.ContractPolicy;

/// <summary>
/// New, user-authorized HR read policy design. These names describe this candidate;
/// their presence does not provision a contract, allocation, membership or client.
/// The audience is the existing organization-fabric product's observed audience.
/// </summary>
public static class HrReadProfile
{
    public const string SchemaVersion = "3.0.0";
    public const string ProfileId = "fanasa.organization-hr-read/1";
    public const string ProductId = "organization-fabric";
    public const string Audience = "fanasa-organization-fabric";
    public const string RequiredScope = "organization.hr.read";
    public const string OperationGroup = "organization.hr.read";
    public const string ChartAction = "organization.hr.chart.read";
    public const string HistoryAction = "organization.hr.history.read";
    public const string TemplateId = "organization-hr-read/1";
    public const string Role = "HrReader";

    public static bool IsAction(string? action) => action is ChartAction or HistoryAction;
}

public sealed record ProjectionSelector(string ConsumerTenantId, string ProfileId,
    string ProductId, string ContractId);

/// <summary>
/// Selects exactly one projection from an existing authoritative source snapshot.
/// This utility owns no store or fallback. Callers must still validate the result
/// and evaluate all current tenant, membership, grant and contract gates.
/// </summary>
public static class ContractProjectionSelector
{
    public static ContractAccessProjection? FindSingle(IEnumerable<ContractAccessProjection> projections,
        ProjectionSelector selector)
    {
        ArgumentNullException.ThrowIfNull(projections);
        ArgumentNullException.ThrowIfNull(selector);
        if (string.IsNullOrWhiteSpace(selector.ConsumerTenantId) || string.IsNullOrWhiteSpace(selector.ProfileId)
            || string.IsNullOrWhiteSpace(selector.ProductId) || string.IsNullOrWhiteSpace(selector.ContractId))
            throw new ArgumentException("Every projection selector dimension is required.", nameof(selector));
        var matches = projections.Where(p => p is not null
            && p.ConsumerTenantId == selector.ConsumerTenantId && p.ProfileId == selector.ProfileId
            && p.ProductId == selector.ProductId && p.ContractId == selector.ContractId).Take(2).ToArray();
        if (matches.Length > 1) throw new InvalidOperationException("Ambiguous projection selector.");
        return matches.Length == 0 ? null : ContractJson.Copy(matches[0]);
    }
}
