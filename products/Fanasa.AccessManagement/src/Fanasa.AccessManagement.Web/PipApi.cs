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
    TemplateOrganizationStore? examples = null, PipEducationalExample? educational = null,
    PipDiagnostics? diagnostics = null, PipEducationalJourney? journey = null) : ControllerBase
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
        catch (PipSourceSettingsConflictException error) { return Conflict(new { errorCode = error.ErrorCode, error = error.Message }); }
        catch (InvalidOperationException error) { return Conflict(new { error = error.Message }); }
    }
    [HttpGet("sources")]
    public IActionResult Sources(Guid tenantId) => !CanManage(tenantId) ? Forbid() : Execute(() => store.ListSources(tenantId));

    [HttpGet("overview")]
    public IActionResult Overview(Guid tenantId) => !CanManage(tenantId) ? Forbid() : Execute(() => Diagnostics.Overview(tenantId));
    private PipDiagnostics Diagnostics => diagnostics ?? throw new InvalidOperationException("PIP diagnostics service is unavailable.");

    [HttpPost("sources"), ValidateAntiForgeryToken]
    public IActionResult Register(Guid tenantId, RegisterPipSourceCommand command)
    {
        if (Guard(tenantId) is { } denied) return denied;
        return Execute(() =>
        {
            AccessPolicyStore.Label(command.Reason, 1000);
            if (command.Enabled) throw new ArgumentException("Save the source disabled, test its connection, and then enable it.");
            return store.Register(tenantId, command.Name, command.Endpoint, command.AllowedHost, command.SecretReference,
                command.SchemaVersion, command.MaxAgeSeconds, command.TimeoutSeconds, command.MaxResponseBytes, command.Enabled,
                command.Definitions, command.SyncSubjects, command.PollIntervalSeconds, Actor, command.Reason);
        });
    }
    [HttpPost("sources/{sourceId:guid}/settings"), ValidateAntiForgeryToken]
    public IActionResult Settings(Guid tenantId, Guid sourceId, UpdatePipSourceSettingsCommand command)
    {
        if (Guard(tenantId) is { } denied) return denied;
        return Execute(() => store.UpdateSettings(tenantId, sourceId, command.ExpectedGeneration, command.Name,
            command.MaxAgeSeconds, command.TimeoutSeconds, command.MaxResponseBytes, command.SyncSubjects,
            command.PollIntervalSeconds, Actor, command.Reason));
    }
    [HttpPost("sources/{sourceId:guid}/enabled"), ValidateAntiForgeryToken]
    public IActionResult Enable(Guid tenantId, Guid sourceId, PipEnableCommand command)
    {
        if (Guard(tenantId) is { } denied) return denied;
        return Execute(() =>
        {
            AccessPolicyStore.Label(command.Reason, 1000);
            return store.EnableAfterTest(tenantId, sourceId, command.Enabled, Actor, command.Reason, transport, clock.GetUtcNow());
        });
    }
    [HttpGet("sources/{sourceId:guid}/runs")]
    public IActionResult Runs(Guid tenantId, Guid sourceId, [FromQuery] int take = 50) => !CanManage(tenantId) ? Forbid() : Execute(() => store.ListRuns(tenantId, sourceId, take));

    [HttpGet("sources/{sourceId:guid}/status")]
    public IActionResult Status(Guid tenantId, Guid sourceId, [FromQuery] string subject)
    {
        if (!CanManage(tenantId)) return Forbid();
        if (diagnostics is not null) return Execute(() => diagnostics.Status(tenantId, sourceId, subject));
        return Execute(() =>
        {
            var source = store.ReadSource(tenantId, sourceId);
            var result = store.Read(tenantId, subject, clock.GetUtcNow(), sourceId);
            return new { result.Status, result.Error, result.Snapshot?.ObservedAt, result.Snapshot?.FreshUntil,
                result.Snapshot?.SourceVersion, result.Snapshot?.ContentHash, deploymentApproved = source.Educational || transport.IsAuthorized(source),
                checkpoint = store.Checkpoint(tenantId, sourceId, subject) };
        });
    }
    [HttpPost("sources/{sourceId:guid}/test"), ValidateAntiForgeryToken]
    public async Task<IActionResult> Test(Guid tenantId, Guid sourceId, PipSyncCommand command, CancellationToken cancellationToken)
    {
        if (Guard(tenantId) is { } denied) return denied;
        try { return Ok(await Diagnostics.TestAsync(tenantId, sourceId, command.Subject, Actor, command.Reason, cancellationToken)); }
        catch (KeyNotFoundException) { return NotFound(); }
        catch (ArgumentException) { return BadRequest(new { errorCode = "invalid-test-request", error = "شخص و دلیل آزمون معتبر را وارد کنید." }); }
        catch (InvalidOperationException) { return Conflict(new { errorCode = "test-not-available", error = "مجوز اتصال استقرار و وضعیت منبع را بررسی کنید؛ ممکن است آزمون دیگری در حال اجرا باشد." }); }
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
    [HttpPost("journey"), ValidateAntiForgeryToken]
    public IActionResult Journey(Guid tenantId, PipExampleCommand command)
    {
        if (!Admin) return Forbid();
        return Execute(() => (journey ?? throw new InvalidOperationException("PIP educational journey is unavailable.")).Ensure(tenantId, Actor, command.Reason));
    }
}
public sealed record RegisterPipSourceCommand(string Name, string Endpoint, string AllowedHost, string? SecretReference,
    int SchemaVersion = 1, int MaxAgeSeconds = 300, int TimeoutSeconds = 10, int MaxResponseBytes = 262144,
    bool Enabled = false, PipAttributeDefinition[]? Definitions = null, string[]? SyncSubjects = null, int PollIntervalSeconds = 0, string Reason = "");
public sealed record PipEnableCommand(bool Enabled, string Reason = "");
public sealed record UpdatePipSourceSettingsCommand(long ExpectedGeneration, string Name, int MaxAgeSeconds = 300,
    int TimeoutSeconds = 10, int MaxResponseBytes = 262144, string[]? SyncSubjects = null, int PollIntervalSeconds = 0, string Reason = "");
public sealed record PipSyncCommand(string Subject, string Reason = "");
public sealed record PipExampleCommand(string Reason);
