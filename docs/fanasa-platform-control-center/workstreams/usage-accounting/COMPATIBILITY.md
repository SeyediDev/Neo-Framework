# Local policy → admission → committed usage compatibility

This is the bounded local correction in [INTEGRATION-ALIGNMENT](../../INTEGRATION-ALIGNMENT.md). The product, organization identities, contract and 12 IRR rate are synthetic. Nothing here registers a live product, creates a contract/grant or installs a runtime accounting API.

The input chain is the shared [binding](../../integration/fixture-binding.v1.json), the policy evaluator's generated [Decision 2.0.0](../contract-policy/fixtures/integration-decision.v2.json), and the Gateway PEP/limiter's generated [confirmed admission](../../../../../NeoCluster/fanasa-platform-workstreams/api-gateway/fixtures/integration-admission.v1.json). Accounting does not construct a substitute Permit or successful admission. A missing predecessor file leaves the dependent check pending.

The existing UsageEvent schema stays at `1.0`: its fields and meanings have not changed. Policy DTO `2.0.0`, policy version `3`, contract version `7`, allocation revision `1` and rate version `7` are independent values. The common event uses contract `fixture-contract-b`, allocation period `2026-10`, authorization `decisionId = Decision.requestId`, `admissionId = admission: + requestId`, and the single policy reference `fixture-organization-unit-save@3`. A Gateway nonce beginning `g-` remains separate from the business and attempt UUIDs.

[IntegrationCompatibility.cs](sample/IntegrationCompatibility.cs) validates the actual predecessor tuple against the common binding, including exact owner/consumer UUIDs, the Gateway subject UUID as the operation actor, product/environment/action, contract, allocation/canonical scope, independent config revision, UTC calendar bounds, both required obligations and the preserved backend resource context. The witness deadline must be at most Decision validity, evaluation plus 30 seconds and token expiry. Contract versions must fit `1..2147483647`; policy versions and allocation revisions must be positive exact safe integers. Fractional/overflow values, unhandled multiple policies and nonempty unresolved reference strings are rejected. No alias identity catalog is introduced.

The admission meter is `api.request.admitted` in requests. The charge meter is `organization.change` in changes. Successful admission cannot debit the ledger. The synthetic backend must first commit and flush its existing `BackendCompletion` evidence, and intake must resolve it by the same business key and immutable operation fingerprint. The original artificial historical snapshot then gives exactly 12 IRR. The guard is an optional sample validation seam in the existing `ProposedUsageIntake`, after receipt/operation deduplication and before any new accounting effects. It adds no storage engine or accounting framework.

The original logical key and financial deduplication rules are preserved. Identical delivery replay returns its durable result. A new delivery/attempt for the same business operation creates only an alias receipt. Current policy/admission state is not re-evaluated behind an existing immutable financial receipt; replay of that receipt cannot create another debit. A new operation still requires complete evidence. Authorization validity is checked at historical backend acceptance, so delayed accounting does not confuse an expired current wall-clock deadline with an invalid past authorization.

From `E:/SJVS/Projects/Neo`:

```powershell
& ./docs/fanasa-platform-control-center/workstreams/usage-accounting/sample/run-integration.ps1
python -B docs/fanasa-platform-control-center/workstreams/usage-accounting/schema-check/validate.py --event docs/fanasa-platform-control-center/workstreams/usage-accounting/fixtures/integration-usage-event.v1.json
```

The wrapper requires the actual generated predecessor files and creates a fresh directory below `sample/results/`. Build outputs remain in the sample; local NuGet audit is disabled exactly as in the existing harness. [Integration evidence](EVIDENCE.md) records the executed command, results, source/input/output hashes and generated files. The original two-operation reconciliation remains a historical regression suite, separate from the one-operation common compatibility proof.

Remaining acceptance includes authenticated producer/PDP/Gateway evidence resolution, real historical rate provenance, durable registry/contract provenance, real backend data-tenant authorization and product runtime wiring. Native APISIX/Redis/TLS, deployment, financial period operations, settlement and revenue sharing remain outside this local sample.
