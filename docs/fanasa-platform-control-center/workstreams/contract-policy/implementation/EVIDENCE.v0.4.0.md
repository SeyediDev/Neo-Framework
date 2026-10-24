# Authenticated Gateway integration evidence — source delivery 0.4.0

Date: 2026-10-10, Asia/Tehran. Workspace: `E:/SJVS/Projects/Neo`. This records completed source integration, local verification and local publication. Production deployment and real commercial acceptance were not performed. The native library source and build version remain `0.2.0`; its DLL was rebuilt and tested again. No NuGet package was published.

## Delivered behavior

[PCC-GATEWAY.md](PCC-GATEWAY.md) documents the current configuration and wire contract. Four new production files implement a dedicated authenticated [Gateway Decision endpoint](pcc/ContractPolicyGatewayApi.cs), [Gateway service JWT validation](pcc/ContractPolicyGatewaySecurity.cs), [authoritative Decision/PIP binding](pcc/ContractPolicyGatewayDecision.cs), and [signed request receiver, executor and commit guard](pcc/ContractPolicyGatewayContext.cs).

`POST /api/gateway/decision` uses `GatewayDecisionBearer`, an explicitly configured registered service client and scope, and the central Fanasa authority. Strict request/raw-claim validation and current registration checks accompany the production JWT handler. Decision evaluation reads current server authority and has no organization or financial side effects.

The optional `ContractPolicy:GatewayRequest` receiver requires direct Kestrel TLS, a current pinned client certificate, request-bound HMAC, a canonical exact envelope, and independent `OrganizationBearer` authentication. It binds the actual body, raw path/query, Authorization and Idempotency-Key bytes. Neither forwarded certificate headers nor a caller's admission ID establish authority. `FabricServices` registers `AddContractPolicyHostWithGatewayContext`; only explicitly enabled signed mode registers its executor and matching commit guard. The old `GatewayContext` mode cannot be enabled alongside it.

Nonce receipts persist in Fabric under `contract-policy.gateway-context.replay.v1`; the atomic claim uses the existing immediate SQLite transaction. The same executor supplies `IContractPolicyGatewayCommitGuard`. `OrganizationStore` invokes it after acquiring the writer lock and after all writes, before commit. The guard rechecks the signed principal, witness, certificate, full fractional deadline, current access, native Decision and metering binding. A late veto rolls back organization/history/receipt/measured usage/ledger/outbox while preserving the separately claimed replay tombstone. A pending PIP ValueTask is denied without an asynchronous wait inside the thread-local transaction.

An exact already-committed receipt retry requires the same actor, command fingerprint, tenant and historical revision. This retry status is fixed before execution. Current authority and witness validity remain required, but an expired historical tariff is not reopened and the original response, consumption and debit remain unchanged. A newly staged receipt cannot bypass the final rate check.

The shared integration edits are the project source/reference wiring, `FabricServices` registration, the product README paragraph, the two transaction guard calls in `OrganizationFabric.cs`, and the small environment/binding hooks in `OrganizationUsageMetering.cs`. Whole-file snapshots include concurrent content and do not attribute the entire files to this continuation. Other existing dirty workspace changes were preserved.

Concurrent PCC control-plane work is included in this host: [ContractGatewayPublication.cs](pcc/ContractGatewayPublication.cs) and its Host/Store/Api integration retain immutable publication snapshots and canonical limits hashes. Producer ingress returns `202` while pending or superseded; only a trusted matching durable observed receipt yields an acknowledged `200`. Its unconfigured default observer and synthetic PCC tests do not prove an external Gateway observation. This continuation did not author those concurrent changes.

## Final local results

Windows x64; .NET SDK `10.0.401`, runtime `10.0.12`; existing host packages remain `10.0.8`.

