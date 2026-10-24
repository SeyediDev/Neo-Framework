# Native workbench evidence

Historical evidence for the initial JSON-store release. The guided SQLite delivery and its current verification are recorded in [guided database evidence](../EVIDENCE-GUIDED-DATABASE-2026-10-12.md). The counts and persistence description below apply to the earlier release only.

Verified on 2026-10-12 (Asia/Tehran).

The isolated .NET 10 host builds with zero warnings and zero errors. It consumes the existing native policy DLL directly rather than implementing approximate policy evaluation in JavaScript. Its configuration-specific native reference permits deployment preparation to compare the exact native binaries used by the PCC application and the workbench.

`node --test workbench.test.mjs`: **6 tests passed, 0 failed**, using a separate host on loopback port 4197 and a separate state directory. The UI engine on port 4187 and its state were not changed by this suite. Final duration: 8.29 seconds.

The tests cover:

- All ten server-owned scenarios, actual native `Permit`, `Deny`, `Indeterminate`, `NotApplicable` outcomes, native unknown-obligation and expired-decision rejection, and PDP Permit followed by a product resource tenant refusal.
- Failed adapters consuming nothing; a retry of the same decision succeeding; six concurrent requests sharing one successful operation/consume; a process restart retaining the idempotency receipt and the actual unit saveCount.
- Administrative revision conflicts, independent reviewer labels, immutable publication with an unchanged contract binding, explicit trusted-producer projector acceptance, independent contract/policy/configuration versions, candidate execution rejection, and rejection of a decision after its accepted binding changes.
- Native Permit with local monthly-quota refusal and token-bucket refusal, preserving existing usage when allocation configuration changes, and preventing execution after policy retirement.
- A meaningful configuration transition empties a one-token bucket, allows the old rate to refill it, and then binds a much slower rate. The old-rate refill remains available; the new rate is applied only after the transition, and monthly usage is retained.
- Rejection of invented scenarios, caller-supplied attribute graphs, absent/foreign Origin, mismatched retained decision identity, unsupported failure modes, invalid typed policy input, plus audit/export visibility.

The ten schemas and the original synthetic fixture sources remain unchanged. The workbench uses copies of its two fixture files and rebases the initial in-memory validity window using server time. Native PDP `rawDecision` is retained unchanged; the unknown-obligation test passes a separate, labelled tampered copy to the native PEP.

Successful local execution atomically writes four observable effects in `workbench-state.json`: the admission counter, unit record saveCount/timestamp, successful operation and idempotency receipt. This is a flushed-file, single-process local sandbox store. It does not connect to real users, real product operations, production counters or arbitrary XACML XML policies.

The standalone engine binds only loopback. A deployed application's authenticated administrator proxy and antiforgery boundary remain responsible for protecting remote access. Engine author/reviewer labels are not authenticated identities.
