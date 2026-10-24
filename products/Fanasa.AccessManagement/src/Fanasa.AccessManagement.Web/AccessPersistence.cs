using Fanasa.AccessManagement.Web.Domain.Access;
using Microsoft.EntityFrameworkCore;

namespace Fanasa.AccessManagement.Web.Infrastructure.Persistence;

public sealed class AccessDbContext(DbContextOptions<AccessDbContext> options) : DbContext(options)
{
    public DbSet<TenantRow> Tenants => Set<TenantRow>();
    public DbSet<ProductRow> Products => Set<ProductRow>();
    public DbSet<ProductClientRow> ProductClients => Set<ProductClientRow>();
    public DbSet<ContractEntitlementRow> ContractEntitlements => Set<ContractEntitlementRow>();
    public DbSet<UsageEventRow> UsageEvents => Set<UsageEventRow>();
    public DbSet<PricingPlanRow> PricingPlans => Set<PricingPlanRow>();
    public DbSet<PricingRuleRow> PricingRules => Set<PricingRuleRow>();
    public DbSet<AccountBalanceRow> AccountBalances => Set<AccountBalanceRow>();
    public DbSet<UsageChargeRow> UsageCharges => Set<UsageChargeRow>();
    public DbSet<TenantSubscriptionRow> TenantSubscriptions => Set<TenantSubscriptionRow>();
    public DbSet<TenantUserRow> TenantUsers => Set<TenantUserRow>();
    public DbSet<AccessRoleRow> AccessRoles => Set<AccessRoleRow>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.HasDefaultSchema("access");
        b.Entity<TenantRow>().HasIndex(x => x.Key).IsUnique();
        b.Entity<ProductRow>().HasIndex(x => x.Key).IsUnique();
        b.Entity<ProductClientRow>().HasIndex(x => new { x.ProductId, x.Key }).IsUnique();
        b.Entity<UsageEventRow>().HasIndex(x => x.IdempotencyKey).IsUnique();
        b.Entity<UsageChargeRow>().HasIndex(x => x.IdempotencyKey).IsUnique();
        b.Entity<TenantUserRow>().HasIndex(x => new { x.TenantId, x.KeycloakSubject }).IsUnique();
        b.Entity<TenantRow>().Property(x => x.CreatedAt).HasPrecision(3);
        b.Entity<UsageEventRow>().Property(x => x.Quantity).HasPrecision(19, 6);
        b.Entity<UsageChargeRow>().Property(x => x.Amount).HasPrecision(19, 2);
        b.Entity<PricingPlanRow>().Property(x => x.FixedMonthlyAmount).HasPrecision(19, 2);
        b.Entity<PricingPlanRow>().Property(x => x.IncludedCredit).HasPrecision(19, 2);
        b.Entity<PricingRuleRow>().Property(x => x.UnitPrice).HasPrecision(19, 6);
        b.Entity<PricingRuleRow>().Property(x => x.IncludedQuantity).HasPrecision(19, 6);
    }
}

public sealed class TenantRow { public Guid Id { get; set; } public string Key { get; set; } = ""; public string DisplayName { get; set; } = ""; public bool IsActive { get; set; } public DateTimeOffset CreatedAt { get; set; } }
public sealed class ProductRow { public Guid Id { get; set; } public string Key { get; set; } = ""; public string DisplayName { get; set; } = ""; public string Audience { get; set; } = ""; public string? RepositoryUrl { get; set; } public string CenterSlug { get; set; } = ""; public string AttributesJson { get; set; } = "{}"; }
public sealed class ProductClientRow { public Guid Id { get; set; } public Guid ProductId { get; set; } public string Key { get; set; } = ""; public string DisplayName { get; set; } = ""; public string ClientType { get; set; } = ""; public string RedirectUrisJson { get; set; } = "[]"; public string AllowedScopesJson { get; set; } = "[]"; public bool IsActive { get; set; } }
public sealed class ContractEntitlementRow { public Guid Id { get; set; } public Guid TenantId { get; set; } public string ExternalContractId { get; set; } = ""; public string ProductKey { get; set; } = ""; public string CenterSlug { get; set; } = ""; public string PlanKey { get; set; } = ""; public decimal? IncludedQuantity { get; set; } public string Unit { get; set; } = ""; public DateTimeOffset EffectiveFrom { get; set; } public DateTimeOffset? EffectiveTo { get; set; } public string Status { get; set; } = ""; public DateTimeOffset UpdatedAt { get; set; } }
public sealed class UsageEventRow { public Guid Id { get; set; } public Guid TenantId { get; set; } public string ProductKey { get; set; } = ""; public string CenterSlug { get; set; } = ""; public string Metric { get; set; } = ""; public decimal Quantity { get; set; } public string Unit { get; set; } = ""; public DateTimeOffset OccurredAt { get; set; } public string Source { get; set; } = ""; public string IdempotencyKey { get; set; } = ""; public string? CorrelationId { get; set; } public DateTimeOffset RecordedAt { get; set; } }
public sealed class PricingPlanRow { public Guid Id { get; set; } public string ProductKey { get; set; } = ""; public string Key { get; set; } = ""; public string DisplayName { get; set; } = ""; public string BillingMode { get; set; } = ""; public string Currency { get; set; } = ""; public decimal FixedMonthlyAmount { get; set; } public decimal IncludedCredit { get; set; } public bool IsActive { get; set; } }
public sealed class PricingRuleRow { public Guid Id { get; set; } public Guid PlanId { get; set; } public string Metric { get; set; } = ""; public string Unit { get; set; } = ""; public decimal UnitPrice { get; set; } public decimal IncludedQuantity { get; set; } public decimal? MaximumQuantity { get; set; } }
public sealed class AccountBalanceRow { public Guid TenantId { get; set; } public string AccountType { get; set; } = ""; public decimal Available { get; set; } public decimal Reserved { get; set; } public string Currency { get; set; } = "IRR"; public DateTimeOffset UpdatedAt { get; set; } }
public sealed class UsageChargeRow { public Guid Id { get; set; } public Guid TenantId { get; set; } public string ProductKey { get; set; } = ""; public string Metric { get; set; } = ""; public decimal Quantity { get; set; } public decimal Amount { get; set; } public string Currency { get; set; } = ""; public string IdempotencyKey { get; set; } = ""; public DateTimeOffset CalculatedAt { get; set; } }
public sealed class TenantSubscriptionRow { public Guid Id { get; set; } public Guid TenantId { get; set; } public string ProductKey { get; set; } = ""; public Guid PricingPlanId { get; set; } public string Status { get; set; } = ""; public DateTimeOffset StartedAt { get; set; } public DateTimeOffset? RenewsAt { get; set; } public int SeatLimit { get; set; } public int ActiveSeats { get; set; } }
public sealed class TenantUserRow { public Guid Id { get; set; } public Guid TenantId { get; set; } public string KeycloakSubject { get; set; } = ""; public string? DisplayName { get; set; } public bool IsActive { get; set; } public DateTimeOffset JoinedAt { get; set; } }
public sealed class AccessRoleRow { public Guid Id { get; set; } public Guid TenantId { get; set; } public string ProductKey { get; set; } = ""; public string Key { get; set; } = ""; public string DisplayName { get; set; } = ""; public string PermissionsJson { get; set; } = "[]"; }
