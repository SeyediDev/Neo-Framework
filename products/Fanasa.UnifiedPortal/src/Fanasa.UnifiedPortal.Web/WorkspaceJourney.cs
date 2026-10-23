using System.Text.Json.Serialization;

namespace Fanasa.UnifiedPortal.Web;

// This presentation seam does not grant access, register products or post accounting entries.
// Production remains disconnected until the central offer/order/usage contracts exist.
public interface IWorkspaceJourneySource
{
    bool IsSyntheticPreview { get; }
    Task<WorkspaceJourneyData> GetAsync(string subject, Guid organizationId, CancellationToken cancellationToken);
}

public sealed record WorkspaceJourneyData(
    IReadOnlyList<WorkspaceOffer> Catalog,
    IReadOnlyList<WorkspaceRequest> Requests,
    IReadOnlyList<WorkspacePublication> Publications,
    WorkspaceUsage Usage,
    Guid OrganizationId,
    string Subject);

public sealed record WorkspaceOffer(string Id, string Key, string Name, string Center, string Description,
    IReadOnlyList<WorkspacePlan> Plans, string Status = "available");
public sealed record WorkspacePlan(string Id, string Name, string Amount, string Currency, string Interval, string Quota);
public sealed record WorkspaceHistory(string Status, string Label, string At);
public sealed record WorkspaceRequest(string Id, string ServiceId, string ServiceName, string PlanId, string PlanName,
    Guid OrganizationId, string Status, string CreatedAt, string Reason, IReadOnlyList<WorkspaceHistory> History,
    [property: JsonIgnore] string RequestedBySubject);
public sealed record WorkspacePublication(string Id, string Name, string Center, string Description, string Url,
    Guid OrganizationId, string Status, string CreatedAt, IReadOnlyList<WorkspaceHistory> History,
    [property: JsonIgnore] string SubmittedBySubject);
public sealed record WorkspaceUsage(string Status, string PeriodLabel, string AsOf, string Currency, string TotalAmount,
    IReadOnlyList<WorkspaceUsageItem> Items)
{
    public static WorkspaceUsage NotConnected { get; } = new("notConnected", "", "", "IRR", "", []);
    public static WorkspaceUsage Unavailable { get; } = new("unavailable", "", "", "IRR", "", []);
}
public sealed record WorkspaceUsageItem(string ServiceId, string Name, string Quantity, string Unit, string Amount, string Quota);
public sealed record WorkspaceActiveService(string Id, string Key, string Name, string Center, string? Url);
public sealed record WorkspaceCenter(string Id, string Name, string Logo);
public sealed record WorkspaceBootstrap(string Mode, string SubjectKey, Guid? OrganizationId,
    IReadOnlyCollection<PlatformOrganizationView> Organizations, string OrganizationName,
    string CatalogStatus, string Message, bool IsAdmin, IReadOnlyList<WorkspaceOffer> Catalog,
    IReadOnlyList<WorkspaceActiveService> ActiveServices, IReadOnlyList<WorkspaceRequest> Requests,
    IReadOnlyList<WorkspacePublication> Publications, WorkspaceUsage Usage,
    IReadOnlyList<WorkspaceCenter>? Centers = null,
    ServiceCatalogPresentation? PublishedCatalog = null, string? AccountingUrl = null);

public static class WorkspaceCenters
{
    // Capability identities and packaged assets already used by the public Portal home.
    // These descriptors carry no product registration or authorization data.
    public static IReadOnlyList<WorkspaceCenter> All { get; } = new (string Key, string Name)[]
    {
        ("abrgan", "ابرگان"), ("karavan", "کاروان"), ("kavan", "کاوان"), ("peyvand", "پیوند"),
        ("dana", "دانا"), ("bonyan", "بنیان"), ("fanasa-cloud", "ابر فن‌آسا"), ("asman", "آسمان"),
        ("kerane", "کرانه"), ("rayan", "رایان"), ("dejan", "دژان"), ("ayin", "آیین"),
        ("didban", "دیده‌بان"), ("tarazu", "ترازو")
    }.Select(x => new WorkspaceCenter(x.Key == "fanasa-cloud" ? "fanasa.cloud" : "fanasa." + x.Key,
        x.Name, "/brand/center-" + x.Key + "-mark-color.svg")).ToArray();
}

public sealed class NotConnectedWorkspaceJourneySource : IWorkspaceJourneySource
{
    public bool IsSyntheticPreview => false;
    public Task<WorkspaceJourneyData> GetAsync(string subject, Guid organizationId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new WorkspaceJourneyData([], [], [], WorkspaceUsage.NotConnected, organizationId, subject));
    }
}
