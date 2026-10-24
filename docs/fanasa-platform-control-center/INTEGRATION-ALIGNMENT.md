# Alignment of the three local architecture packages

This is the next authorized task for the three existing architecture packages. It is a local compatibility correction, not a deployment or a new runtime. Keep the three package directories separate. Shared application wiring still belongs to the coordinating chat. If in-app continuation cannot start these owners, the bounded [integration recovery chat task](/E:/SJVS/Projects/Neo/docs/fanasa-platform-control-center/INTEGRATION-RECOVERY-TASK.md) explicitly takes over only these compatibility corrections.

The delivered packages pass their separate fixture suites, but their Decision formats, obligations, identity constraints and operations differ. They cannot yet be treated as one accepted path. Use the existing policy owner's DTO as the decision source; the Gateway's earlier XACML-shaped envelope is a provisional fixture, not a second central contract.

## Common fixture

Use [fixture-binding.v1.json](/E:/SJVS/Projects/Neo/docs/fanasa-platform-control-center/integration/fixture-binding.v1.json) for one synthetic organization-unit-save scenario. The identities, contract, product binding and rate are explicitly artificial. Do not register this fixture as a real product, create grants/contracts, or publish routes. This test demonstrates compatibility only; real-product/contract acceptance remains separate.

The admission meter is `api.request.admitted` in units of requests. The billable meter remains `organization.change` in units of committed changes. They measure different boundaries and must not be equated. Permit or successful admission alone does not create a charge.

## Fixed mapping and invariants

| Source | Target / invariant |
| --- | --- |
| Policy `decision`, `obligations` | Gateway consumes the policy DTO through its existing adapter. HTTP 200 alone never forwards. Only Permit with both supported obligations may proceed. |
| `urn:fanasa:obligation:admit-allocation:1` | Atomic TPS/burst/quota admission for its exact allocation. Unknown, duplicate, missing or malformed obligations fail closed. |
| `urn:fanasa:obligation:resource-tenant:1` | Match consumer/product/environment/action to trusted request and route binding. Preserve this context for backend PEP. Actual data-tenant enforcement is the backend's responsibility and remains a runtime acceptance requirement. Do not remove the obligation. |
| `allocationReference.allocationId`, limits `allocationId` | Gateway allocation id and UsageEvent `allocationId` agree exactly. |
| limits `canonicalScope` | Gateway allocation scope is this exact canonical scope, without narrowing it to a route or product. |
| `rate.requestsPerSecond`, `rate.burstCapacity`, `quota.requests` | Gateway TPS, burst, quota. A zero quota denies all admissions; absent/invalid limits do not become unlimited defaults. |
| Independent authoritative allocation `configRevision` | Positive, monotonic revision of allocation configuration. Include it in the owner's projection/limits contract or a validated provisioning binding. Never infer it from policyVersion/contractVersion. Equal revision with different limits conflicts; stale revision is rejected. |
| `fixed-calendar-utc` / `month` | UTC calendar bounds for the applicable instant: October 2026 is `[2026-10-01, 2026-11-01)`. Usage `allocationPeriodId` is `2026-10`. Contract amendments, suspend/resume, policy revisions and retries do not reset these counters. A new period must be provisioned by Control Plane. |
| `evaluatedAt`, `validUntil` | Parse ISO times to epoch safely. Gateway deadline is at most the policy deadline, evaluatedAt + 30 seconds, and token expiry. Do not round a deadline up. Recheck before forwarding. |
| Gateway correlation nonce | Prefix its cryptographic nonce with `g-` to meet the policy requestId syntax. Keep it distinct from the business operation UUID. |
| Policy `requestId` | For this one-evaluation-per-unique-nonce slice, it is the Decision reference used as Usage `authorization.decisionId`. No retry/re-evaluation may silently replace the evidence behind an existing reference. No fabricated decisionId field is required. |
| Confirmed admission witness | Gateway returns `admissionId = "admission:" + requestId` only after an unambiguous allowed/OK result, with the allocation/period/config binding. Timeout, exception, unknown result or provisioning acknowledgement provides no successful admission witness. Reference strings alone are not authorization evidence. |
| The single selected `policyReferences` entry | Usage `authorization.policyVersion = policyId + "@" + policyVersion`. This fixture contains exactly one policy. Reject an unhandled multi-policy reference; never select the first item and discard the rest. The wider architecture may compose policies after its reference mapping is defined. |
| `projectionReference.contractId/contractVersion` | Usage contract reference, with explicit range validation: contractVersion must fit 1..2147483647. No truncation or wrap. |
| Registered product binding / fixture productId | Usage `productKey`. No alternative product catalog or identity alias registry. |
| Policy identity fields | Accept real PCC/Keycloak UUID identity strings where they represent tenants/principals. A leading-letter slug rule must not reject an otherwise valid UUID. Do not rewrite UUIDs into invented aliases. Keep requestId and product-key constraints distinct from identity constraints. |

