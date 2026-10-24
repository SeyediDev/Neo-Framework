using System.Globalization;
using Fanasa.AccessManagement.Web.Accounting;
using Fanasa.AccessManagement.Web.Metering;
using Fanasa.AccessManagement.Web.Persistence;
using Microsoft.Extensions.DependencyInjection;

// Disposable, loopback-only UI fixture. The production host never references this assembly.
// These labels and snapshots are synthetic evidence, not commercial or Gateway authority.
static class UsageAccountingPreview
{
    public static void Register(IServiceCollection services, FabricDatabase database, AccountingStore accounting, Guid consumer)
    {
        var now = TimeProvider.System.GetUtcNow();
        var events = Enumerable.Range(1, 27).Select(index => Fixture(consumer, index, now)).ToArray();
        var validator = new SyntheticPreviewValidator(events);
        var measured = new MeasuredUsageStore(database, accounting, validator);

        // Retain the last three completed fixture events before configuring the account.
        // Its default payg mode cannot account for their frozen postpaid snapshot.
        foreach (var usage in events.Skip(24)) measured.Accept(usage, retainPending: true);
        accounting.Execute(consumer, new BillingCommand(accounting.Read(consumer).Account.Revision,
            "configure", "synthetic-preview-account", "postpaid", "IRR", 10_000_000,
            null, null, 0, 0, 0, "Disposable synthetic preview account only", null, null), "demo");
        foreach (var usage in events.Take(24)) measured.Accept(usage);

        services.AddSingleton<IUsageSnapshotValidator>(validator);
        services.AddSingleton(measured);
        services.AddSingleton<UsageReconciliationStore>();
        services.AddSingleton(new MeasuredUsageRecoveryStore(database, measured));
    }

    private static UsageEventV1 Fixture(Guid consumer, int index, DateTimeOffset now)
    {
        var key = $"synthetic-preview:{consumer:D}:{index}";
        var requestId = UsageEventV1.StableGuid(key + ":request");
        var acceptedAt = now.AddDays(-8).AddHours((index - 1) * 6);
        var rate = new UsageRateSnapshot("synthetic-preview.organization-change", 1,
            now.AddDays(-10), now.AddHours(-12), "120000", "IRR", 0, "away-from-zero", "postpaid");
        return new UsageEventV1("1.0", UsageEventV1.StableGuid(key + ":event"), acceptedAt.AddSeconds(2), acceptedAt.AddSeconds(3),
            new("synthetic-preview-workload", "product-backend", "disposable-loopback-preview"), "development",
            consumer, consumer, OrganizationUsageMetering.ProductKey, "synthetic-preview-contract", 1,
            "synthetic-preview-allocation", acceptedAt.ToString("yyyy-MM", CultureInfo.InvariantCulture),
            new("organization-unit-save:" + requestId.ToString("D"), "organization.unit.save/v1", "demo", requestId,
                UsageEventV1.Hash(key + ":synthetic-unit-save"), acceptedAt, "organization-unit-committed/v1"),
            new("synthetic-preview-decision:" + index, "synthetic-preview-policy/v1", "synthetic-preview-admission:" + index),
            new("organization.change", "1", "change", "1"), rate, "completed",
            new(key + ":synthetic-receipt", UsageEventV1.Hash(key + ":synthetic-result"), "fixture://local-preview/organization/" + index), requestId);
    }

    private sealed class SyntheticPreviewValidator(IEnumerable<UsageEventV1> events) : IUsageSnapshotValidator
    {
        private readonly Dictionary<string, string> allowed = events.ToDictionary(x => x.EventKey, x => x.EventFingerprint, StringComparer.Ordinal);
        public void ValidateNew(UsageEventV1 message)
        {
            message.Validate();
            if (!allowed.TryGetValue(message.EventKey, out var fingerprint) || fingerprint != message.EventFingerprint)
                throw new UnauthorizedAccessException("Only the exact disposable synthetic preview events are approved by this fixture.");
        }
    }
}
