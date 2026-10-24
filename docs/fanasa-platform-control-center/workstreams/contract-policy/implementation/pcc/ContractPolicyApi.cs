using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Fanasa.AccessManagement.Web.Security;
using Fanasa.ContractPolicy;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fanasa.AccessManagement.Web.ContractPolicies;

[ApiController, Route("api/contract-policy/projection-events")]
[Authorize(AuthenticationSchemes = ContractProducerSecurity.Scheme)]
public sealed class ContractProjectionIngressApi(ContractPolicyStore store, ContractProducerOptions settings,
    ContractProducerAuthorization authorization, ContractGatewayPublicationService publications) : ControllerBase
{
    [HttpPost, RequestSizeLimit(262_144)]
    public async Task<IActionResult> Accept(CancellationToken cancellationToken)
    {
        if (!authorization.Allows(User) || settings.Binding is not { } binding) return Forbid(ContractProducerSecurity.Scheme);
        var buffer = new char[ContractEventAdapter.MaximumPayloadCharacters + 1];
        var count = 0;
        try
        {
            using var reader = new StreamReader(Request.Body, new UTF8Encoding(false, true), false, 4096, leaveOpen: true);
            while (count < buffer.Length)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(count), cancellationToken);
                if (read == 0) break;
                count += read;
            }
        }
        catch (DecoderFallbackException) { return BadRequest(new { error = "invalid-event-json" }); }
        if (count > ContractEventAdapter.MaximumPayloadCharacters) return StatusCode(413);
        var result = store.Apply(new string(buffer, 0, count), User, binding, () => authorization.Allows(User));
        if (result.Errors.Contains("producer-not-authorized", StringComparer.Ordinal)) return Forbid(ContractProducerSecurity.Scheme);
        if (result.Status is "applied" or "duplicate" or "stale")
        {
            var accepted = ContractJson.Parse(new string(buffer, 0, count))!.Deserialize<ContractProjectionEvent>(ContractJson.Options)!;
            var ack = result.Status == "stale" ? null : await publications.ObserveAsync(
                accepted.Projection.ContractId, accepted.EventId, () => authorization.Allows(User), cancellationToken);
            if (!authorization.Allows(User)) return Forbid(ContractProducerSecurity.Scheme);
            Response.Headers.CacheControl = "no-store";
            if (ack is not null) return Ok(ack);
            return Accepted(new { result.Status, result.State, result.Errors, accepted.EventId,
                accepted.AggregateVersion, Phase = result.Status == "stale" ? "superseded" : "pending" });
        }
        return result.Errors.Contains("invalid-event-json", StringComparer.Ordinal) ? BadRequest(result) : Conflict(result);
    }
}

[ApiController, Route("api/contract-policy/tenants/{tenantId:guid}/organization.unit.save/evaluate")]
[Authorize(AuthenticationSchemes = OrganizationBearerSecurity.Scheme)]
public sealed class ContractPolicyDecisionApi(OrganizationContractPolicyDecisionService decisions) : ControllerBase
{
    [HttpPost]
    public async Task<IActionResult> Evaluate(Guid tenantId, CancellationToken cancellationToken)
    {
        Response.Headers.CacheControl = "no-store";
        // The route and authenticated principal are sufficient. Caller-supplied PIP contexts are rejected.
        if (Request.ContentLength is > 0 || Request.Headers.ContainsKey("Transfer-Encoding"))
            return BadRequest(new { error = "This endpoint accepts no request attributes." });
        return Ok(await decisions.EvaluateAsync(tenantId, User, "r-" + Guid.NewGuid().ToString("N"), cancellationToken));
    }
}

public sealed record PublishContractPolicyCommand(long ExpectedRevision, ContractPolicyBinding Binding, string Reason);
public sealed record RetireContractPolicyCommand(long ExpectedRevision, string Reason);
public sealed record AssignContractProductRoleCommand(string Subject, Guid RoleId, bool Active,
    DateTimeOffset ValidFrom, DateTimeOffset ValidTo, long ExpectedRevision, string Reason);

[ApiController, Route("api/contract-policy/administration/tenants/{tenantId:guid}")]
[Authorize(AuthenticationSchemes = CookieAuthenticationDefaults.AuthenticationScheme, Policy = "PlatformAdmin")]
public sealed class ContractPolicyAdministrationApi(ContractPolicyStore store) : ControllerBase
{
    [HttpGet("policy")]
    public IActionResult Policy(Guid tenantId) => Ok(store.ReadPolicy(tenantId));

    [HttpGet("role-assignments/{subject}")]
    public IActionResult Assignment(Guid tenantId, string subject) => Ok(store.ReadRoleAssignment(tenantId, subject));

    [HttpPost("policy"), ValidateAntiForgeryToken]
    public IActionResult Publish(Guid tenantId, PublishContractPolicyCommand command) => Execute(() =>
        store.PublishPolicy(tenantId, command.Binding, command.ExpectedRevision, Actor(), command.Reason));

    [HttpPost("policy/retire"), ValidateAntiForgeryToken]
    public IActionResult Retire(Guid tenantId, RetireContractPolicyCommand command) => Execute(() =>
        store.RetirePolicy(tenantId, command.ExpectedRevision, Actor(), command.Reason));

    [HttpPost("role-assignments"), ValidateAntiForgeryToken]
    public IActionResult Assign(Guid tenantId, AssignContractProductRoleCommand command) => Execute(() =>
        store.SaveRoleAssignment(tenantId, command.Subject, command.RoleId, command.Active, command.ValidFrom,
            command.ValidTo, command.ExpectedRevision, Actor(), command.Reason));

    private string Actor() => User.FindFirst("sub")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? throw new UnauthorizedAccessException();
    private IActionResult Execute<T>(Func<T> action)
    {
        try { return Ok(action()); }
        catch (UnauthorizedAccessException) { return Forbid(CookieAuthenticationDefaults.AuthenticationScheme); }
        catch (ArgumentException error) { return BadRequest(new { error = error.Message }); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (InvalidOperationException error) { return Conflict(new { error = error.Message }); }
    }
}
