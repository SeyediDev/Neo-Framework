using Fanasa.AccessManagement.Web.Application.Access;
using Fanasa.AccessManagement.Web.Domain.Access;
using Microsoft.AspNetCore.Mvc;

namespace Fanasa.AccessManagement.Web.Api;

[ApiController, Route("api/access")]
public sealed class AccessApi(IAccessManagement access) : ControllerBase
{
    [HttpGet("catalog/products")] public ActionResult<IReadOnlyCollection<Product>> Products([FromQuery] string? center) => Ok(access.GetProducts(center));
    [HttpPost("catalog/products")] public ActionResult<Product> RegisterProduct(RegisterProductRequest request) { try { var x = access.RegisterProduct(request); return Created($"/api/access/catalog/products/{x.Key}", x); } catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); } catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); } }
    [HttpGet("catalog/products/{productId:guid}/clients")] public ActionResult<IReadOnlyCollection<ProductClient>> Clients(Guid productId) => Ok(access.GetClients(productId));
    [HttpPost("catalog/products/{productId:guid}/clients")] public ActionResult<ProductClient> RegisterClient(Guid productId, RegisterClientRequest request) { try { return Ok(access.RegisterClient(productId, request)); } catch (KeyNotFoundException ex) { return NotFound(new { error = ex.Message }); } catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); } }
    [HttpGet("tenants")] public ActionResult<IReadOnlyCollection<Tenant>> Tenants() => Ok(access.GetTenants());
    [HttpPost("tenants")] public ActionResult<Tenant> CreateTenant(CreateTenantRequest request) { try { return Ok(access.CreateTenant(request.Key, request.DisplayName)); } catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); } catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); } }
    [HttpGet("tenants/{tenantId:guid}/entitlements")] public ActionResult<IReadOnlyCollection<ContractEntitlement>> Entitlements(Guid tenantId) => Ok(access.GetEntitlements(tenantId));
    [HttpPost("contract-events")] public ActionResult<ContractEntitlement> ContractEvent(ContractLifecycleEvent request) { try { return Ok(access.ApplyContractEvent(request)); } catch (KeyNotFoundException ex) { return NotFound(new { error = ex.Message }); } catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); } }
    [HttpPost("usage-events")] public ActionResult<UsageEvent> Usage(RecordUsageCommand request) { try { return Ok(access.RecordUsage(request)); } catch (KeyNotFoundException ex) { return NotFound(new { error = ex.Message }); } catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); } }
    [HttpGet("tenants/{tenantId:guid}/usage")] public ActionResult<IReadOnlyCollection<UsageSummary>> UsageSummary(Guid tenantId, [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to) { var end = to ?? DateTimeOffset.UtcNow; return Ok(access.GetUsage(tenantId, from ?? end.AddDays(-30), end)); }
    [HttpGet("catalog/plans")] public ActionResult<IReadOnlyCollection<PricingPlan>> Plans([FromQuery] string? productKey) => Ok(access.GetPlans(productKey));
    [HttpPost("catalog/plans")] public ActionResult<PricingPlan> RegisterPlan(RegisterPlanRequest request) { try { return Ok(access.RegisterPlan(request)); } catch (KeyNotFoundException ex) { return NotFound(new { error = ex.Message }); } }
    [HttpPost("catalog/plans/{planId:guid}/rules")] public ActionResult<PricingRule> AddRule(Guid planId, AddPricingRuleRequest request) { try { return Ok(access.AddPricingRule(planId, request)); } catch (KeyNotFoundException ex) { return NotFound(new { error = ex.Message }); } }
    [HttpGet("tenants/{tenantId:guid}/balance")] public ActionResult<AccountBalance> Balance(Guid tenantId, [FromQuery] string accountType = "consumer") => Ok(access.GetBalance(tenantId, accountType));
    [HttpPost("charges") ] public ActionResult<UsageCharge> Charge(Guid tenantId, string productKey, string metric, decimal quantity, string idempotencyKey) { try { return Ok(access.CalculateCharge(tenantId, productKey, metric, quantity, idempotencyKey)); } catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); } }
    [HttpGet("catalog/products/{productKey}/revenue")] public ActionResult<RevenueSnapshot> Revenue(string productKey, [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to) { var end = to ?? DateTimeOffset.UtcNow; return Ok(access.GetRevenue(productKey, from ?? end.AddDays(-30), end)); }
    [HttpPost("subscriptions")] public ActionResult<TenantSubscription> Subscribe(CreateSubscriptionRequest request) { try { return Ok(access.Subscribe(request)); } catch (KeyNotFoundException ex) { return NotFound(new { error = ex.Message }); } }
    [HttpGet("tenants/{tenantId:guid}/subscriptions")] public ActionResult<IReadOnlyCollection<TenantSubscription>> Subscriptions(Guid tenantId) => Ok(access.GetSubscriptions(tenantId));
    [HttpPost("users")] public ActionResult<TenantUser> AddUser(AddTenantUserRequest request) { try { return Ok(access.AddUser(request)); } catch (KeyNotFoundException ex) { return NotFound(new { error = ex.Message }); } catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); } }
    [HttpGet("tenants/{tenantId:guid}/users")] public ActionResult<IReadOnlyCollection<TenantUser>> Users(Guid tenantId) => Ok(access.GetUsers(tenantId));
    [HttpPost("roles")] public ActionResult<AccessRole> CreateRole(CreateRoleRequest request) { try { return Ok(access.CreateRole(request)); } catch (KeyNotFoundException ex) { return NotFound(new { error = ex.Message }); } }
    [HttpGet("tenants/{tenantId:guid}/roles")] public ActionResult<IReadOnlyCollection<AccessRole>> Roles(Guid tenantId, [FromQuery] string? productKey) => Ok(access.GetRoles(tenantId, productKey));
}
public sealed record CreateTenantRequest(string Key, string DisplayName);
