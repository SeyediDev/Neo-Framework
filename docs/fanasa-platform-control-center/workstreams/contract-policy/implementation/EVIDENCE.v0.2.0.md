# Independent adapter evidence — native module 0.2.0

Date: 2026-10-10, Asia/Tehran. This continuation remains entirely within the contract-policy workstream's `implementation/` directory. Existing PCC authentication/source was inspected read-only. No shared Program.cs, registry, persistence, migration, architecture, business contract model, Gateway, accounting or live grants were changed.

## Delivery

Added [ContractEventAdapter.cs](src/Fanasa.ContractPolicy/ContractEventAdapter.cs), [TrustedAccessSnapshotProvider.cs](src/Fanasa.ContractPolicy/TrustedAccessSnapshotProvider.cs), [AdapterTests.cs](tests/Fanasa.ContractPolicy.Tests/AdapterTests.cs) and [ADAPTERS.md](ADAPTERS.md). `Program.cs` in the independent regression project now registers these tests through the existing test counter; it is not the PCC Program.cs. `Directory.Build.props` sets native version `0.2.0`, and [README.md](README.md) points to the current guide/evidence. No external package dependencies were added.

The original [EVIDENCE.md](EVIDENCE.md) and [manifest.v0.1.0.json](manifest.v0.1.0.json) are preserved as historical records. Their original source hashes are not asserted to match this extended working copy. Contract schemas/profiles remain at v1/v2 with no schema edits.

## Final verification

Environment remains .NET SDK `10.0.401`, Microsoft.NETCore.App `10.0.12`, Windows x64. The final command from `E:/SJVS/Projects/Neo` rebuilt the library and regression executable, emitted no warning/error output and exited **0**:

```powershell
dotnet run --project docs/fanasa-platform-control-center/workstreams/contract-policy/implementation/tests/Fanasa.ContractPolicy.Tests/Fanasa.ContractPolicy.Tests.csproj --no-restore --verbosity minimal
```

Exact final output:

```text
Contract policy .NET: 344 passed, 0 failed.
```

The total includes **241 existing core cases** and **103 new adapter cases**. Warnings remain errors in project configuration. No code/test source was edited after the final successful run.

## Producer authorization and event tests

Synthetic authenticated principals test exact issuer/client/scope binding, legitimate multiple audience claims, missing/wrong/duplicate claims, unauthenticated identities, ambiguous authenticated identities, conflicting client_id, case-sensitive raw claim names, scope substring/tab rejection and HTTPS Fanasa realm configuration. An added unauthenticated Fabric identity can neither supply nor replace the selected producer's security claims.

The event body cannot authorize its producer. Authorization precedes strict bounded JSON parsing, and the projector independently compares the body producer with the authorized producer binding. Tests cover malformed/missing/unknown/oversized payloads, replay, conflict, stale allocation/configuration snapshots, newer suspension followed by old Active events and independent rejected-state copies. No event adapter path writes state or receipts.

These tests prove authorization behavior **after host authentication**; synthetic ClaimsPrincipal construction does not verify JWT signatures, token lifetime or a live Keycloak producer. The existing PCC Bearer pipeline must perform those checks. Its developer-client registry policy is not producer authorization. Dedicated client/scope registration remains host configuration to supply, not provisioned by this delivery.

## PIP freshness and correlation tests

The injected synthetic reader produces the complete current v2 golden decision. The provider correlates subject/consumer/product across tenant, membership, explicit grant and accepted projection, and verifies evidence contract/policy versions. Tests preserve inactive authority data for a normal PDP Deny and prevent administrator or grant fabrication.

Server time is sampled after the awaited read; missing, expired, future-dated, unavailable or conflicting snapshots cannot return a usable context. Source expiry, configured observation age and 30/60-second profile ceilings bound the evidence. Full-tick freshness comparisons and canonical millisecond output are checked independently. Caller cancellation propagates; unrelated source cancellation/error fails closed, without a cached Permit fallback.

`AuthenticationValidUntil` is required from validated host token/client-policy lifetime. New checks reject malformed/missing/expired deadlines, bound both PDP and PEP lifetime to earlier identity expiry, prevent source mutation from extending the retained deadline and reject authentication expiring during an awaited lookup. No infinite authentication lifetime is assumed.

Query/source/output isolation, oversized authority graphs and negative versions/consumer/subject/route binding are covered. Reader implementations and principals are explicitly synthetic; there is no new persistent PIP source or proof of cross-store transactional consistency.

## Source manifest and preserved inputs

[manifest.v0.2.0.json](manifest.v0.2.0.json) records SHA-256 for **18 native source/build/test/guide files** and **18 existing schema/fixture inputs**. Evidence, manifests and generated bin/obj/results are excluded to avoid self-referential hashes. This is a local source manifest, not a deployed artifact.

The existing 18 schema/fixture fingerprints match the earlier native manifest. The reference JavaScript/schema/fixture package was not changed. Its **171-pass** run and exact v2 fixture regeneration remain the previous-stage verification in [EVIDENCE.md](EVIDENCE.md); they were not rerun for this native-only adapter addition.

## Remaining integration

[ADAPTERS.md](ADAPTERS.md) gives current API examples and the PCC wiring proposal. Activation requires the registered producer binding and successful existing Bearer authentication, approved business mapping, authoritative accepted-state/PIP readers, atomic state/receipt persistence with version control, and request-bound PEP obligation execution. Shared invalidation, real registry/tenancy reads and a real product/API acceptance test are still host integration work. No full JSON Schema/XACML conformance, live enforcement, distributed storage/admission or JWT validation is claimed here. No Skill or MCP contract changed, so no external skill/tool edits were required.
