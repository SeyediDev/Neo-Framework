using Fanasa.AccessManagement.Web.Governance;
using Fanasa.AccessManagement.Web.Organization;
using Fanasa.AccessManagement.Web.Security;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fanasa.AccessManagement.Web.Api;

[ApiController, Authorize(AuthenticationSchemes = CookieAuthenticationDefaults.AuthenticationScheme), Route("api/governance/tenants/{tenantId:guid}/pip")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class PipApi(PipStore store, PipSyncService sync, PipTransportOptions transport, TimeProvider clock,
    TemplateOrganizationStore? examples = null, PipEducationalExample? educational = null) : ControllerBase
{
    private bool Admin => User.Identity?.IsAuthenticated == true && User.HasClaim("permission", "platform.admin");
    private bool CanManage(Guid tenantId) => User.Identity?.IsAuthenticated == true && (Admin || TenantAuthorization.Allows(User, tenantId, "tenancy.manage"));
    private string Actor => OrgAuthorization.Subject(User);
    private IActionResult? Guard(Guid tenant) => !CanManage(tenant) ? Forbid() : TemplateOrganizationWriteGuard.Reject(examples, tenant);
    private IActionResult Execute(Func<object> operation)
    {
        try { return Ok(operation()); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException error) { return BadRequest(new { error = error.Message }); }
        catch (InvalidDataException error) { return BadRequest(new { error = error.Message }); }
        catch (InvalidOperationException error) { return Conflict(new { error = error.Message }); }
    }
    [HttpGet("sources")]
    public IActionResult Sources(Guid tenantId) => !CanManage(tenantId) ? Forbid() : Execute(() => store.ListSources(tenantId));

    [HttpPost("sources"), ValidateAntiForgeryToken]
    public IActionResult Register(Guid tenantId, RegisterPipSourceCommand command)
    {
        if (Guard(tenantId) is { } denied) return denied;
        return Execute(() =>
        {
            AccessPolicyStore.Label(command.Reason, 1000);
            if (command.Enabled) transport.Authorize(tenantId, command.Endpoint, command.SecretReference);
            return store.Register(tenantId, command.Name, command.Endpoint, command.AllowedHost, command.SecretReference,
                command.SchemaVersion, command.MaxAgeSeconds, command.TimeoutSeconds, command.MaxResponseBytes, command.Enabled,
                command.Definitions, command.SyncSubjects, command.PollIntervalSeconds, Actor, command.Reason);
        });
    }
    [HttpPost("sources/{sourceId:guid}/enabled"), ValidateAntiForgeryToken]
    public IActionResult Enable(Guid tenantId, Guid sourceId, PipEnableCommand command)
    {
        if (Guard(tenantId) is { } denied) return denied;
        return Execute(() =>
        {
            AccessPolicyStore.Label(command.Reason, 1000);
            var source = store.ReadSource(tenantId, sourceId);
            if (command.Enabled) transport.Authorize(source);
            return store.SetEnabled(tenantId, sourceId, command.Enabled, Actor, command.Reason);
        });
    }
    [HttpGet("sources/{sourceId:guid}/runs")]
    public IActionResult Runs(Guid tenantId, Guid sourceId, [FromQuery] int take = 50) => !CanManage(tenantId) ? Forbid() : Execute(() => store.ListRuns(tenantId, sourceId, take));

    [HttpGet("sources/{sourceId:guid}/status")]
    public IActionResult Status(Guid tenantId, Guid sourceId, [FromQuery] string subject)
    {
        if (!CanManage(tenantId)) return Forbid();
        return Execute(() =>
        {
            var source = store.ReadSource(tenantId, sourceId);
            var result = store.Read(tenantId, subject, clock.GetUtcNow(), sourceId);
            return new { result.Status, result.Error, result.Snapshot?.ObservedAt, result.Snapshot?.FreshUntil,
                result.Snapshot?.SourceVersion, result.Snapshot?.ContentHash, deploymentApproved = source.Educational || transport.IsAuthorized(source),
                checkpoint = store.Checkpoint(tenantId, sourceId, subject) };
        });
    }
    [HttpPost("sources/{sourceId:guid}/sync"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Sync(Guid tenantId, Guid sourceId, PipSyncCommand command, CancellationToken cancellationToken)
    {
        if (Guard(tenantId) is { } denied) return denied;
        try
        {
            AccessPolicyStore.Label(command.Reason, 1000);
            return Ok(await sync.SyncAsync(tenantId, sourceId, command.Subject, cancellationToken, Actor, command.Reason));
        }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException error) { return BadRequest(new { error = error.Message }); }
        catch (InvalidOperationException error) { return Conflict(new { error = error.Message }); }
    }
    [HttpPost("example"), ValidateAntiForgeryToken]
    public IActionResult Example(Guid tenantId, PipExampleCommand command)
    {
        if (!Admin) return Forbid();
        return Execute(() => (educational ?? throw new InvalidOperationException("PIP educational service is unavailable.")).Ensure(tenantId, Actor, command.Reason));
    }
}
public sealed record RegisterPipSourceCommand(string Name, string Endpoint, string AllowedHost, string? SecretReference,
    int SchemaVersion = 1, int MaxAgeSeconds = 300, int TimeoutSeconds = 10, int MaxResponseBytes = 262144,
    bool Enabled = false, PipAttributeDefinition[]? Definitions = null, string[]? SyncSubjects = null, int PollIntervalSeconds = 0, string Reason = "");
public sealed record PipEnableCommand(bool Enabled, string Reason = "");
public sealed record PipSyncCommand(string Subject, string Reason = "");
public sealed record PipExampleCommand(string Reason);