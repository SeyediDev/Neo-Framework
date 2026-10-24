# PCC integration evidence — source delivery 0.3.0

Date: 2026-10-10, Asia/Tehran. Workspace: `E:/SJVS/Projects/Neo`. This records implemented source wiring and local verification, not a deployed service or live business acceptance. Delivery version `0.3.0` includes the unchanged native library build version `0.2.0`; no NuGet package was created or published.

## Implemented delivery

The existing Web project references `Fanasa.ContractPolicy` and compiles the four owned [pcc source files](pcc/). `FabricServices.AddIdentityOrganizationFabric` calls `AddContractPolicyHost`. New production behavior includes dedicated producer JWT authentication, atomic accepted projection/receipt/shared-allocation/outbox persistence, a trusted Fabric PIP reader, independent policy publication and explicit product-role assignment, pure organization decisions, and an admission wrapper that rechecks authority after obligation execution.

Producer and organization integration require explicit configuration and existing valid registrations. No product, client, grant, role, commercial contract, rate or subscription is provisioned by this delivery. The producer receiver audience is `fanasa-access-management-web`; its configured `ProductKey` binds the registered owner of the sending client. The sender's product audience is not assumed to equal the receiver audience. See [PCC-INTEGRATION.md](PCC-INTEGRATION.md) for exact routes and configuration.

The shared edits owned by this work are the Web project reference/source link, `FabricServices` registration and product README paragraph. Existing dirty changes elsewhere are not attributed to this delivery. This integration did not edit the host `Program.cs`, central registry, base Fabric persistence, migrations, business contract schema, Gateway or ledger.

## Final local results

Environment: Windows x64, .NET SDK `10.0.401`, runtime `10.0.12`; existing host packages remain pinned to `10.0.8`.

| Verification | Final result |
| --- | --- |
| Actual `Fanasa.AccessManagement.Web` build | Exit 0; 0 warnings, 0 errors |
| Native library core/adapter console suite | 344 passed, 0 failed; exit 0 |
| New PCC integration console suite | 114 passed, 0 failed; exit 0 |
| Existing organization Bearer HTTP regression | 77 checks passed; exit 0 |
| Existing organization/accounting/governance/recovery regression | 248 checks passed; exit 0 |
| Local Debug publish | Exit 0; both host/native DLLs and all 10 schemas present |

The four suites total **783 passing checks**, not a claim of 783 distinct production scenarios. The final PCC harness build also emitted 0 warnings and 0 errors. Its copied host and native DLLs matched the actual final Web build. The locally published host matched that same final built host. No production source was edited after the final successful build and suites; subsequent edits were documentation and manifest completion.

Commands used from the workspace root:

```powershell
dotnet build products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/Fanasa.AccessManagement.Web.csproj --no-restore --nologo --verbosity minimal

dotnet run --project docs/fanasa-platform-control-center/workstreams/contract-policy/implementation/tests/Fanasa.ContractPolicy.Tests/Fanasa.ContractPolicy.Tests.csproj --no-build --no-restore --verbosity minimal

dotnet build docs/fanasa-platform-control-center/workstreams/contract-policy/implementation/tests/Fanasa.ContractPolicy.Pcc.Tests/Fanasa.ContractPolicy.Pcc.Tests.csproj --no-restore --verbosity minimal -p:BuildProjectReferences=false -p:StaticWebAssetsEnabled=false
dotnet run --project docs/fanasa-platform-control-center/workstreams/contract-policy/implementation/tests/Fanasa.ContractPolicy.Pcc.Tests/Fanasa.ContractPolicy.Pcc.Tests.csproj --no-build --no-restore --verbosity minimal

dotnet run --project products/Fanasa.AccessManagement/tests/Organization.Bearer.Tests/Organization.Bearer.Tests.csproj --verbosity minimal -p:BuildProjectReferences=false -p:StaticWebAssetsEnabled=false
dotnet run --project products/Fanasa.AccessManagement/tests/Organization.Tests/Organization.Tests.csproj --verbosity minimal -p:BuildProjectReferences=false -p:StaticWebAssetsEnabled=false
```

The test-only MSBuild overrides avoid sandbox/cache ownership conflicts after the actual Web project was freshly built; they do not change product configuration. Sandbox attempts for existing regression builds and local publish encountered write/cache restrictions, including `MSB3491` in Web `obj`; the authorized local commands were retried through automatic approval review. Normal build/run commands are in the integration guide. Local publication used `dotnet publish` on that Web project with `--no-build --no-restore --nologo -c Debug -o <unique temporary directory> --verbosity minimal`.

