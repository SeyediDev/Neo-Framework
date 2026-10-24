# Native contract policy evidence — module 0.1.0

Date: 2026-10-10, Asia/Tehran. Authorized development scope is the existing contract-policy workstream. This delivery adds the standalone .NET module under `implementation/`; existing v1/v2 schemas, fixtures, reference JavaScript, historical evidence and manifests were preserved. No shared host, Program.cs, registry, database, migration, architecture document or deployment was changed.

## Delivered and built

`Fanasa.ContractPolicy` targets `net10.0`, has no external package dependencies and uses nullable checks and warnings as errors. It exposes typed projection/context/decision/event DTOs, a startup-compiled bounded local-schema validator, a pure decision evaluator, a pure snapshot projector, a PEP decision helper and `ContractPolicyModule` composition. The executable regression project uses the existing fixtures and schemas copied to its output.

Verified environment: .NET SDK `10.0.401`, MSBuild `18.9.11`, Microsoft.NETCore.App runtime `10.0.12`, Windows x64. The initial library build completed with **0 warnings and 0 errors**:

```powershell
dotnet build docs/fanasa-platform-control-center/workstreams/contract-policy/implementation/src/Fanasa.ContractPolicy/Fanasa.ContractPolicy.csproj --nologo --verbosity minimal
```

The final command rebuilt the library and regression executable, emitted no compiler warnings/errors and exited **0**. It ran from `E:/SJVS/Projects/Neo`, after the initial successful restore/build:

```powershell
dotnet run --project docs/fanasa-platform-control-center/workstreams/contract-policy/implementation/tests/Fanasa.ContractPolicy.Tests/Fanasa.ContractPolicy.Tests.csproj --no-restore --verbosity minimal
```

Exact final result:

```text
Contract policy .NET: 241 passed, 0 failed.
```

## What the checks establish

- Complete v1 Permit/Deny/NotApplicable/Indeterminate DTOs match independently authored golden fixtures, including nulls, references and obligations. The complete v2 decision matches the current integration fixture.
- Contract, identity/client/audience/scope, tenant, membership, explicit grant, action, allocation scope and zone remain required gates. Buying eligibility creates no grant or administrator role. Matching explicit Deny preserves its precedence.
- Permit lifetime is bounded by contract, membership, grant and evidence expiry and by 60 seconds in v1 or 30 seconds in v2. Missing/unknown/duplicate/unfulfilled obligations, invalid bindings and expired decisions cannot pass the PEP helper.
- Allocation identity and canonical rate/month keys remain stable across revisions and suspension. The v2 allocation configuration revision is independent of contract and policy versions.
- Projector checks cover authenticated-producer binding, replay, equal-version conflicts, stale events, terminal Ended subscriptions, full-snapshot version gaps and immediate suspension after a future-effective amendment. State/receipts/obligations are independent copies.
- Strict JSON/schema checks cover unknown fields, duplicate properties including escaped names, unsupported/cyclic/missing local schema references, contains cardinality, depth/node limits, UTC timestamps, safe numbers and required constructor parameters. Integer forms `7.0`/`7e0` and nullable revision `1e0` deserialize correctly.
- Eighteen regression cases cover externally parsed lazy duplicate-property JsonNodes. Schema/projection validation returns errors, evaluation returns a schema-valid non-Permit decision, and PEP returns false; caller input serialization remains unchanged.
- A shared set of bootstrap services produced deterministic decisions/projector results in 64 concurrent calls. This checks in-process pure services, not multiple-writer persistence or distributed admission.
- Golden v1/v2 receipt fingerprints are checked against the current Node reference. This is fixture compatibility evidence, not general canonical-JSON conformance.

## Reference package verification

The unchanged reference checks were also rerun from the parent contract-policy directory:

```powershell
node --test --experimental-test-isolation=none --test-reporter=tap tests/contract-policy.test.cjs tests/integration-policy.test.cjs
node sample/generate-integration-fixture.cjs --check
```

Both exited **0**. TAP reported **171 tests, 171 pass, 0 fail**, with zero cancelled/skipped/todo. Regeneration confirmed all four persisted v2 integration fixtures and decision SHA-256 `17eebeb459c6c58d3ed5d82af64b15c55eb1cde8b8664e27e40c26fffca75ac3`.

## Deliberate native boundary corrections

After validated stable identity, an older aggregate is recorded as stale before allocation revision comparison. A valid old full snapshot cannot roll back state or be treated as a new configuration amendment. The current JavaScript reference checks allocation revision first. Native identifier and month anchors reject trailing newline, which JavaScript's historical `$` anchor can accept. Strict raw snapshots also prevent lazy duplicate-property DOM input from escaping through an exception. The reference package and historical manifests were not rewritten for these corrections.

## Integration limits and next step

[README.md](README.md) provides project-reference and DI wiring for the existing PCC host. A trusted contracts adapter must authenticate its producer and map approved business events; a PIP must supply actual identity, tenant, membership, grant, policy/projection versions, execution binding and server time. Persist returned state and receipts atomically with concurrency control. Actual PEP adapters must execute admission and backend resource checks; completion ID lists here do not prove those operations occurred.

The profiles and fixtures remain synthetic. No live product registration, contract provisioning, token verification, host activation, database inbox/outbox, invalidation, Gateway/limiter, seat admission or Accounting integration is established by this delivery. The schema validator implements the package's bounded subset; the module does not claim full JSON Schema or XACML conformance. Caller mutation of the same input during a call remains unsupported.

[manifest.v0.1.0.json](manifest.v0.1.0.json) records SHA-256 for 14 native source/build/test/guide files and 18 existing schema/fixture inputs. Paths are relative to `implementation/`. Generated bin/obj output, this evidence file and the manifest itself are excluded; this is a source delivery manifest, not a release or deployment artifact. No Skill or MCP contract is changed by this standalone module, so no external skill/tool files required edits.
