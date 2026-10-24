# HR API security and command checks

This executable starts disposable Kestrel HTTPS hosts and runs actual HTTP requests through `ConfigureOrganizationHrUpstream`, `AddOrganizationHrSecurity`, `OrganizationHrBoundary` and `OrganizationHrApi`. Its certificates, signing keys, central product/client, tenant memberships, grants and subscriptions are synthetic. It does not provision live identities or relax the production authentication code.

Coverage includes the actual TLS peer pin and clientAuth EKU; JWT signature, issuer, audience, expiry and canonical timing claims; cookie and forged-header isolation; exact service subject/client/scope; current central registration, membership, grants, tenant and subscription; private Gateway authorization subrequests; strict JSON/body/media bounds; chart/history; command revision conflicts; appointments and archive sequence; immutable history; durable receipts and exact replay; denied-operation no-mutation checks; and rejection of HR `unit.save` while measured metering is enabled.

The test uses a static offline JWT signing configuration after registering the production scheme. Its HTTP clients pin the disposable server certificate. These test-only substitutions do not establish live Keycloak discovery, API Gateway routing, production certificate trust or operational authorization.

The latest local run passed 171 checks with exit zero. [Evidence](../../../../docs/fanasa-platform-control-center/pilot/hr-api-verification.json) and [log](../../../../docs/fanasa-platform-control-center/pilot/hr-api-test.log) record this result separately from Gateway acceptance. Checks include real token/grant expiry during Store processing and rollback of chart/history/receipt/outbox, then successful recovery of the same request without a ghost receipt. A test-only Store dependency barrier preserves the synchronous Fabric transaction; production clocks and authorization are unchanged. Named human listener preservation and denial of HR bypass on that listener are also exercised. Reopening the database/store checks persistence; it is not a full process restart test.

The companion `verify_openapi.py` checks the pilot YAML and published JSON agree on routes and schemas, resolves local references and validates focused command examples against Draft 2020-12. It uses already available PyYAML/jsonschema development packages; they are not product runtime dependencies. It is not a full OpenAPI compliance validator. See [the contract log](../../../../docs/fanasa-platform-control-center/pilot/hr-openapi-validation.log).

Run with .NET 10:

```powershell
$hrArtifacts = Join-Path ([System.IO.Path]::GetTempPath()) 'fanasa-organization-hr-build'
dotnet run --project products/Fanasa.AccessManagement/tests/Organization.Hr.Tests/Organization.Hr.Tests.csproj --no-launch-profile --artifacts-path $hrArtifacts -p:NuGetAudit=false -p:RestoreIgnoreFailedSources=true
```

The audit property only works around an unavailable audit feed; this command does not perform a dependency vulnerability audit. CI should restore through its configured feed and retain test output as evidence.

Runtime requirements are explicit under `Organization:HrApi`: enabled, HTTPS authority at `/realms/fanasa`, centrally registered audience, exact Keycloak service-account subject, client ID, distinct read/write scopes and SHA-256 Gateway certificate pin. `Kestrel:Endpoints:OrganizationHr` must provide an HTTPS URL and server certificate. The Gateway must present the pinned, currently valid clientAuth TLS certificate on its upstream connection; forwarded certificate headers do not satisfy this requirement. The service must be an active member of the route tenant with a current durable `organization.read`/`organization.write` grant, and its active central client must allow the requested scope. When subscription enforcement is enabled, the tenant also requires a current `organization-fabric` subscription. No runtime registration or grant is synthesized by the authentication extension.

Deploy probes should verify successful authorized chart/history reads via the Gateway; 401 for anonymous/invalid JWT; 403 for wrong scope, wrong tenant or revoked durable grant; denied direct upstream access without the Gateway certificate; handshake rejection with a different certificate; strict command validation; and unchanged records for denied calls. A command/replay probe must use an explicitly designated development tenant and stable UUID Idempotency-Key, then check one revision, one receipt and one outbox event. Never run write probes against an unspecified operational tenant.
