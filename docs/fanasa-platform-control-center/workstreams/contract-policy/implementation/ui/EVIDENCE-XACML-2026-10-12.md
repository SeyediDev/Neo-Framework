# XACML UI and VPS publication — 2026-10-12

Release: `xacml-workbench-20261012-03`. User authorized VPS publication and supplied the corrected connection reference under `.users`. No credential value is included in this evidence, artifact or skill.

The new interface explains and executes the path from policy authoring/repository through request context, trusted source selection, native decision, mandatory obligations and observable effect. `/Governance` uses the existing role/time policy APIs and actual tenant sources. `/PolicyWorkbench` uses an isolated, server-owned synthetic contract profile and the actual native evaluator, projector and PEP. The profiles remain distinct and neither claims a general XML XACML interpreter.

## Implemented behavior

- Governance: revision/algorithm/immutable IDs, draft/publication/retirement, independent real publisher, copy to new draft, catalog filters, explicit missing role/unit references, authoritative simulation reasons, every obligation/advice, and invalidation of changed or late simulation results. Its claims/lifecycle activation remains disabled.
- Workbench: immutable published policies; management conflicts; independent laboratory author/reviewer labels; explicit contract binding; separate contract/policy/allocation versions; ten server-selected scenarios; original native decision and input provenance; candidate inspection cannot execute.
- Execution: native guard, admission limits and source tenant obligations, failed obligation without effect, real persisted sample-record `saveCount`, one operation/meter unit, idempotent retry, stale binding and retired-policy refusal. Record, consumption, operation and receipt are persisted together. Token refill uses the old rate before a limits revision transition.
- Protected entry: existing SSO cookie plus `PlatformAdmin`; POST antiforgery; bounded JSON and operation whitelist; fixed loopback proxy without forwarding caller authentication. Private HTML is outside `wwwroot`. Static files contain no API credentials or private state. The engine listens only on loopback.

## Verification

| Check | Result | Evidence |
|---|---|---|
| Native workbench HTTP/behavior groups | 6/6, exit 0 | `server/EVIDENCE.md`, `server/workbench.test.mjs` |
| Workbench desktop/mobile browser | 30/30, exit 0; no unexpected JS/load/external request errors | `evidence/qa-workbench-results.json` |
| Governance real Razor browser | 19/19, exit 0; no JS exceptions/external requests; 1440×1100 and 390×844 | `.build/governance-browser-qa/evidence/report.json` |
| Authenticated proxy boundary | 18/18 | `products/Fanasa.AccessManagement/tests/Workbench.Boundary.Tests` |
| Governance/architecture behavior | 10/10, exit 0 | `governance-ui.test.cjs`, `architecture-ui.test.cjs` |
| Release organization/accounting regression | 447 checks, exit 0 | Release `Organization.Tests.dll`, tool session 75682 |
| Release packaging | Exit 0, zero warnings/errors; identical native engine DLL in host and workbench; ten schemas | `release-manifest.json` in published artifact |

Screenshots were inspected at desktop and mobile sizes. Six workbench stages have no horizontal page overflow. Expected independent-review conflict is recorded separately from unexpected browser errors. Earlier workbench runs exposed a service-restart race and UI invalidation of a scenario-only allocation version; those runs failed and the final successful report supersedes them. Governance runs use a fresh disposable fixture; reusing the fixture originally exposed a test synchronization issue, corrected in the final harness.

The final change after the 30 workbench browser checks only changes session labels when the protected shell supplies its CSRF metadata; `node --check` passed. The same compiled native and Web code shipped. The logged-in positive cookie/CSRF path was exercised in a local test host; VPS checks cover HTTPS routing, anonymous denial and the actual native loopback operation.

## VPS result

Destination: `62.60.199.135`, existing development service at `platform.fanasa.net.local`.

- Active path: `/opt/fanasa-access-management/development/releases/xacml-workbench-20261012-03`.
- Prior release retained: `/opt/fanasa-access-management/development/releases/tenant-management-20261008`.
- Artifact SHA-256: `7775f4299334264ef169ff2e8fe26963734f74e2ee4700d1d86fd4037d87da0e`.
- All artifact manifest file hashes verified on the VPS before the atomic `current` switch. Existing SSO configuration, service identity, operational state and Gateway routes retained. Rollback was prepared; no rollback was needed.
- `fanasa-access-management@development.service` and `fanasa-policy-workbench.service` are active. Web is on `127.0.0.1:5191`; the isolated engine is on `127.0.0.1:4187`. Laboratory persistence is `/opt/fanasa-access-management/development/state/workbench` outside release contents.
- HTTPS Gateway check with certificate validation: `/health/live` 200, `/Governance` 302, `/PolicyWorkbench` 302, `/api/workbench` 401. Anonymous state/export/history all 401; static `/workbench/index.html` 404; workbench JavaScript loads successfully.
- Actual VPS native smoke: all four decisions, two obligations, failed resource obligation with zero effect, successful one save/one meter unit, replay with no duplicate effect. Only the isolated laboratory was reset afterwards; operational state was not reset.

Entry points: [Governance](https://platform.fanasa.net.local/Governance), [Policy workbench](https://platform.fanasa.net.local/PolicyWorkbench).

This publication deploys the UI, protected laboratory and current application build to the existing VPS development service. It does not activate the operational policy claims path, lifecycle worker, or commercial Gateway admission. Laboratory author/reviewer labels are not separate authenticated production administrators; Governance retains its actual independent administrator requirement.

## Reusable connection skill

`vps-access` is installed at `C:/Users/JavadSeyediPC/.codex/skills/vps-access/SKILL.md` and passed the bundled skill validator. It references `E:/SJVS/Projects/.users/info.txt`, keeps credentials in process memory and verifies the pinned SSH host key. Its helper was verified by actual SSH inspection and SFTP publication. The old `.vps` reference was corrected; no password is stored in the skill.
