# Evidence — Contract & XACML package 1.0.0

Date: 2026-10-10 (Asia/Tehran). Scope: only `docs/fanasa-platform-control-center/workstreams/contract-policy/`.

Implemented outputs: five versioned schemas, normalized projection/context/event fixtures, four independently hand-authored golden Decision DTOs, a bounded schema validator, pure projector/evaluator, a PEP test gate, a fixture runner, parameter mapping and a source/wiring proposal. [README](README.md) describes the supported profile; [manifest.v1.json](manifest.v1.json) records the SHA-256 fingerprints for the verified code/schema/fixture/test set.

## Executed verification

Runtime observed locally: Node.js `v24.12.0`. No packages installed.

From this package directory:

```powershell
node --test --experimental-test-isolation=none --test-reporter=tap tests/contract-policy.test.cjs
node sample/run-fixture.cjs
```

Final test result (exit 0):

```text
# tests 145
# suites 0
# pass 145
# fail 0
# cancelled 0
# skipped 0
# todo 0
```

The `--experimental-test-isolation=none` option keeps the built-in test runner in the current process. Default child-process isolation encountered sandbox `EPERM`; the supported command above completed all assertions. This is an execution constraint, not a skipped test.

Fixture runner result (exit 0):

```text
fixtureOnly: true
projectionResult: applied
decisions: Permit, Deny, NotApplicable, Indeterminate
rateKey: fanasa:allocation:sample-allocation-club-b:tenant=tenant-b;products=club;environments=development;groups=reporting:rate
quotaKey: fanasa:allocation:sample-allocation-club-b:tenant=tenant-b;products=club;environments=development;groups=reporting:quota:2026-10
```

## What the assertions establish

| Requirement | Evidence in tests/contract-policy.test.cjs |
| --- | --- |
| Four Decisions | Full DTO equality against fixtures/golden-decisions.v1.json, independently authored rather than recorded from evaluator output |
| Required conditions are AND | Independent identity/client/audience/scope, tenant, membership, contract/status/time/feature, explicit grant/role, environment/group/zone negative cases |
| Purchase provides eligibility | Absent/invalid grant prevents Permit; platform.admin eligibility rejected; product assignment cannot bypass contract suspension |
| Missing or unsupported data | Missing attributes, malformed booleans/IDs/dates, unknown schema/profile/fields, stale versions/evidence and unsupported product fail closed |
| Deny precedence | A known target/action explicit Deny wins over an otherwise valid Permit and missing unrelated attribute |
| Obligations | Permit requires exactly one of each obligation even in schema-only validation; unknown/duplicate/missing/unfulfilled/malformed obligations, changed allocation/resource and expired/overlong deadline are rejected |
| Stable allocation | Canonical scope sorting and delimiter/type checks; amendment/price/rate/quota changes preserve rate/month keys; wrong scope/period/type rejected |
| Replay/order/suspend/end | Canonical envelope fingerprint, conflicting ID/payload and same-version conflict, receipt for equivalent new event ID, stale replay, full-snapshot gap, terminal expiry and forbidden resume |
| Scheduled change versus revoke | Newer immediate suspension supersedes an older future-effective amendment; evaluation remains Deny now and later, and replay cannot reactivate it |
| Pure sample state | Frozen inputs and mutated returned copies demonstrate no input writes; evaluator has no counter/admission/usage/grant/ledger mutation |

These are deterministic offline behavioral tests, not a performance benchmark, concurrency/store test, or deployed product acceptance.

## Source and limits of delivery

[SOURCE-AND-WIRING.md](SOURCE-AND-WIRING.md) records exact current source anchors, direct-grant/runtime versus governance-simulation semantics, receipt/timestamp-ordering gaps, product-role versus organizational-role distinction, product inventory gap and proposed interfaces within the existing PCC module boundary. Existing runtime tests were inspected as source evidence; this delivery did not rerun or activate that host.

The synthetic `club/report.read` pilot is pinned to `fanasa-club-api`; another product needs a reviewed profile with the real registry key and audience. No Managed Club registration, live grant, producer authentication, deployed policy or backend acceptance was created or verified.

The following remain integration acceptance work: source/artifact reconciliation (including historical 8f13), authoritative registry/offer/subscription/PIP readers, authenticated producer and atomic inbox/projection history, policy publication/distribution/acknowledgement, bounded invalidation/cache freshness, live PDP/PEP and resource authorization, seat admission, shared TPS/burst/quota store and Usage/Accounting connection. The sample does not implement counters, budget, ledger, Gateway, lifecycle business schema or pricing.

Only the assigned package was edited. Program.cs, registry, persistence, migrations, ARCHITECTURE.md and GROWTH-AND-DEVELOPMENT-PLAN.md were left for their owners. No source was copied from 8f13. No full XACML/JSON/REST or general JSON Schema conformance is claimed.

