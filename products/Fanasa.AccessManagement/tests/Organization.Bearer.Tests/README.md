# Organization Bearer boundary checks

This console suite starts disposable Kestrel hosts and sends actual HTTP requests through the production `AddOrganizationBearerSecurity` extension and `OrganizationOperationApi`. It needs no TestHost dependency. JWT signing keys, identities, central registrations, contracts, rates and the allow admission adapter are exclusively synthetic test data.

The additive route is `POST /api/organization-operations/tenants/{tenantId}/unit.save`. Its body is the existing `OrgCommand`, with `Operation=unit.save` and a UUID `RequestId`. The existing cookie/OIDC organization controller and its CSRF check remain separate.

One host wiring call is required after the existing Fabric registrations:

```csharp
Fanasa.AccessManagement.Web.Security.OrganizationBearerSecurity
    .AddOrganizationBearerSecurity(builder.Services, builder.Configuration);
```

The route denies by default. Opt-in configuration lives under `Organization:Bearer` (`Enabled`, `Authority`, `Audience`, `ClientId`, `Scope`). Authority must be the HTTPS `fanasa` realm. The configured audience must equal the current central `organization-fabric` product audience; the configured client must be a current active client of that product and allow the configured scope. No registration, client, grant or commercial binding is created by the extension.

Every invocation requires an active route tenant, active subject membership and current durable `organization.write` grant. JWT permission claims and tenant/admission headers cannot replace these records. Subscription/entitlement checks reuse `OrganizationUsageMetering.ValidateInvocation`; the existing store rechecks and resolves its current rate binding inside the Fabric transaction. Metering must be enabled.

After signature validation, the signed payload must contain exactly one canonical JSON integer `exp` and at most one canonical JSON integer `nbf`. Every boundary check evaluates the current token deadline again after reading durable records, including after asynchronous admission returns. A token that expires during admission cannot authorize a mutation.

`IOrganizationUnitSaveAdmission` is a required trusted server dependency. The supplied production default is unconfigured and denies. A real adapter still needs to validate definitive Gateway admission bound to the exact tenant, actor, request ID and payload, including token/decision validity and idempotent quota handling. This suite's `SyntheticAdmission` is an explicitly synthetic allow/deny adapter, not live Gateway acceptance or a production adapter.

Run only this project:

```powershell
$bearerArtifacts = Join-Path ([System.IO.Path]::GetTempPath()) 'fanasa-organization-bearer-build'
dotnet run --project products/Fanasa.AccessManagement/tests/Organization.Bearer.Tests/Organization.Bearer.Tests.csproj --no-launch-profile --artifacts-path $bearerArtifacts -p:NuGetAudit=false -p:RestoreIgnoreFailedSources=true
```

The audit property is an environment-specific workaround for an unreachable NuGet audit feed; this command does not perform a dependency vulnerability audit. Once restored, `--no-restore` can be added. No deployment, real Keycloak token, TLS discovery, production registration/rate, native APISIX/Redis admission, or production quota accounting is asserted by this suite.
