# Native policy workbench

This is an isolated host of the existing `Fanasa.ContractPolicy.dll`. It references the already built native assembly, and copies the ten existing schemas plus two synthetic fixture files into its own output. It does not build or mutate the source policy module, its fixtures, the PCC backend, or the production gateway.

The native reference uses the same `$(Configuration)` as this host. The corresponding native DLL must already exist before a standalone build; deployment preparation builds the main PCC application in Release first, then publishes this host in Release, and verifies equal native DLL hashes. A workbench build itself has no ProjectReference and never rebuilds that shared module.

The workbench serves its API on `127.0.0.1:4187`. The UI host on port 4186 proxies `/api/workbench`; the deployed application must protect that proxy with its own authenticated administrator session and antiforgery protection. This engine supplies no user authentication. Author and reviewer values are demonstration labels; differing labels demonstrate separation of duties, not proof of two authenticated people. No CORS policy is enabled. POST requests require an exact local/configured Origin and JSON, with a 32 KiB body limit. Unknown command fields are rejected.

Build and run from this directory:

```powershell
dotnet build Fanasa.ContractPolicy.Workbench.csproj
dotnet bin/Debug/net10.0/Fanasa.ContractPolicy.Workbench.dll
```

Set `WORKBENCH_DATA_DIR` to a persistent directory outside a deployment release. `WORKBENCH_ORIGIN` can specify the exact protected application origin. `WORKBENCH_PORT` changes the loopback port for isolated tests. The store is a SQLite `workbench.db` using WAL, full synchronous commits, foreign keys, row constraints and a durable state checksum. Policies, drafts, trusted context, records, decisions, operations, receipts and audit entries have persisted tables. A transaction commits the product effect, metering and receipt together. A mutation version also protects concurrent engine instances, independently of the management revision. This isolated workbench is not a distributed commercial admission counter.

On the first database startup, an existing `workbench-state.json` is imported only after full definition and effect validation; the original file is retained unchanged. A missing trusted context in a legacy export is backfilled once from the verified sample source using the accepted validity windows. Subsequent requests read the persisted context from SQLite. Existing invalid databases or legacy files are rejected; the server never silently replaces them with fresh examples.

Every read and command rechecks SQLite integrity, foreign keys, normalized-column/JSON agreement and the full state checksum. Native schemas, policy/binding agreement, trusted identity/membership/grant references, stored native decision replay and operation/receipt/usage/saveCount coherence are validated for each distinct full-state SHA-256; the last two successful exact-content validations may be reused. Current readiness, request evaluation and PEP deadlines are never cached. A valid expired or revoked membership is still coherent data and produces a denied request. Corruption returns `storage-invalid` (503) and blocks decisions and effects. Repair requires the operator to diagnose/restore the isolated database; `/reset` cannot bypass corruption checks. A native regex deadline is also fail-closed and returns a structured 503 response; its native guard remains unchanged.

`GET /api/workbench` returns policies, drafts, the accepted native projection and projector receipts, the server scenario catalog, attribute sources, engine/schema hashes, counters, saved unit records, successful operations and audit history. `/history` and `/export` provide read-only views.

`GET /api/workbench/readiness` reports current storage integrity, native definition checks, actual binding, UTC validity windows, persisted identity/membership/grant, target/scope/zone, ownership and remaining admission capacity. `ready` describes the baseline request's ability to succeed; `canEvaluate` remains true for coherent denial settings so their result can be inspected. `/health` returns 503 for invalid storage, otherwise 200. Readiness is guidance; the native evaluator and PEP recheck the actual request at execution time.

`POST /api/workbench/validate` accepts `{policy,author,reviewer}` with the same typed policy fields as `/draft`. It returns `valid`, `canPublish`, field-level `errors` and `warnings`, native checks and a concrete server-owned example, without changing data. A same-author reviewer prevents publication but does not invalidate a usable draft. Valid zero quota, explicit denial or an unsupported request zone are explained as restrictive behavior, not corrected behind the user's back. `/draft` shares policy field validation and independently validates through the native engine before writing.

Mutation commands:

- `/draft`: `{expectedRevision,author,reason,policy:{name,explicitDeny,zoneConstraints,requestsPerSecond,burstCapacity,monthlyQuota}}`. Native schema and semantic validation run before persistence.
- `/publish`: `{expectedRevision,draftId,reviewer,reason}`. The reviewer label must differ from the author. Publication does not update an accepted contract binding.
- `/retire`: `{expectedRevision,policyId,reviewer,reason}`. Content remains immutable, its administrative status changes, and execution using a retired binding is disabled.
- `/bind`: `{expectedRevision,policyId,reviewer,reason}`. A complete synthetic event from the fixed producer identity is passed to the native projector. Contract version advances independently; allocation configRevision changes only when limits change. Existing quota usage is retained.
- `/evaluate`: `{scenarioId,policyId?,proposedUnitName?}`. Only catalogued scenarios are accepted. The server reads synthetic attribute sources from its validated database and calls the actual native evaluator. The ordinary unit-name payload is trimmed, validated (2–120 characters) and retained with this decision; it is not a trusted identity or owner attribute. An optional policy is a candidate inspection and can never execute. The native `rawDecision` is retained unchanged. Lab inspection rows explain inputs; they are not native per-gate trace output.
- `/execute`: `{decisionId,scenarioId,failObligation?}`. `failObligation` can be `admit-allocation` or `resource-tenant`. The retained decision must match the current accepted binding and persisted trusted-source fingerprint; a source change invalidates a pending Permit immediately. Legacy decisions without a source fingerprint require a fresh evaluation. Historical decisions remain inspectable after coherent source changes. The native PEP validates the decision and its deadline, including a final check immediately before SQL commit. The write transaction also compares the previously read full-state fingerprint and mutation version, protecting coherent maintenance changes from stale writes. Local adapters check the actual stored unit tenant and perform token-bucket/monthly admission. Success atomically saves the retained unit name, increments saveCount and the admission counter, and records the operation and idempotency receipt. Execute cannot replace the payload retained by Evaluate. A replay with matching current definitions returns the existing receipt without a second consume. Failed attempts do not create an idempotency receipt, consume usage or modify a unit.
- `/reset`: `{expectedRevision}` clears only sandbox state. Administrative conflicts return HTTP 409 and the current revision.

The supported policy is the existing typed `fanasa.contract-policy/2` profile with `deny-overrides`, not an arbitrary XML XACML 3.0 interpreter. The API deliberately has no endpoint for caller-provided attribute graphs, unrestricted policies, real users, real counters, or production publication. Scenario variations and synthetic producer trust are visibly labelled. The unknown-obligation scenario passes a separate tampered copy into the native PEP; it never rewrites the retained native PDP output.

Run the meaningful native/HTTP and database-integrity tests from this directory after building the native engine, the workbench and the typed maintenance probe:

```powershell
dotnet build Fanasa.ContractPolicy.Workbench.csproj
dotnet build tests/DatabaseFreshness.Probe/DatabaseFreshness.Probe.csproj
node --test --test-concurrency=1 workbench.test.mjs runtime-integrity.test.mjs
```

These tests launch separate engine processes and state directories, without touching the UI's current state. The probe performs coherent SQLite maintenance using the exact persisted types, so checksum reconstruction cannot turn a source-revocation test into a corruption test. Coverage includes actual content changes, failure rollback, same/two-process concurrency, restart persistence, migration, column corruption, UTC validity, expiry while awaiting a SQL write lock, stale maintenance writes and immediate source-change invalidation.
