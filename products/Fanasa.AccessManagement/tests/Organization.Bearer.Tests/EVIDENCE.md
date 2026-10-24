# Local Bearer HTTP evidence — 2026-10-10

Result: **77 synthetic Bearer HTTP checks passed; exit code 0.** This is local middleware/backend evidence, not live identity or Gateway acceptance. This final run includes the token deadline correction after independent review; it supersedes the earlier 66-check result.

Exact passing command (PowerShell, repository root):

```powershell
$bearerBuild = Join-Path ([System.IO.Path]::GetTempPath()) 'fanasa-organization-bearer-build'
dotnet run --project products/Fanasa.AccessManagement/tests/Organization.Bearer.Tests/Organization.Bearer.Tests.csproj --no-launch-profile --no-restore --artifacts-path $bearerBuild -p:NuGetAudit=false
```

Only this dedicated project and its existing web project reference were built. The initial default restore hit NU1900 because the NuGet audit feed was unreachable; using cached restored assets and `NuGetAudit=false` allowed the focused run. Workspace `obj` creation was denied, so build artifacts used the user temporary directory. Temporary synthetic host databases were disposed and removed after the successful run.

The real JwtBearer handler and authorization middleware returned 401 for anonymous access, valid cookie alone, invalid signature, wrong issuer/audience and expired JWT. They returned 403 for inactive token, unauthorized/conflicting client, missing scope, cross-tenant actor, forged claims/tenant/permission/admission headers without a durable grant, revoked grant, inactive tenant/member/client, and removed registered client scope.

HTTP checks also rejected suspended subscription, expired entitlement, unavailable/default admission, synthetic admission denial, grant revocation during asynchronous admission, unsupported operations, missing request UUID, conflicting receipt payload and replay after grant revocation. Missing Bearer settings, missing opt-in, disabled metering and unconfigured admission all rejected requests without 500s. Every denied request compared all organization/history/receipts, measured and legacy usage, all accounting records and outbox against its pre-request snapshot.

Timing claims require exactly one canonical JSON integer `exp` and at most one canonical JSON integer `nbf`, inspected in the already signature-validated token payload. This prevents the library's claim normalization from accepting a numeric-string expiration. Signed missing, duplicated and noncanonical expiration, and future `nbf`, were denied without state changes. The boundary also rereads current time after durable record reads on every invocation. A Kestrel test entered the asynchronous synthetic admission adapter with a valid live JWT, waited until 100 milliseconds after its signed expiration, then received HTTP 403 with the complete business/financial snapshot unchanged.

With the explicitly synthetic allow adapter, one actual `OrganizationStore` unit.save committed one revision/unit, one measured usage operation, one synthetic 12 IRR debit and one UsageCommitted outbox. An exact repeated HTTP request returned the original receipt with no new domain/history/usage/ledger/outbox state.

New files: `OrganizationBearerSecurity.cs`, `OrganizationOperationApi.cs`, and this dedicated console project (`Organization.Bearer.Tests.csproj`, `Program.cs`, `README.md`, `EVIDENCE.md`). No existing controller, Fabric store, registry, policy engine, metering owner file or Program.cs was edited by this workstream. The root coordinator owns the one-line host registration.

Remaining runtime work: real registered product/client/audience/scope inventory, authentic central JWT/discovery/TLS, trusted definitive Gateway admission with quota replay handling, and real approved commercial/rate configuration. The production admission adapter remains intentionally absent and the route remains disabled by default. No deployment, live grant/client/registry mutation, user-owned chat creation or messaging occurred.