| Verification | Final result | Retained output |
| --- | --- | --- |
| Actual Web build | Exit 0; 0 warnings, 0 errors; 1:30.13 | Tool build session `9120` |
| Native core/adapter console suite | 344 passed, 0 failed; exit 0 | [Run](tests/native-final-4c87-run.log) |
| PCC HTTP/integration console suite | 134 passed, 0 failed; exit 0 | [Build](tests/pcc-final-89e186-build.log), [run](tests/pcc-final-89e186-run.log) |
| Gateway HTTPS/mTLS integration suite | 78 passed, 0 failed; exit 0 | [Build](tests/Fanasa.ContractPolicy.Gateway.Tests/Evidence/final-build.log), [run](tests/Fanasa.ContractPolicy.Gateway.Tests/Evidence/final-run.log) |
| Existing organization Bearer HTTP suite | 77 checks passed; exit 0 | [Build](tests/bearer-final-89e186-build.log), [run](tests/bearer-final-89e186-run.log) |
| Existing organization/accounting/governance/recovery suite | 356 checks passed; exit 0 | [Build](tests/organization-final-89e186-build.log), [run](tests/organization-final-89e186-run.log) |
| Local Debug publish | Exit 0; both DLLs and 10 schemas match source/build | `.build/contract-policy-0.4.0-publish/`, tool session `96078` |

The five suites total **989 passing checks**, not 989 distinct production scenarios. All four host-dependent test builds reported zero warnings and errors and loaded copies of the final host/native assemblies. Gateway's final test build took 25.61 seconds; its final run was session `45730`. Native rerun session `40165` completed against the rebuilt native DLL. Organization's current count is 356, superseding the historical 248-check result.

Final SHA-256:

| Artifact | SHA-256 |
| --- | --- |
| `Fanasa.AccessManagement.Web.dll` | `89E1863FC651961BD187DC040153B9252FB5A3B5D7BE3D7A1AD446590E6B5209` |
| `Fanasa.ContractPolicy.dll` | `4C87ACC8FB26E112DD86E5ACBE306B390A881030B7A02D9955426153B84B4841` |
| Signed receiver source | `A83F8A2898B3D811BBB89775FFA51BED6670ECA2B39F6CE52233C9A0F1627308` |

The Web build timestamp is `2026-10-10T17:07:49.3325844Z`. Final `.cs`/`.cshtml`/`.razor` input timestamp inspection found no host or PCC source newer than the DLL. Subsequent changes were test fixtures and documentation. Four host-dependent test directories and local publication contain identical Web/native DLLs; the native suite's core copy matches too. All ten published schemas match the source byte hashes.

The 34 native source/build/schema/fixture/test inputs selected from [manifest.v0.3.0.json](manifest.v0.3.0.json) have zero mismatches. The native DLL differs from that historical build and was therefore actually rerun; unchanged source is not an assertion of unchanged binary output. [manifest.v0.4.0.json](manifest.v0.4.0.json) records final source/documentation fingerprints, five whole-file shared snapshots, artifact hashes and verification results. It is not complete reproducible provenance for every host dependency. Generated outputs, runtime data, logs and historical manifests/evidence are excluded from source input hashes.

## Gateway proof and its scope

The [Gateway harness](tests/Fanasa.ContractPolicy.Gateway.Tests/Program.cs) runs actual Kestrel HTTPS, independent temporary client certificates, signed JWTs through production handlers, current registry/PIP/DI, SQLite and the real organization/metering path. Its C# HMAC signer independently implements the published framing contract. External Lua/APISIX/Redis is not executed.

The successful route records one organization revision, one measured operation, a synthetic debit of 12, and a usage outbox record. Same-nonce replay is `403`; concurrent requests produce one `200` and one `403`. Independent Fabric reopen preserves replay tombstones. A fresh authorized business retry returns the exact original HTTP response and unchanged business snapshot. After the synthetic immutable rate window closes, that retry still succeeds unchanged; a new business UUID is denied without writes.

Checks cover invalid JWT claims/signatures, request parsing and tuple binding, service/client/scope revocation, and scope revocation after initial authorization while the real body read is awaited. Signed operation checks cover certificate absence/pin mismatch, HMAC/body/path/auth/idempotency tampering, duplicate or unknown fields/headers, nonce/key/tier/time/revision/period mismatches, grant/role/policy revocation, disabled mode and incompatible metering contract. Resource tests cover a mismatched context tenant and an unknown parent ID; they do not seed a resource owned by another tenant.

Three late-expiry cases prove that the real admission or real usage validator hook was reached before moving the injected clock: expiry after admission, expiry during measured writes, and one .NET tick beyond the signed fractional deadline. Each returns `403` with the entire business snapshot unchanged. These are local synthetic clock tests, not measurements of distributed revocation latency.

## Earlier results retained

