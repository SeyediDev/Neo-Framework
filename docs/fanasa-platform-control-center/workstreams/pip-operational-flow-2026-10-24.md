# PIP operational flow — reviewed local delivery, 2026-10-24

Status: implementation and targeted local verification complete. **Not merged, pushed, published or activated on the VPS.** Central release owner retains sole Git and shared deployment ownership. This document is a handoff artifact; it does not authorize deployment, access grants or changes to shared Gateway configuration.

## Exact delivery

- Isolated checkout: `C:/Users/JavadSeyediPC/.codex/worktrees/pip-operational-flow/Neo`
- Package: `C:/Users/JavadSeyediPC/.codex/worktrees/pip-operational-flow/Neo/.build/pip-flow-20261024/pip-operational-flow-20261024.zip`
- Package SHA256: `ea9eaf3fec65cfcd6f335731ae2d1b83bfc13d5fc04a20c842fd3b3a85429a43`
- Source identity: 620 files, aggregate `19c29056db206a75e458dc8a10251f32033dab3be685757e44f2aa9f43e6fa5d`.
- Delta: **41 files** relative to immutable `E:/SJVS/Projects/Neo/.build/pip-operational-local-20261023/build-source` (606 files). README was outside that source set; its base is Git `e639ab0d99a2f889fa2a22dd8da159032360638e` and is separately identified in the manifest.
- Package contains `SOURCE-DELTA.json`, full changed files in `files/`, review patch, both verification source snapshots, logs, screenshots and `VERIFICATION.json`.
- Verify destination base hashes before applying. A mismatch needs review/merge; do not overwrite central changes. The broad Git diff of the C checkout includes the previous baseline overlay and is **not** this wave's delta.

## Implemented behavior

- Dedicated HR attributes reader with signed JWT, actual Gateway mTLS, exact client/subject/scope/tenant registration, active membership, live read grant and live subscription checks before/after projection in one Fabric snapshot. Bootstrap never depends on the PIP facts it supplies.
- Schema 2 carries authoritative `validUntil`; freshness is capped by observation TTL and appointment start/end. Schema 1 hashes are unchanged. Actual organization facts are membership/assignment plus optional unit/position/role for one effective assignment; employment is never inferred from membership.
- Deployment-scoped OAuth renewal and per-binding CA trust preserve exact destination/IP checks; no redirect, cookies, proxy or certificate AIA downloads.
- Disabled registration → contract test → current receipt activation → independent synchronization → per-subject diagnostics → policy simulation. Test receipts cannot become authorization snapshots. Generation changes invalidate activation/cache/checkpoints; late old runs cannot replace current checkpoints.
- Deadline-aware scheduling, per-subject health/provenance/deadlines and actionable UI. All six HR schema fields are included in the form; a contract regression compares the form with the authoritative projector definitions.
- Typed equals/not-equals, missing/stale fail closed, decision configuration/evidence without fabricated rule traces, 15 durable synthetic educational scenarios preserving previous examples and calendar scenarios. Educational policies remain draft, `Enforce=false`, without grants or external HTTP.
- CI/archive UI floor 44; additive Gateway source route, OpenAPI and Persian operations documentation. No live shared Nginx changes.

## Verification

| Suite | Passed |
|---|---:|
| Organization.Tests | 729 |
| Organization.Hr.Tests, actual local TLS/JWT/HTTP | 201 |
| Organization.Temporal.Tests | 141 |
| Organization.Calendar.Tests | 62 |
| Four organization/PIP UI suites | 44 |
| CI workspace and archive verification Python | 28 |

All four .NET builds: zero warnings/errors. These are targeted local receipts, not a full release-pipeline pass. After .NET verification, browser testing found one omitted optional HR field in the UI preset; only `organization.js` and its UI test changed. All 44 UI tests were rerun successfully, and source identities prove backend/build/CI bytes stayed identical. Before/after the final UI run, the full source identity is stable; both snapshots are included.

Browser: all 15 educational scenarios matched, scenario deep link populated exact subject/action/time/draft choice, explicit simulation returned Permit with calendar and PIP evidence, unapproved source registration stayed disabled, test/enable buttons were blocked, step links targeted actual controls, all six HR fields appeared, no console errors, and 390px layout had no horizontal overflow. OpenAPI was manually reviewed; a YAML parser was unavailable.

Preview (process 12640, disposable DB, loopback synthetic identity):

`http://127.0.0.1:5212/Organization?tenant=e78d248c-ab8e-4ca9-b8de-e87916324c38#organization-pip`

## Remaining coordinated release work

1. Central owner reviews/applies this exact delta against its current commit, resolves any drift and runs its full GitLab release gates.
2. Configure a dedicated tenant-scoped reader identity, exact read scope, active organization subscription, bounded read grant, secret reference, OAuth profile, CA and destination binding per `deploy/fanasa-organization-hr/PIP-ATTRIBUTES.md`. No identity/grant/secret was provisioned by this delivery. The expired previous HR development grant was not renewed.
3. Gateway owner integrates the additive exact route without overwriting shared Nginx. Deploy centrally; run live allow/deny, cross-tenant, expiry, restart and HR→PIP→PDP checks with the configured source.
4. Seed/validate the educational journey in the deployment's existing template tenant through its authorized endpoint. The preview tenant GUID is temporary and must not be used as a live identity.

No live connectivity or production-readiness claim follows from the synthetic tests. Existing live databases, grants, SSO, PVC and main E checkout source/index were preserved; only this handoff and a local packaging helper were added outside the isolated development checkout.
