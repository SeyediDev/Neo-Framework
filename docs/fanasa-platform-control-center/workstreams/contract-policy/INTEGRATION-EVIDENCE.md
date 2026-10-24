# Policy compatibility evidence — package 2.0.0

Date: 2026-10-10, Asia/Tehran. Authorized scope: only `docs/fanasa-platform-control-center/workstreams/contract-policy/`. The original [EVIDENCE.md](EVIDENCE.md), v1 schemas/fixtures and regression test file remain unchanged. No applicable ancestor `AGENTS.md` was present. The [alignment task](../../INTEGRATION-ALIGNMENT.md) supplied the scope and invariants.

The actual normalized projector/evaluator generated [integration-decision.v2.json](fixtures/integration-decision.v2.json) from the common synthetic binding, plus its adjacent projection/context/event. It is schema `2.0.0`, profile `fanasa.contract-policy/2`, `Permit`, request `g-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa`, evaluated at `2026-10-10T06:29:59.000Z`, valid until `2026-10-10T06:30:29.000Z`, and includes both original obligations. Admission parameters contain the independent `configRevision: 1`. Its exact SHA-256 is `17eebeb459c6c58d3ed5d82af64b15c55eb1cde8b8664e27e40c26fffca75ac3`.

## Executed commands and outputs

Runtime command `node --version` returned `v24.12.0`, exit 0. No dependencies were installed. Commands below ran from this package directory.

```powershell
node sample/generate-integration-fixture.cjs
```

Exit 0. The script emitted a JSON bundle for `integration-projection.v2.json`, `integration-context.v2.json`, `integration-projection-event.v2.json` and `integration-decision.v2.json`. The coordinator persisted the actual output using its workspace file tool. Direct Node filesystem writing initially returned sandbox `EPERM` while creating the new schemas; the file tool completed those authorized writes. No schema or check was skipped.

```powershell
node sample/generate-integration-fixture.cjs --check
```

Exit 0, exact output:

```json
{
  "fixtureOnly": true,
  "generatedFixtures": [
    "integration-projection.v2.json",
    "integration-context.v2.json",
    "integration-projection-event.v2.json",
    "integration-decision.v2.json"
  ],
  "decision": "Permit",
  "schemaVersion": "2.0.0",
  "requestId": "g-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
  "configRevision": 1,
  "decisionSha256": "17eebeb459c6c58d3ed5d82af64b15c55eb1cde8b8664e27e40c26fffca75ac3"
}
```

```powershell
node --test --experimental-test-isolation=none --test-reporter=tap tests/contract-policy.test.cjs tests/integration-policy.test.cjs
```

Exit 0. Original 145 assertions passed unchanged; the new integration test file added 26. Final TAP result:

```text
1..31
# tests 171
# suites 0
# pass 171
# fail 0
# cancelled 0
# skipped 0
# todo 0
```

The new checks exercise the actual generated Decision against the common tuple; literal UUID tenant/principal strings and unchanged key/nonce syntax; organization audience, membership, tenant, grant/action and stale evidence gates; required positive independent allocation revision; conflict on changed limits at the same revision; rejection of stale revisions; unchanged rate/month counter keys across revisions, policy/contract amendments and suspension/resume; zero quota; the October UTC calendar boundary; both obligations; unsupported multiple policy references/period; deadline and short PIP evidence enforcement. The original four full golden Decision checks and replay/order/suspend/end checks remain intact. Frozen inputs and returned copies preserve pure evaluation/projecting.

## Changed files and fingerprints

Modified: `README.md`, `sample/contract-policy.cjs`.

Added: `INTEGRATION-EVIDENCE.md`, `manifest.v2.json`, five `schemas/*.v2.schema.json` files, four `fixtures/integration-*.v2.json` files, `sample/generate-integration-fixture.cjs`, `tests/integration-policy.test.cjs`.

The current code/schema/fixture/test/document SHA-256 entries are in [manifest.v2.json](manifest.v2.json), including the common binding file as an external input. `manifest.v1.json` retains its original historical hashes; original v1 schemas, fixtures and regression checks were not rewritten to make them fit the new tuple. No Skill or MCP tool contract references this offline package, so no external Skill/MCP files required changes.

## Acceptance limits

This evidence establishes the policy package's generated predecessor output and local fail-closed checks. Cross-package consumption belongs to the Gateway/Accounting integration checks and their integration report. The artificial `organization-fabric` binding is not a live registered product or contract. Producer/PIP authentication, token verification, shared authoritative allocation provisioning, native APISIX/Redis/TLS, data-tenant backend authorization, live rate provenance and production wiring remain pending. The evaluator writes no counters, admission witness, usage or debit; admission and committed-business billing meters remain separate. No shared runtime, registry, persistence, Program.cs, deployment or protected companion instructions were edited.