DTO schema versions remain separate from policy/contract versions and allocation revisions. If a DTO changes, update its declared version and manifest/evidence according to the owning package. Do not claim full OASIS XACML conformance from these custom DTOs.

## policy

Owner: Fanasa Contract & XACML. Edit only its contract-policy directory.

1. Add the common organization-operation fixture using the common binding and both required obligations. Preserve the existing regression fixture if useful.
2. Resolve UUID identity acceptance and authoritative allocation configRevision in the schemas/projector/evaluator sample. Keep evaluation side-effect-free; no counter, ledger, server or allocation provisioning inside PDP.
3. Emit one reusable Decision fixture file generated by the evaluator. Document its exact schema version and file path for Gateway/Accounting. Keep the nonce/reference, period and policy reference rules above explicit.
4. Run only the affected schema/evaluator checks and record hashes/evidence. Report the generated output, not merely another independent Permit example.

## gateway

Owner: Fanasa API Gateway. Edit only its api-gateway directory in NeoCluster.

1. Adapt the existing PEP to consume the owner's policy DTO and support both actual obligation IDs. Remove the requirement to fabricate absent grantVersion/identity echo fields from the old envelope; authenticate the PDP transport, correlate the nonce and validate the actual trusted allocation/resource bindings.
2. Use the common fixture and consume the Decision produced by the policy package. Until that output exists, prepare the mapping and explicitly mark the dependent interoperability check pending; do not declare a handcrafted replacement equivalent.
3. Map period/revision/TPS/burst/quota with the invariants above. Return the confirmed admission witness/reference through authorized context for the backend.
4. Add a small boundary test proving cross-package Permit/admission and negative cases for omitted resource obligation, wrong consumer, unsupported period/revision, expired decision and ambiguous admission. Preserve existing allocation-sharing/no-reset tests. Native APISIX/Redis/TLS/deployment acceptance remains pending.

## accounting

Owner: Fanasa Usage & Accounting. Edit only its usage-accounting directory.

1. Add one compatibility fixture for the common binding without changing the existing billable boundary or deduplication rules. Use the single-policy reference syntax and Decision/admission reference mapping above.
2. Consume the actual policy/Gateway fixture output once available. Validate owner/consumer/product/contract/allocation/period against it; do not make an authenticated reference true merely because it is a nonempty string.
3. The synthetic backend must provide committed business evidence and the existing artificial rate snapshot before its one intended debit. Deny, absent/unknown admission and uncommitted outcomes must not create a debit. Replayed committed evidence produces the same intended debit once.
4. Add only the necessary cross-package checks and update evidence/hashes. Live rate provenance, producer authentication and runtime wiring remain pending.

## Coordinating acceptance

The coordinator reviews the generated artifacts and one shared tuple across all three packages. The existing independent-suite counts are historical package checks, not evidence of this compatibility task. Until the common boundary checks pass, mark cross-package compatibility pending. Do not modify shared Program.cs, registry, persistence or production as part of this task.