Final timestamp inspection found `ContractPolicyHost.cs` newer than the previously recorded host DLL. The actual Web was therefore rebuilt again successfully with 0 warnings and 0 errors, the PCC harness recopied that fresh host, all three host-dependent suites were rerun, and local publish was recreated. The final manifest identifies this fresh output, not the earlier DLL. The native library DLL remained identical to its tested output.

## What the new PCC suite exercises

The harness uses actual PCC DI, controllers and production JWT handlers, Kestrel loopback HTTP, ephemeral RSA metadata/key material, temporary SQLite databases and real cookie/antiforgery handling. Identities, registrations, contracts and executor completions remain synthetic.

Checks cover invalid signatures/issuer/audience/lifetime, signed duplicate or escaped/malformed raw claims, service and human subject rules, registry/client/scope revocation, and token expiry while a body is streamed. Expiry or final authorizer revocation produces no persistent projection writes. Cookie-only administrator routes and CSRF requirements are exercised through HTTP.

Pure decisions check explicit role assignment plus direct grant, current policy/allocation binding, offboarding and revocation, a real two-person published legacy Deny veto, corrupted stored policy/role metadata, and caller body/header isolation. PDP evaluation leaves organization, usage and ledger unchanged.

Persistence checks cover replay, conflict, stale and suspended projections, shared allocation revisions, restart, two SQLite writers and authorizer rollback. Admission checks use synthetic complete, partial, unknown-only, unknown-superset, duplicate, null, throwing, revoked, changed and expired obligation execution. The host wrapper requires the exact issued completion IDs without extras or duplicates, beyond the native helper's required-ID presence check. The actual default executor is unconfigured; the real `unit.save` route remains **403**.

## Earlier runs and corrections

Earlier PCC runs reported `100 passed / 10 failed`, then `110 passed / 2 failed`. Test harness corrections included missing `AddRazorPages` registration already present in the production host, a fixed executor clock earlier than evaluation, and a slow-body delay exceeding Kestrel's default rate guard. The unknown-only case tests missing required completion; separate final cases exercise the host's stronger rejection of unknown-superset and duplicate completions. The streamed-body test uses a three-second token with default Kestrel transport limits. A subsequent 112/0 result was superseded by the complete fresh build/run: **114/0**, including both strengthened completion cases. Final source and loaded DLL hashes were confirmed after that run; the native helper was unchanged.

One earlier native no-build run reported `343 passed / 1 failed`; the exact failed case was lost from truncated tool output, so its cause is **unproven**. Two subsequent independent complete native runs both passed `344 / 0`. No native source change or diagnosis is asserted from those reruns. One full successful native log was retained in the local temporary directory as `fanasa-native-regression-0a82d48b9835442d9a53a2897935bc3c.log`.

## Manifest and limits

[manifest.v0.3.0.json](manifest.v0.3.0.json) records current SHA-256 source/test/documentation inputs, schema/fixture fingerprints and the whole-file current snapshots of the three shared hooks. Those snapshots include preexisting workspace content and do not claim the entire files were authored by this task. It is a source manifest, not complete reproducible provenance for all host dependencies. Generated bin/obj, temporary test data, publication outputs and historical evidence/manifests are excluded from input hashes.

Native library/build/test inputs and schema/fixtures retained from the 0.2.0 manifest remain unchanged, apart from the intentionally updated README/adapter guide. The JavaScript reference was unchanged and was not rerun: its previous **171-pass** baseline remains historical in [EVIDENCE.md](EVIDENCE.md), outside the 783 current .NET checks.

Registry and Fabric use separate stores; no cross-store atomicity is claimed. Final authority and token lifetime are rechecked, while distributed invalidation and commercial producer mapping remain external integration boundaries. Stored outbox records do not prove external delivery.

The default Gateway obligation executor has `IsConfigured=false` and returns no completion. Existing APISIX/Lua/Redis code does not supply an authenticated witness delivery interface to this C# backend in the inspected source. This delivery therefore does not prove live Gateway admission, counters/seat/budget/ledger execution, TLS/Keycloak registration, a signed real commercial contract, production deployment or operational end-to-end success. It does not assert full OASIS XACML/JSON Schema conformance.

The public Neo Framework API, Neo MCP contracts and maintained Skill recipes are unaffected. Companion maintenance is limited to the module and consuming host documentation.