- The current native run initially reported **343/1**. The retained [earlier log](tests/native-earlier-4c87-run.log) identifies `v2 lower aggregate and lower config revision is stale and replay is remembered` failing with a regex timeout (`RegexMatchTimeoutException`, configured 100 ms). The complete rerun passed **344/0** without native source/test changes. Concurrent load was observed, but the underlying scheduling cause was not proved. The separate cause-unproven failure recorded in 0.3.0 remains historical.
- An intermediate PCC run loaded Web hash `74848B73C7E651A9DF6F3E89646CFCC9A1ADA9E66620CE9FDC93346D05BB6E9F` and reported **133/1** on Unicode limits-hash parity. That assembly preceded the concurrent non-BMP canonicalization fix. Its [build](tests/pcc-existing-74848-build.log) and [run](tests/pcc-existing-74848-run.log) are preserved; the fresh final host passes **134/0**.
- Gateway's [first run](tests/Fanasa.ContractPolicy.Gateway.Tests/Evidence/earlier-tls-fixture-failure.log) reported **0/78**, with TLS handshake EOF before HTTP. Temporary test certificate import changed from `EphemeralKeySet` to `UserKeySet | Exportable`, without `PersistKeySet` or trust-store registration. Real TLS then worked. A Schannel key-loading incompatibility was suspected; its underlying platform cause was not independently proved.
- The [second Gateway run](tests/Fanasa.ContractPolicy.Gateway.Tests/Evidence/earlier-policy-fixture-failure.log) reported **74/4**. A retired-policy test attempted to republish the same version; the actual store correctly rejected it and the shared fixture remained retired, contaminating later positive cases. Moving retirement into its own disposable fixture resolved this test-only issue. Hook-reached and exact-response assertions were also strengthened. The final **78/0** supersedes both earlier runs; production TLS/authentication requirements were retained.

## Reproduction and remaining boundaries

From the workspace root, build the actual host first. Test-only overrides recopy that built host while avoiding a second project-reference/static-assets build:

```powershell
dotnet build products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/Fanasa.AccessManagement.Web.csproj --no-restore --nologo --verbosity minimal

dotnet build docs/fanasa-platform-control-center/workstreams/contract-policy/implementation/tests/Fanasa.ContractPolicy.Gateway.Tests/Fanasa.ContractPolicy.Gateway.Tests.csproj --no-restore --verbosity minimal -p:BuildProjectReferences=false -p:StaticWebAssetsEnabled=false
dotnet run --project docs/fanasa-platform-control-center/workstreams/contract-policy/implementation/tests/Fanasa.ContractPolicy.Gateway.Tests/Fanasa.ContractPolicy.Gateway.Tests.csproj --no-build --no-restore --verbosity minimal

dotnet run --project docs/fanasa-platform-control-center/workstreams/contract-policy/implementation/tests/Fanasa.ContractPolicy.Tests/Fanasa.ContractPolicy.Tests.csproj --no-build --no-restore --verbosity minimal

dotnet publish products/Fanasa.AccessManagement/src/Fanasa.AccessManagement.Web/Fanasa.AccessManagement.Web.csproj --no-build --no-restore --nologo -c Debug -o .build/contract-policy-0.4.0-publish --verbosity minimal
```

PCC, Organization.Bearer and Organization suites use the same test build overrides followed by their own `dotnet run --no-build --no-restore`. Authorized local commands requiring writable build caches were approved through automatic review. No automatic review rejection remains.

The JavaScript reference's historical 171-pass baseline was not rerun and is excluded from the current 989 .NET checks. See [EVIDENCE.md](EVIDENCE.md). Public Neo Framework APIs, MCP contracts and maintained Skill recipes are unchanged.

The delivery creates no real client/product/grant/role/contract/rate registration. Gateway Decision, signed execution and metering require explicit valid configuration. The signed executor also requires enabled organization metering and an environment exactly matching the organization policy environment, with valid server-side financial bindings; see the [metering prerequisite](PCC-GATEWAY.md). Registry and Fabric are separate stores, with no cross-store atomicity claim. Gateway quota/admission remains external; the receiver consumes authenticated evidence and does not provision or refund counters. Outbox persistence does not prove external delivery or observed acknowledgement.

The external sender signs `ngx.var.request_uri`; backend routing must preserve those raw path/query bytes. Local testing does not prove deployed APISIX routing, Lua compatibility in a running Gateway, Redis admission, operational key rotation, central Keycloak registration, an observed external control-plane receipt, a signed commercial contract or approved pricing. No deployment or real commercial data was changed. Full OASIS XACML/JSON Schema conformance is not claimed.
