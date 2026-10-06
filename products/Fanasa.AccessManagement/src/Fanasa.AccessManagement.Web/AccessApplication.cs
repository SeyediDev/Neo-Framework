using System.Collections.Concurrent;
using Fanasa.AccessManagement.Web.Domain.Access;

namespace Fanasa.AccessManagement.Web.Application.Access;

public interface IAccessManagement
{
    IReadOnlyCollection<Tenant> GetTenants();
    Tenant CreateTenant(string key, string displayName);
    IReadOnlyCollection<Product> GetProducts(string? centerSlug = null);
    Product RegisterProduct(RegisterProductRequest request);
    IReadOnlyCollection<ProductClient> GetClients(Guid productId);
    ProductClient RegisterClient(Guid productId, RegisterClientRequest request);
    IReadOnlyCollection<ContractEntitlement> GetEntitlements(Guid tenantId);
    ContractEntitlement ApplyContractEvent(ContractLifecycleEvent message);
    UsageEvent RecordUsage(RecordUsageCommand command);
    IReadOnlyCollection<UsageSummary> GetUsage(Guid tenantId, DateTimeOffset from, DateTimeOffset to);
    PricingPlan RegisterPlan(RegisterPlanRequest request);
    IReadOnlyCollection<PricingPlan> GetPlans(string? productKey = null);
    PricingRule AddPricingRule(Guid planId, AddPricingRuleRequest request);
    AccountBalance GetBalance(Guid tenantId, string accountType = "consumer");
    UsageCharge CalculateCharge(Guid tenantId, string productKey, string metric, decimal quantity, string idempotencyKey);
    RevenueSnapshot GetRevenue(string productKey, DateTimeOffset from, DateTimeOffset to);
    TenantSubscription Subscribe(CreateSubscriptionRequest request);
    IReadOnlyCollection<TenantSubscription> GetSubscriptions(Guid tenantId);
    TenantUser AddUser(AddTenantUserRequest request);
    IReadOnlyCollection<TenantUser> GetUsers(Guid tenantId);
    AccessRole CreateRole(CreateRoleRequest request);
    IReadOnlyCollection<AccessRole> GetRoles(Guid tenantId, string? productKey = null);
}

// Development adapter. The production adapter will be backed by Neo persistence and migrations.
public sealed class InMemoryAccessManagement : IAccessManagement
{
    private readonly ConcurrentDictionary<Guid, Tenant> tenants = new();
    private readonly ConcurrentDictionary<string, Product> products = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, ProductClient> clients = new();
    private readonly ConcurrentDictionary<string, ContractEntitlement> entitlements = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, UsageEvent> usage = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, PricingPlan> plans = new();
    private readonly ConcurrentDictionary<Guid, PricingRule> rules = new();
    private readonly ConcurrentDictionary<(Guid TenantId, string Type), AccountBalance> balances = new();
    private readonly ConcurrentDictionary<string, UsageCharge> charges = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, TenantSubscription> subscriptions = new();
    private readonly ConcurrentDictionary<Guid, TenantUser> users = new();
    private readonly ConcurrentDictionary<Guid, AccessRole> roles = new();

