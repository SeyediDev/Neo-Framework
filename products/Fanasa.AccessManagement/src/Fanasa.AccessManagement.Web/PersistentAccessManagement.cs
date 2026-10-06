using Fanasa.AccessManagement.Web.Domain.Access;
using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Accounting;
using Fanasa.AccessManagement.Web.Persistence;
using Fanasa.AccessManagement.Web.Organization;

namespace Fanasa.AccessManagement.Web.Application.Tenancy;

public sealed record TenantGrant(Guid TenantId, string Subject, string Permission, DateTimeOffset? ExpiresAt)
{
    public static readonly string[] Permissions = ["organization.read", "organization.write", "billing.read", "billing.write", "billing.meter", "tenancy.read", "tenancy.manage"];
}

/// <summary>Durable tenancy; product registration delegates to the existing central catalog.</summary>
public sealed class PersistentAccessManagement : IAccessManagement
{
    private readonly FabricDatabase db;
    private readonly IAccessManagement catalog;
    public PersistentAccessManagement(FabricDatabase db, IAccessManagement catalog)
    {
        this.db = db; this.catalog = catalog;
        db.Transaction(() => { foreach (var tenant in catalog.GetTenants()) if (db.Read<Tenant>("tenant", tenant.Id.ToString()) is null) db.Put("tenant", tenant.Id.ToString(), tenant); return true; });
    }
    private T[] All<T>(string kind) => db.All<T>(kind);
    private T Save<T>(string kind, Guid id, T value) { db.Put(kind, id.ToString(), value); return value; }
    private void TenantExists(Guid id) { if (!GetTenants().Any(x => x.Id == id && x.IsActive)) throw new KeyNotFoundException("Tenant not found."); }
    private void ProductExists(string key) { if (!GetProducts().Any(x => x.Key == key)) throw new KeyNotFoundException("Product not found."); }
    private static void Label(string? value, int maximum = 200) { if (string.IsNullOrWhiteSpace(value) || value.Length > maximum) throw new ArgumentException("A valid label is required."); }
    public T Audited<T>(Func<T> action, string actor, string reason, string operation) => db.Transaction(() =>
    {
        Label(actor); Label(reason, 1000); var result = action();
        db.Emit("tenancy:" + Guid.NewGuid(), operation, new { Actor = actor, Reason = reason, Result = result }, DateTimeOffset.UtcNow); return result;
    });
    public IReadOnlyCollection<Tenant> GetTenants() => All<Tenant>("tenant").OrderBy(x => x.DisplayName).ToArray();
    public TenantGrant[] GetGrants(string subject) => All<TenantGrant>("tenant-grant").Where(x => x.Subject == subject).ToArray();
    public void SetGrant(TenantGrant grant, string actor, string reason) => db.Transaction(() =>
    {
        TenantExists(grant.TenantId); Label(actor); Label(reason, 1000);
        if (!GetUsers(grant.TenantId).Any(x => x.KeycloakSubject == grant.Subject && x.IsActive) || !TenantGrant.Permissions.Contains(grant.Permission)) throw new ArgumentException("Grant must target an active member and a supported permission.");
        db.Put("tenant-grant", $"{grant.TenantId}:{grant.Subject}:{grant.Permission}", grant);
        db.Emit("grant:" + Guid.NewGuid(), "TenantGrantChanged", new { grant.TenantId, grant.Subject, grant.Permission, grant.ExpiresAt, Actor = actor, Reason = reason }, DateTimeOffset.UtcNow);
        return true;
    });
    public TenantUser SetMembership(Guid tenant, Guid memberId, bool active, string actor, string reason) => db.Transaction(() =>
    {
        TenantExists(tenant); Label(actor); Label(reason, 1000);
        var member = GetUsers(tenant).SingleOrDefault(x => x.Id == memberId) ?? throw new KeyNotFoundException();
        var seats = GetUsers(tenant).Count(x => x.IsActive && x.Id != memberId) + (active ? 1 : 0);
        var subscriptions = GetSubscriptions(tenant).ToArray();
        if (subscriptions.Any(x => x.Status == "active" && (!x.RenewsAt.HasValue || x.RenewsAt > DateTimeOffset.UtcNow) && seats > x.SeatLimit)) throw new InvalidOperationException("Subscription seat limit reached.");
        member = member with { IsActive = active }; Save("membership", member.Id, member);
        foreach (var subscription in subscriptions) Save("subscription", subscription.Id, subscription with { ActiveSeats = seats });
        if (!active)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var grant in GetGrants(member.KeycloakSubject).Where(x => x.TenantId == tenant))
                db.Put("tenant-grant", $"{tenant}:{grant.Subject}:{grant.Permission}", grant with { ExpiresAt = now });
            if (db.Read<OrgState>("organization", tenant.ToString()) is { } organization && organization.Appointments.Any(x => x.Subject == member.KeycloakSubject && (!x.To.HasValue || x.To > now)))
            {
                var appointments = organization.Appointments.Where(x => x.Subject != member.KeycloakSubject || x.From < now)
                    .Select(x => x.Subject == member.KeycloakSubject && (!x.To.HasValue || x.To > now) ? x with { To = now } : x).ToArray();
                var change = new OrgChange(Guid.NewGuid(), organization.Revision + 1, actor, reason, now, "membership.offboard");
                organization = organization with { Revision = change.Revision, Appointments = appointments, Changes = [..organization.Changes, change] };
                db.Put("organization", tenant.ToString(), organization); db.Put("organization.history", $"{tenant}:{organization.Revision}", organization);
                db.Emit($"organization:{tenant}:{organization.Revision}", "OrganizationChanged", new { TenantId = tenant, organization.Revision, Operation = change.Operation, Actor = actor }, now);
            }
        }
        db.Emit("membership:" + Guid.NewGuid(), "TenantMembershipChanged", new { TenantId = tenant, member.Id, Active = active, Actor = actor, Reason = reason }, DateTimeOffset.UtcNow);
        return member;
    });
    public TenantSubscription SetSubscription(Guid tenant, Guid subscriptionId, string status, DateTimeOffset? renewsAt, string actor, string reason) => db.Transaction(() =>
    {
        TenantExists(tenant); Label(actor); Label(reason, 1000);
        if (status is not ("active" or "suspended" or "expired") || (status == "active" && (!renewsAt.HasValue || renewsAt <= DateTimeOffset.UtcNow))) throw new ArgumentException("Active subscriptions require a future renewal date.");
        var item = GetSubscriptions(tenant).SingleOrDefault(x => x.Id == subscriptionId) ?? throw new KeyNotFoundException();
        if (status == "active" && GetSubscriptions(tenant).Any(x => x.Id != item.Id && x.ProductKey == item.ProductKey && x.Status == "active" && (!x.RenewsAt.HasValue || x.RenewsAt > DateTimeOffset.UtcNow))) throw new InvalidOperationException("Another active subscription exists.");
        var seats = GetUsers(tenant).Count(x => x.IsActive);
        if (seats > item.SeatLimit) throw new InvalidOperationException("Seat limit exceeded.");
        item = item with { Status = status, RenewsAt = renewsAt, ActiveSeats = seats }; Save("subscription", item.Id, item);
        db.Emit("subscription:" + Guid.NewGuid(), "TenantSubscriptionChanged", new { TenantId = tenant, item.Id, Status = status, RenewsAt = renewsAt, Actor = actor, Reason = reason }, DateTimeOffset.UtcNow);
        return item;
    });
    public Tenant CreateTenant(string key, string displayName) => db.Transaction(() =>
    {
        Label(key, 80); Label(displayName);
        if (GetTenants().Any(x => x.Key.Equals(key.Trim(), StringComparison.OrdinalIgnoreCase))) throw new InvalidOperationException("Tenant key already exists.");
        var item = new Tenant(Guid.NewGuid(), key.Trim(), displayName.Trim(), true, DateTimeOffset.UtcNow);
        return Save("tenant", item.Id, item);
    });
    public IReadOnlyCollection<Product> GetProducts(string? centerSlug = null) => catalog.GetProducts(centerSlug);
    public Product RegisterProduct(RegisterProductRequest request) => catalog.RegisterProduct(request);
    public IReadOnlyCollection<ProductClient> GetClients(Guid productId) => All<ProductClient>("client").Where(x => x.ProductId == productId).ToArray();
    public ProductClient RegisterClient(Guid productId, RegisterClientRequest request) => db.Transaction(() =>
    {
        if (!GetProducts().Any(x => x.Id == productId)) throw new KeyNotFoundException();
        Label(request.Key, 80); Label(request.DisplayName);
        if (GetClients(productId).Any(x => x.Key == request.Key)) throw new InvalidOperationException("Client key already exists.");
        var redirects = request.RedirectUris ?? [];
        if (redirects.Any(x => !Uri.TryCreate(x, UriKind.Absolute, out var uri) || uri.Scheme != "https" || !string.IsNullOrEmpty(uri.UserInfo))) throw new ArgumentException("Exact HTTPS redirect URLs are required.");
        var item = new ProductClient(Guid.NewGuid(), productId, request.Key.Trim(), request.DisplayName.Trim(), request.ClientType, redirects, request.AllowedScopes ?? [], true);
        return Save("client", item.Id, item);
    });
    public IReadOnlyCollection<PricingPlan> GetPlans(string? productKey = null) => All<PricingPlan>("plan").Where(x => productKey is null || x.ProductKey == productKey).ToArray();
    public PricingPlan RegisterPlan(RegisterPlanRequest request) => db.Transaction(() =>
    {
        ProductExists(request.ProductKey); Label(request.Key, 80); Label(request.DisplayName);
        if (request.BillingMode is not ("payg" or "postpaid") || request.Currency is not ("IRR" or "USD" or "EUR") || request.FixedMonthlyAmount < 0 || request.IncludedCredit < 0) throw new ArgumentException("Invalid plan pricing.");
        if (request.FixedMonthlyAmount != 0 || request.IncludedCredit != 0) throw new ArgumentException("Base fees and included billing credits are not implemented; use metered tariffs.");
        if (GetPlans(request.ProductKey).Any(x => x.Key == request.Key)) throw new InvalidOperationException("Plan key already exists.");
        var item = new PricingPlan(Guid.NewGuid(), request.ProductKey, request.Key.Trim(), request.DisplayName.Trim(), request.BillingMode, request.Currency, request.FixedMonthlyAmount, request.IncludedCredit, true);
        return Save("plan", item.Id, item);
    });
    public PricingRule AddPricingRule(Guid planId, AddPricingRuleRequest request) => db.Transaction(() =>
    {
        if (!GetPlans().Any(x => x.Id == planId && x.IsActive)) throw new KeyNotFoundException();
        Label(request.Metric, 100); Label(request.Unit, 50);
        if (request.UnitPrice < 0 || request.IncludedQuantity < 0 || request.MaximumQuantity < request.IncludedQuantity) throw new ArgumentException("Invalid pricing rule.");
        if (All<PricingRule>("rule").Any(x => x.PlanId == planId && x.Metric == request.Metric)) throw new InvalidOperationException("Metric already has a rule.");
        var item = new PricingRule(Guid.NewGuid(), planId, request.Metric, request.Unit, request.UnitPrice, request.IncludedQuantity, request.MaximumQuantity);
        return Save("rule", item.Id, item);
    });
    public IReadOnlyCollection<TenantSubscription> GetSubscriptions(Guid tenantId) => All<TenantSubscription>("subscription").Where(x => x.TenantId == tenantId).ToArray();
    public TenantSubscription Subscribe(CreateSubscriptionRequest request) => db.Transaction(() =>
    {
        TenantExists(request.TenantId);
        if (request.SeatLimit < 1 || request.SeatLimit > 100000) throw new ArgumentException("Seat limit must be positive.");
        var plan = GetPlans(request.ProductKey).SingleOrDefault(x => x.Id == request.PricingPlanId && x.IsActive) ?? throw new KeyNotFoundException("Active plan not found for this product.");
        if (db.Read<BillingAccount>("accounting", request.TenantId.ToString()) is { } account && (account.Mode != plan.BillingMode || account.Currency != plan.Currency)) throw new ArgumentException("Subscription plan must match the tenant accounting mode and currency.");
        if (GetSubscriptions(request.TenantId).Any(x => x.ProductKey == request.ProductKey && x.Status == "active" && (!x.RenewsAt.HasValue || x.RenewsAt > DateTimeOffset.UtcNow))) throw new InvalidOperationException("An active subscription already exists.");
        var seats = GetUsers(request.TenantId).Count(x => x.IsActive);
        if (seats > request.SeatLimit) throw new InvalidOperationException("Existing membership exceeds the seat limit.");
        var now = DateTimeOffset.UtcNow;
        var item = new TenantSubscription(Guid.NewGuid(), request.TenantId, request.ProductKey, request.PricingPlanId, "active", now, now.AddMonths(1), request.SeatLimit, seats);
        return Save("subscription", item.Id, item);
    });
    public IReadOnlyCollection<TenantUser> GetUsers(Guid tenantId) => All<TenantUser>("membership").Where(x => x.TenantId == tenantId).ToArray();
    public TenantUser AddUser(AddTenantUserRequest request) => db.Transaction(() =>
    {
        TenantExists(request.TenantId); Label(request.KeycloakSubject, 200);
        if (GetUsers(request.TenantId).Any(x => x.KeycloakSubject == request.KeycloakSubject)) throw new InvalidOperationException("Membership already exists.");
        var seats = GetUsers(request.TenantId).Count(x => x.IsActive) + 1;
        var subscriptions = GetSubscriptions(request.TenantId).ToArray();
        if (subscriptions.Any(x => x.Status == "active" && (!x.RenewsAt.HasValue || x.RenewsAt > DateTimeOffset.UtcNow) && seats > x.SeatLimit)) throw new InvalidOperationException("Subscription seat limit reached.");
        var item = new TenantUser(Guid.NewGuid(), request.TenantId, request.KeycloakSubject, request.DisplayName, true, DateTimeOffset.UtcNow);
        Save("membership", item.Id, item);
        foreach (var subscription in subscriptions) Save("subscription", subscription.Id, subscription with { ActiveSeats = seats });
        return item;
    });
    public IReadOnlyCollection<AccessRole> GetRoles(Guid tenantId, string? productKey = null) => All<AccessRole>("access-role").Where(x => x.TenantId == tenantId && (productKey is null || x.ProductKey == productKey)).ToArray();
    public AccessRole CreateRole(CreateRoleRequest request) => db.Transaction(() =>
    {
        TenantExists(request.TenantId); ProductExists(request.ProductKey); Label(request.Key, 80); Label(request.DisplayName);
        if (request.Permissions is null || request.Permissions.Count > 200 || request.Permissions.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("Invalid permissions.");
        if (GetRoles(request.TenantId, request.ProductKey).Any(x => x.Key == request.Key)) throw new InvalidOperationException("Role key already exists.");
        var item = new AccessRole(Guid.NewGuid(), request.TenantId, request.ProductKey, request.Key.Trim(), request.DisplayName.Trim(), request.Permissions.Distinct().ToArray());
        return Save("access-role", item.Id, item);
    });
    public IReadOnlyCollection<ContractEntitlement> GetEntitlements(Guid tenantId) => All<ContractEntitlement>("entitlement").Where(x => x.TenantId == tenantId).ToArray();
    public ContractEntitlement ApplyContractEvent(ContractLifecycleEvent message) => db.Transaction(() =>
    {
        TenantExists(message.TenantId); ProductExists(message.ProductKey); Label(message.EventId, 200);
        var eventKey = $"{message.TenantId}:{message.EventId}";
        var fingerprint = System.Text.Json.JsonSerializer.Serialize(message);
        var previous = db.Read<string>("contract.receipt", eventKey);
        if (previous is not null && previous != fingerprint) throw new InvalidOperationException("Contract event identity conflict.");
        var old = GetEntitlements(message.TenantId).SingleOrDefault(x => x.ExternalContractId == message.ContractId && x.ProductKey == message.ProductKey && x.CenterSlug == message.CenterSlug);
        if (previous is not null) return old ?? throw new InvalidDataException();
        if (old is not null && message.EffectiveAt < old.EffectiveFrom) throw new InvalidOperationException("Out-of-order contract event.");
        var status = message.EventType switch { "ContractActivated" or "ContractRenewed" => "active", "ContractSuspended" => "suspended", "ContractExpired" => "expired", _ => throw new ArgumentException("Invalid event type.") };
        var item = new ContractEntitlement(old?.Id ?? Guid.NewGuid(), message.TenantId, message.ContractId, message.ProductKey, message.CenterSlug, message.PlanKey, message.IncludedQuantity, message.Unit, message.EffectiveAt, null, status, DateTimeOffset.UtcNow);
        db.Put("contract.receipt", eventKey, fingerprint); return Save("entitlement", item.Id, item);
    });
    public UsageEvent RecordUsage(RecordUsageCommand command) => db.Transaction(() =>
    {
        TenantExists(command.TenantId); ProductExists(command.ProductKey); Label(command.IdempotencyKey, 200);
        if (command.Quantity < 0) throw new ArgumentException("Quantity must be nonnegative.");
        var key = $"{command.TenantId}:{command.IdempotencyKey}";
        var fingerprint = System.Text.Json.JsonSerializer.Serialize(command);
        var prior = db.Read<string>("usage.receipt", key);
        if (prior is not null && prior != fingerprint) throw new InvalidOperationException("Usage identity conflict.");
        if (prior is not null) return db.Read<UsageEvent>("usage", key) ?? throw new InvalidDataException();
        var item = new UsageEvent(Guid.NewGuid(), command.TenantId, command.ProductKey, command.CenterSlug, command.Metric, command.Quantity, command.Unit, command.OccurredAt, command.Source, command.IdempotencyKey, command.CorrelationId, DateTimeOffset.UtcNow);
        db.Put("usage.receipt", key, fingerprint); db.Put("usage", key, item); return item;
    });
    public IReadOnlyCollection<UsageSummary> GetUsage(Guid tenantId, DateTimeOffset from, DateTimeOffset to) => All<UsageEvent>("usage").Where(x => x.TenantId == tenantId && x.OccurredAt >= from && x.OccurredAt < to).GroupBy(x => new { x.ProductKey, x.CenterSlug, x.Metric, x.Unit }).Select(g => new UsageSummary(tenantId, g.Key.ProductKey, g.Key.CenterSlug, g.Key.Metric, g.Sum(x => x.Quantity), g.Key.Unit, g.Count(), from, to)).ToArray();
    public AccountBalance GetBalance(Guid tenantId, string accountType = "consumer")
    {
        TenantExists(tenantId);
        var view = new AccountingStore(".", this, database: db).Read(tenantId);
        return new(tenantId, accountType, view.Account.Mode == "payg" ? view.WalletBalance : view.AvailableCredit, 0, view.Account.Currency, DateTimeOffset.UtcNow);
    }
    public UsageCharge CalculateCharge(Guid tenantId, string productKey, string metric, decimal quantity, string idempotencyKey) => throw new InvalidOperationException("Use the transactional accounting usage endpoint; legacy charge calculation is disabled.");
    public RevenueSnapshot GetRevenue(string productKey, DateTimeOffset from, DateTimeOffset to)
    {
        var accounts = All<BillingAccount>("accounting");
        var amounts = accounts.SelectMany(a => a.Entries.Where(x => x.Kind == "usage" && x.ProductKey == productKey && x.At >= from && x.At < to).Select(x => new { a.TenantId, a.Currency, x.Amount })).ToArray();
        if (amounts.Select(x => x.Currency).Distinct().Count() > 1) throw new InvalidOperationException("Revenue across currencies requires explicit currency reporting.");
        var gross = amounts.Sum(x => x.Amount);
        return new(productKey, amounts.FirstOrDefault()?.Currency ?? "IRR", gross, 0, 0, amounts.Select(x => x.TenantId).Distinct().Count(), from, to);
    }
}
