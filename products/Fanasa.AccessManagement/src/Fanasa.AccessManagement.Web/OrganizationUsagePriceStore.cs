using System.Globalization;
using System.Text.Json;
using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Persistence;
using Fanasa.AccessManagement.Web.Platform;

namespace Fanasa.AccessManagement.Web.Metering;

public sealed record OrganizationUsagePrice(string PriceId, int PriceVersion, Guid OwnerTenantId,
    string ProductKey, string MeterId, string MeterVersion, string MeterUnit, UsageRateSnapshot RateSnapshot,
    string Actor, string Reason, DateTimeOffset RecordedAt);

/// <summary>Exact, immutable commercial price references in the existing Fabric database.</summary>
public sealed class OrganizationUsagePriceStore(FabricDatabase database, IAccessManagement access,
    PlatformRegistry registry, TimeProvider? timeProvider = null)
{
    private const string Kind = "organization-usage.price";
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;

    // Caller authorization belongs to the administration API, like the other Fabric stores.
    public OrganizationUsagePrice Publish(string priceId, int priceVersion, UsageRateSnapshot rate, string actor, string reason)
    {
        ValidateReference(priceId, priceVersion, rate); UsageEventV1.Label(actor); UsageEventV1.Label(reason, 1000);
        return database.Transaction(() =>
        {
            var owner = CurrentOwner(); var previous = Read(priceId, priceVersion);
            if (previous is not null)
            {
                if (previous.OwnerTenantId != owner || previous.RateSnapshot != rate)
                    throw new InvalidOperationException("A price ID/version already identifies different content.");
                return previous;
            }
            var value = new OrganizationUsagePrice(priceId, priceVersion, owner, OrganizationUsageMetering.ProductKey,
                "organization.change", "1", "change", rate, actor.Trim(), reason.Trim(), clock.GetUtcNow().ToUniversalTime());
            database.Put(Kind, Key(priceId, priceVersion), value);
            database.Emit("organization-usage-price:" + Key(priceId, priceVersion), "OrganizationUsagePricePublished",
                value, value.RecordedAt);
            return value;
        });
    }

    public OrganizationUsagePrice? Read(string priceId, int priceVersion)
    {
        RequireReference(priceId, priceVersion);
        var value = database.Read<OrganizationUsagePrice>(Kind, Key(priceId, priceVersion));
        if (value is null) return null;
        try
        {
            ValidateReference(value.PriceId, value.PriceVersion, value.RateSnapshot);
            UsageEventV1.Label(value.Actor); UsageEventV1.Label(value.Reason, 1000);
            if (value.PriceId != priceId || value.PriceVersion != priceVersion || value.OwnerTenantId == Guid.Empty
                || value.ProductKey != OrganizationUsageMetering.ProductKey || value.MeterId != "organization.change"
                || value.MeterVersion != "1" || value.MeterUnit != "change" || value.RecordedAt.Offset != TimeSpan.Zero)
                throw new InvalidDataException("Invalid persisted organization price binding.");
            return value;
        }
        catch (ArgumentException error) { throw new InvalidDataException("Invalid persisted organization price.", error); }
    }

    public UsageRateSnapshot Resolve(string priceId, int priceVersion, Guid expectedOwner, DateTimeOffset acceptedAt)
    {
        if (acceptedAt.Offset != TimeSpan.Zero) throw new ArgumentException("Price admission time must be UTC.");
        var value = Read(priceId, priceVersion) ?? throw new UnauthorizedAccessException("The exact contract price reference is unpublished.");
        if (expectedOwner != CurrentOwner() || value.OwnerTenantId != expectedOwner
            || acceptedAt < value.RateSnapshot.EffectiveFrom || acceptedAt >= value.RateSnapshot.EffectiveTo)
            throw new UnauthorizedAccessException("The contract price owner or admitted rate window does not match.");
        return value.RateSnapshot;
    }

    private Guid CurrentOwner()
    {
        var products = registry.Products().Where(x => x.Key == OrganizationUsageMetering.ProductKey).Take(2).ToArray();
        if (products.Length != 1 || products[0].TenantId is not { } owner
            || access.GetTenants().Count(x => x.Id == owner && x.IsActive) != 1)
            throw new UnauthorizedAccessException("The central organization product has no unique active owner.");
        return owner;
    }

    private static void ValidateReference(string priceId, int priceVersion, UsageRateSnapshot rate)
    {
        RequireReference(priceId, priceVersion); ArgumentNullException.ThrowIfNull(rate);
        _ = UsageEventV1.ParseDecimal(rate.UnitPrice);
        if (rate.RateId != priceId || rate.RateVersion != priceVersion
            || rate.EffectiveFrom.Offset != TimeSpan.Zero || rate.EffectiveTo.Offset != TimeSpan.Zero
            || rate.EffectiveFrom >= rate.EffectiveTo || rate.Currency is not ("IRR" or "USD" or "EUR")
            || rate.MinorUnitScale != (rate.Currency == "IRR" ? 0 : 2)
            || rate.Rounding != "away-from-zero" || rate.BillingMode is not ("payg" or "postpaid"))
            throw new ArgumentException("Organization price reference or rate snapshot is invalid.");
    }
    private static void RequireReference(string priceId, int priceVersion)
    {
        UsageEventV1.Label(priceId);
        if (priceVersion < 1) throw new ArgumentException("Price version must be a positive UsageEvent 1.0 integer.");
    }
    private static string Key(string priceId, int version) => UsageEventV1.Hash(JsonSerializer.Serialize(new[]
        { priceId, version.ToString(CultureInfo.InvariantCulture) }));
}