    private readonly Fanasa.AccessManagement.Web.Platform.PlatformRegistry? registry;
    public InMemoryAccessManagement(Fanasa.AccessManagement.Web.Platform.PlatformRegistry? registry = null)
    {
        this.registry = registry;
        tenants[Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")] = new(Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), "fanasa-internal", "سازمان فن‌آسا", true, DateTimeOffset.UtcNow);
    }
    public IReadOnlyCollection<Tenant> GetTenants() => tenants.Values.OrderBy(x => x.DisplayName).ToArray();
    public Tenant CreateTenant(string key, string displayName)
    {
        if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(displayName)) throw new ArgumentException("Tenant key and display name are required.");
        if (tenants.Values.Any(x => string.Equals(x.Key, key, StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("A tenant with this key already exists.");
        var item = new Tenant(Guid.NewGuid(), key.Trim(), displayName.Trim(), true, DateTimeOffset.UtcNow); tenants[item.Id] = item; return item;
    }
    public IReadOnlyCollection<Product> GetProducts(string? centerSlug = null) => (registry is null ? products.Values.AsEnumerable() : registry.Products().Select(Fanasa.AccessManagement.Web.Platform.PlatformRegistry.Domain)).Where(x => string.IsNullOrWhiteSpace(centerSlug) || x.CenterSlug.Equals(centerSlug, StringComparison.OrdinalIgnoreCase)).OrderBy(x => x.DisplayName).ToArray();
    public Product RegisterProduct(RegisterProductRequest request)
    {
        if (registry is not null) return registry.RegisterLegacy(request);
        if (string.IsNullOrWhiteSpace(request.Key) || string.IsNullOrWhiteSpace(request.DisplayName) || string.IsNullOrWhiteSpace(request.CenterSlug)) throw new ArgumentException("Product key, display name and center are required.");
        if (!CapabilityCatalog.All.Any(x => x.Name == request.CenterSlug || x.Audience == request.CenterSlug)) throw new ArgumentException("Center must be one of the official Fanasa capability centers.");
        if (products.ContainsKey(request.Key)) throw new InvalidOperationException("A product with this key already exists.");
        var center = CapabilityCatalog.All.First(x => x.Name == request.CenterSlug || x.Audience == request.CenterSlug);
        var item = new Product(Guid.NewGuid(), request.Key.Trim(), request.DisplayName.Trim(), request.Audience.Trim(), request.RepositoryUrl, center.Audience, request.Attributes ?? new Dictionary<string, string>()); products[item.Key] = item; return item;
    }
    public IReadOnlyCollection<ProductClient> GetClients(Guid productId) => clients.Values.Where(x => x.ProductId == productId).OrderBy(x => x.DisplayName).ToArray();
    public ProductClient RegisterClient(Guid productId, RegisterClientRequest request)
    {
        if (!GetProducts().Any(x => x.Id == productId)) throw new KeyNotFoundException("Product was not found.");
        if (string.IsNullOrWhiteSpace(request.Key) || string.IsNullOrWhiteSpace(request.DisplayName)) throw new ArgumentException("Client key and display name are required.");
        var item = new ProductClient(Guid.NewGuid(), productId, request.Key.Trim(), request.DisplayName.Trim(), request.ClientType, request.RedirectUris ?? [], request.AllowedScopes ?? [], true); clients[item.Id] = item; return item;
    }
    public IReadOnlyCollection<ContractEntitlement> GetEntitlements(Guid tenantId) => entitlements.Values.Where(x => x.TenantId == tenantId).OrderByDescending(x => x.UpdatedAt).ToArray();
    public ContractEntitlement ApplyContractEvent(ContractLifecycleEvent message)
    {
        if (!tenants.ContainsKey(message.TenantId)) throw new KeyNotFoundException("Tenant was not found.");
        var key = $"{message.ContractId}:{message.ProductKey}:{message.CenterSlug}";
        var status = message.EventType switch { "ContractActivated" or "ContractRenewed" => "active", "ContractSuspended" => "suspended", "ContractExpired" => "expired", _ => throw new ArgumentException($"Unsupported contract event: {message.EventType}") };
        var current = entitlements.TryGetValue(key, out var old) ? old : null;
        var item = new ContractEntitlement(current?.Id ?? Guid.NewGuid(), message.TenantId, message.ContractId, message.ProductKey, message.CenterSlug, message.PlanKey, message.IncludedQuantity, message.Unit, message.EffectiveAt, null, status, DateTimeOffset.UtcNow); entitlements[key] = item; return item;
    }
    public UsageEvent RecordUsage(RecordUsageCommand command)
    {
        if (!tenants.ContainsKey(command.TenantId)) throw new KeyNotFoundException("Tenant was not found.");
        if (command.Quantity < 0 || string.IsNullOrWhiteSpace(command.IdempotencyKey)) throw new ArgumentException("Quantity must be non-negative and idempotency key is required.");
        if (usage.TryGetValue(command.IdempotencyKey, out var old)) return old;
        var item = new UsageEvent(Guid.NewGuid(), command.TenantId, command.ProductKey, command.CenterSlug, command.Metric, command.Quantity, command.Unit, command.OccurredAt, command.Source, command.IdempotencyKey, command.CorrelationId, DateTimeOffset.UtcNow); usage[command.IdempotencyKey] = item; return item;
    }
    public IReadOnlyCollection<UsageSummary> GetUsage(Guid tenantId, DateTimeOffset from, DateTimeOffset to) => usage.Values.Where(x => x.TenantId == tenantId && x.OccurredAt >= from && x.OccurredAt <= to).GroupBy(x => new { x.TenantId, x.ProductKey, x.CenterSlug, x.Metric, x.Unit }).Select(g => new UsageSummary(g.Key.TenantId, g.Key.ProductKey, g.Key.CenterSlug, g.Key.Metric, g.Sum(x => x.Quantity), g.Key.Unit, g.Count(), from, to)).ToArray();
    public PricingPlan RegisterPlan(RegisterPlanRequest request) { if (!GetProducts().Any(x => x.Key.Equals(request.ProductKey, StringComparison.OrdinalIgnoreCase))) throw new KeyNotFoundException("Product was not found."); var item = new PricingPlan(Guid.NewGuid(), request.ProductKey, request.Key, request.DisplayName, request.BillingMode, request.Currency, request.FixedMonthlyAmount, request.IncludedCredit, true); plans[item.Id] = item; return item; }
    public IReadOnlyCollection<PricingPlan> GetPlans(string? productKey = null) => plans.Values.Where(x => string.IsNullOrWhiteSpace(productKey) || x.ProductKey.Equals(productKey, StringComparison.OrdinalIgnoreCase)).OrderBy(x => x.DisplayName).ToArray();
    public PricingRule AddPricingRule(Guid planId, AddPricingRuleRequest request) { if (!plans.ContainsKey(planId)) throw new KeyNotFoundException("Pricing plan was not found."); var item = new PricingRule(Guid.NewGuid(), planId, request.Metric, request.Unit, request.UnitPrice, request.IncludedQuantity, request.MaximumQuantity); rules[item.Id] = item; return item; }
    public AccountBalance GetBalance(Guid tenantId, string accountType = "consumer") => balances.GetOrAdd((tenantId, accountType), key => new AccountBalance(key.TenantId, key.Type, 0, 0, "IRR", DateTimeOffset.UtcNow));
    public UsageCharge CalculateCharge(Guid tenantId, string productKey, string metric, decimal quantity, string idempotencyKey)
    {
        if (charges.TryGetValue(idempotencyKey, out var existing)) return existing;
        var plan = plans.Values.FirstOrDefault(x => x.ProductKey.Equals(productKey, StringComparison.OrdinalIgnoreCase) && x.IsActive) ?? throw new InvalidOperationException("No active pricing plan exists for this product.");
        var rule = rules.Values.FirstOrDefault(x => x.PlanId == plan.Id && x.Metric.Equals(metric, StringComparison.OrdinalIgnoreCase)) ?? throw new InvalidOperationException("No pricing rule exists for this metric.");
        var billable = Math.Max(0, quantity - rule.IncludedQuantity); var amount = billable * rule.UnitPrice; var charge = new UsageCharge(Guid.NewGuid(), tenantId, productKey, metric, quantity, amount, plan.Currency, idempotencyKey, DateTimeOffset.UtcNow); charges[idempotencyKey] = charge; return charge;
    }
    public RevenueSnapshot GetRevenue(string productKey, DateTimeOffset from, DateTimeOffset to) { var values = charges.Values.Where(x => x.ProductKey.Equals(productKey, StringComparison.OrdinalIgnoreCase) && x.CalculatedAt >= from && x.CalculatedAt <= to).ToArray(); var gross = values.Sum(x => x.Amount); return new RevenueSnapshot(productKey, values.FirstOrDefault()?.Currency ?? "IRR", gross, gross * .7m, gross * .3m, values.Select(x => x.TenantId).Distinct().Count(), from, to); }
    public TenantSubscription Subscribe(CreateSubscriptionRequest request) { if (!tenants.ContainsKey(request.TenantId)) throw new KeyNotFoundException("Tenant was not found."); if (!plans.TryGetValue(request.PricingPlanId, out var plan) || !plan.ProductKey.Equals(request.ProductKey, StringComparison.OrdinalIgnoreCase)) throw new KeyNotFoundException("Pricing plan was not found for this product."); var item = new TenantSubscription(Guid.NewGuid(), request.TenantId, request.ProductKey, request.PricingPlanId, "active", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMonths(1), request.SeatLimit, 0); subscriptions[item.Id] = item; return item; }
    public IReadOnlyCollection<TenantSubscription> GetSubscriptions(Guid tenantId) => subscriptions.Values.Where(x => x.TenantId == tenantId).ToArray();
    public TenantUser AddUser(AddTenantUserRequest request) { if (!tenants.ContainsKey(request.TenantId)) throw new KeyNotFoundException("Tenant was not found."); if (users.Values.Any(x => x.TenantId == request.TenantId && x.KeycloakSubject == request.KeycloakSubject)) throw new InvalidOperationException("User is already a member of this tenant."); var item = new TenantUser(Guid.NewGuid(), request.TenantId, request.KeycloakSubject, request.DisplayName, true, DateTimeOffset.UtcNow); users[item.Id] = item; return item; }
    public IReadOnlyCollection<TenantUser> GetUsers(Guid tenantId) => users.Values.Where(x => x.TenantId == tenantId).OrderBy(x => x.DisplayName).ToArray();
    public AccessRole CreateRole(CreateRoleRequest request) { if (!tenants.ContainsKey(request.TenantId)) throw new KeyNotFoundException("Tenant was not found."); var item = new AccessRole(Guid.NewGuid(), request.TenantId, request.ProductKey, request.Key, request.DisplayName, request.Permissions.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()); roles[item.Id] = item; return item; }
    public IReadOnlyCollection<AccessRole> GetRoles(Guid tenantId, string? productKey = null) => roles.Values.Where(x => x.TenantId == tenantId && (string.IsNullOrWhiteSpace(productKey) || x.ProductKey.Equals(productKey, StringComparison.OrdinalIgnoreCase))).ToArray();
}
