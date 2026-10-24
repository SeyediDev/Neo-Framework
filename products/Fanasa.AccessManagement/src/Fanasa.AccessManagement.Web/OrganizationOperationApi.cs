using System.Security.Claims;
using Fanasa.AccessManagement.Web.Metering;
using Fanasa.AccessManagement.Web.Organization;
using Fanasa.AccessManagement.Web.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Fanasa.AccessManagement.Web.Security
{
    /// <summary>Trusted server adapter must obtain definitive Gateway admission bound to this tenant,
    /// actor, requestId and payload, with valid token/decision timing and idempotent quota handling.
    /// Ambiguous or unavailable admission must return false. No production adapter is supplied;
    /// a header or caller-provided reference is not admission evidence.</summary>
    public interface IOrganizationUnitSaveAdmission
    {
        bool IsConfigured { get; }
        ValueTask<bool> AdmitAsync(Guid tenant, string actor, OrgCommand command, ClaimsPrincipal principal, CancellationToken cancellationToken);
    }

    internal sealed class UnconfiguredOrganizationUnitSaveAdmission : IOrganizationUnitSaveAdmission
    {
        public bool IsConfigured => false;
        public ValueTask<bool> AdmitAsync(Guid tenant, string actor, OrgCommand command, ClaimsPrincipal principal, CancellationToken cancellationToken)
            => ValueTask.FromResult(false);
    }
}

namespace Fanasa.AccessManagement.Web.Api
{
    [ApiController, Route("api/organization-operations/tenants/{tenantId:guid}/unit.save")]
    [Authorize(AuthenticationSchemes = OrganizationBearerSecurity.Scheme, Policy = OrganizationBearerSecurity.Policy)]
    public sealed class OrganizationOperationApi(OrganizationStore store, OrganizationBearerBoundary boundary,
        OrganizationUsageMetering metering, IOrganizationUnitSaveAdmission admission, TemplateOrganizationStore? examples = null) : ControllerBase
    {
        [HttpPost, TemporalPermission("organization.write")]
        public async Task<IActionResult> Execute(Guid tenantId, OrgCommand command, CancellationToken cancellationToken)
        {
            // Recheck current tenant/member/grant before admission and again after asynchronous admission.
            if (!boundary.Allows(User, tenantId)) return Forbid(OrganizationBearerSecurity.Scheme);
            if (TemplateOrganizationWriteGuard.Reject(examples, tenantId) is { } readOnly) return readOnly;
            if (command.Operation != "unit.save" || !Guid.TryParseExact(command.RequestId, "D", out var requestId) || requestId == Guid.Empty)
                return BadRequest(new { error = "unit.save requires a UUID requestId." });
            try
            {
                var actor = OrgAuthorization.Subject(User);
                metering.ValidateInvocation(tenantId, command, actor, User);
                // The existing store resolves current product/subscription/entitlement/rate bindings
                // and commits organization/history/usage/ledger/outbox in one shared Fabric transaction.
                if (!metering.Enabled || !await admission.AdmitAsync(tenantId, actor, command, User, cancellationToken)
                    || !boundary.Allows(User, tenantId)) return Forbid(OrganizationBearerSecurity.Scheme);
                return Ok(store.Execute(tenantId, command, actor, User));
            }
            catch (UnauthorizedAccessException) { return Forbid(OrganizationBearerSecurity.Scheme); }
            catch (KeyNotFoundException) { return NotFound(); }
            catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
            catch (InvalidOperationException ex) { return Conflict(new { error = ex.Message }); }
        }
    }
}
