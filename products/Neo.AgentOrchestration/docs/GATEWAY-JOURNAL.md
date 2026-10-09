# Dual-agent durable execution journal

This is the implemented **journal/core, private HTTP ingress and opt-in queue
composition** for Hermes and OpenCode, not an installed runner or enabled
production gateway. API/Web do not call `AddGatewayJournal`; Worker calls it only
with explicit `AgentGateway:WorkerEnabled=true`. Startup never creates schema;
provisioning never dispatches a model. The default sandbox refuses execution.
OpenCode is the proposed coding lane, Hermes the automation/general lane.
Fanasa keeps the only board, workflow, task ownership, approvals and scheduler.

## Domain and storage boundaries

`Domain/ExternalExecution` owns the pure execution state machine, independent of
native adapters and persistence. `Application/ExternalExecution` defines scoped
journal, operator binding, sandbox evidence and result-delivery contracts.
Infrastructure implements native binding validation, SQL journal and authenticated
HTTP result transport. It does not write task ownership or identity tables.

`GatewayDbContext` reuses Neo EF infrastructure with its own explicit SQL catalog
(default name `FanasaAgentGateway`) and `gateway.Runs`/`gateway.Usage` plus scoped
`gateway.Activations` and Neo `gateway.OutboxMessages` tables.
It contains no WorkItems, SQL/SSO/model credential values or second task catalog.
Scope is an authenticated control-plane assertion, not a cloned tenant registry.
Usage has a composite run/org/workspace/project FK to the immutable reservation.
No cross-database FK or independent membership authority is invented.

The generated `InitialGatewayJournal` migration belongs to this context, not
`OrchestrationDbContext`. Apply it explicitly to a separately authorized catalog
after backup/database-user configuration; **it has not been applied to the VPS**.
No auto-create, auto-migrate, import or cleanup on application startup.
The explicit [CLI](CLI.md) provides `gateway-migrate` and read-only
`gateway-health` using only private `FANASA_AGENT_GATEWAY_SQL`. Health requires
exact known migration history, modeled table inventory and no pending model
changes; it does not audit all columns/indexes. Unknown history or extra/missing
tables fail closed without erasing or repairing them. Ingress and gateway Worker
share the same check before startup. Suffixed catalogs require explicit
`AgentGateway:DatabaseName`; host connections remain separately configured.

## Durable behavior

- Exact run ID/scope/body hash/binding/sandbox deduplication; changed duplicates
  conflict, including different bytes/whitespace. Dispatch returns 202 only after
  `ReserveAsync` commits the run AND initial Outbox activation in one transaction.
  A duplicate returns the existing reservation without another activation. Migrated
  pre-activation records return a reconciliation conflict on retry; they are not
  automatically backfilled, dispatched or falsely acknowledged with 202.
- A transaction-scoped SQL application lock protects journal mutations across
  processes; optimistic versions and two-minute processing leases fence workers.
  No SQL transaction spans native HTTP, sandbox verification or result delivery.
- Initial executor capacity is one globally across engines/tenants. Executor
  reservation is separate from worker lease: timeout does not free capacity.
- Prepare and submit intents are persisted before native writes. OpenCode
  preparation is saved before submission. A crash during either native write is
  held for reconciliation, never blind new-session/new-run creation. No expired
  Hermes idempotency key is replayed by this core.
- Native identities are persisted and scope/engine/fingerprint/message-checked.
  Changed operator configuration is held before acquiring capacity or networking.
- Approval is durably latched; stale terminal polling and stop acknowledgement
  cannot clear it or auto-approve. There is no approval reply/resolution endpoint.
- Native terminal state alone does not release capacity. The trusted sandbox
  collector must verify actual executor exit and supply validated evidence.
  A requested stop or native failure cannot produce a success result.
- Only then is the callback result frozen and capacity released. Exact stored
  result bytes are retried; authenticated API receipt ends delivery. No change to
  the existing success→Review / workflow-decision boundary.
- Completed-message usage is stored once by immutable report ID/hash; changed
  retries conflict atomically. Nullable counters remain unknown, never invented
  zero. These journal observations are **not** trusted run metering ingestion or
  billing; `ORCH-TOKEN-INGEST` remains a separate dependency.

## Operator binding (disabled by default)

Explicit composition: `AddGatewayJournal(configuration, connection)`.
`AgentGateway:Enabled` and `AgentGateway:Bindings:<key>:Enabled` must both be true.
Each key also needs the existing `NativeAgents:Connections:<key>` configuration.
Gateway-only binding fields are:

| Field | Constraint |
| --- | --- |
| `AgentProfileId` | Exact approved profile GUID; no task-text route selection |
| `SandboxId` | Operator sandbox identity, not a path/command |
| `RepositoryUrl` | HTTPS repository URL without credentials/query/fragment |
| `Revision` | Immutable 40–64 hex revision, not mutable branch/latest |
| `ImageDigest` | Exact `sha256:<64 lowercase hex>` sandbox image digest |
| `CallbackBaseUrl` | Operator-owned HTTPS API root/prefix |
| `DispatchSecretRef` | Private env reference for inbound harness dispatch |
| `CallbackSecretRef` | Private env reference for outbound API result |

Callback URL must exactly equal the route derived from configured base, key,
org/workspace/run. Scope/model/endpoint come from native operator configuration.
All three dispatch/callback/native secret references AND values must be distinct.
Changing revision/image/repository/native route/profile/reference changes the
fingerprint; rotating values behind unchanged references does not.

Test-only loopback HTTP requires `AgentGateway:AllowLoopbackHttp=true` in
Development/Testing; Production never permits it. HTTP result clients have a
single 30-second deadline across send and streamed receipt reads, no
redirects/outbound loggers, and an 8-KiB receipt bound. Shorter finite client and
caller budgets win; longer/infinite client timeouts cannot remove the ceiling.
Native and callback transports share the same deadline helper, without resetting
it for each chunk. A stalled/incomplete receipt is unacknowledged, with a sanitized
reconciliation error; it never changes the frozen result or fabricates a receipt.
Only the existing durable delivery path retries the exact stored result.
Stable error codes contain no raw upstream response or exception chain.

`DisabledGatewaySandbox` is the default and rejects execution before capacity or
native intent. A binding is metadata, **not** isolation proof. An actual trusted
collector must verify pinned non-root sandbox/runtime, exact run/revision and
executor exit; arbitrary text from an agent cannot supply evidence authority.

## Private dispatch and existing queue composition

`src/Fanasa.AgentGateway` is a separate disabled-by-default HTTP host. It checks
existing reviewed migrations before listening (no migration on startup). Configure
`ConnectionStrings:AgentGateway` and the operator binding above. Its default
listener is private loopback `http://127.0.0.1:18112`; production requires a trusted
HTTPS reverse proxy. Only the framework's known loopback proxies may supply
forwarded protocol; do not expose the listener or trust arbitrary forwarded headers.

```text
POST /bindings/{key}/runs
Authorization: Bearer <dispatch-secret>
Idempotency-Key: <run GUID, 32 hex digits without hyphens>
Content-Type: application/json; charset=utf-8
```

Body is the unchanged `neo-harness/v1` or `neo-harness/v2` snapshot; model/branch/
directory prose do not grant execution authority. Limits: 256 KiB including
chunked bodies, strict UTF-8, no compressed request body, 30-second acceptance
deadline. Duplicate idempotency headers, wrong run/key/scope/profile/callback and
unsafe configuration are refused. Unknown reservation outcome returns sanitized
503: retry exact bytes/key, never assume it did not commit. Responses expose only
run ID/phase or sanitized code, not prompts, credentials or native responses.
Ingress performs no native write, queue-client call or task lifecycle mutation.

The **existing Worker**, not ingress/API, opts in using
`AgentGateway:WorkerEnabled=true`, `AgentGateway:Enabled=true` and the same
independent gateway connection/bindings. Keep the existing product Harness opt-in,
Outbox queue and distinct product/jobs catalogs. Startup refuses pending gateway
migrations, unknown history and table/model mismatches before starting jobs. Worker composition replaces only the implementation
of the existing `IProcessOutboxRecurringJob` with a two-store coordinator; the same
scheduled invocation calls Neo's `ProcessOutboxRecurringJob` for each store. The
product `IOutboxStore` remains unchanged. There is no second cron/hosted scheduler.

Neo conditional dispatch/execution leases fence each activation. A gateway job
performs one bounded advance (90-second deadline, no network inside the final SQL
transaction), then acknowledges its delivery and stages a successor in one
transaction. Successors are due after 30 seconds; actual pickup also depends on
the existing minute dispatcher and Hangfire queue latency. Native writes still use
the journal's before-write intents. Lost acknowledgements/restarts do not create a
new native run. Approval/reconciliation/Delivered stop automatic chaining; holds
never free capacity. Waiting for the one global capacity slot stages a delayed
successor rather than pretending execution failed.

Initial maximum is **120 activations per run**, including capacity waits. Exhaustion
uses Neo's bounded delivery retries/dead-letter `Failed` state with a sanitized
reason, not a failed model result, completed task or released sandbox. Operator
inspection/recovery remains required. This is a safety bound, not multi-tenant
fairness, an execution deadline, a token/spending limit or progress percentage.

The generated `GatewayDurableActivation` migration is explicit and additive.
Only disposable LocalDB verification is migrated here, **not the VPS**. Never
automatically dispatch imported or pre-activation journal rows.

## Remaining before operational enablement

A separate operator [read-only offline executor probe](SANDBOX-INSPECTION.md)
checks actual Docker info/inspect against a scoped expectation. Its narrow policy
and synthetic/CLI validation are not a lifecycle/evidence collector. It is not
registered as `IGatewaySandbox`; the default still refuses execution. A diagnostic
never releases journal capacity, acknowledges a task or starts/stops an executor.

Full SSE collection, authorized human approval replies/reconciliation,
production sandbox lifecycle/evidence collection, usage ingestion, VPS installation
and live pilot also remain gates. Local schema CLI probes do not establish an
installed runtime's health. A successful journal test
must not mark `ORCH-DUAL-AGENT-GATEWAY`, installation or pilot fully accepted.
The [native adapter](NATIVE-AGENTS.md) now has separately opted-in bounded OpenCode
cursor traversal; exceeding its traversal budget still requires reconciliation,
not fabricated terminal status or incomplete consumption totals.
Its separately opted-in read-only pending-permission snapshot feeds the existing
durable approval latch, without replying/granting, clearing a hold after an empty
snapshot or replacing full SSE/reconciliation and installed-runtime acceptance.

## Verification

`GatewayRunTests` exercise the pure state machine. `GatewayBindingTests` exercise
operator configuration/authentication/fingerprints without an upstream call,
plus a stalled callback-body test through an in-process HTTP handler/stream.
The latter checks deadline cancellation, immutable payload, no adapter retry and
no fabricated acknowledgement; it is not a real API/network callback acceptance.
`SqlGatewayJournalTests` use fresh SQL contexts, real private ingress/configured
binding HTTP, queue-job restart/duplicate/rollback/hold/bounded-observation tests,
native/sandbox/delivery test doubles and a dedicated
`FanasaAgentGateway_Verification` catalog:

```powershell
$env:FANASA_GATEWAY_TEST_SQL = 'Server=(localdb)\MSSQLLocalDB;Database=FanasaAgentGateway_Verification;Integrated Security=true;TrustServerCertificate=true'
dotnet build products/Neo.AgentOrchestration/src/Neo.AgentOrchestration.Provisioning
dotnet test products/Neo.AgentOrchestration/tests/Neo.AgentOrchestration.Tests --filter 'GatewayRunTests|GatewayBindingTests|SqlGatewayJournalTests'
```

The SQL fixture explicitly migrates and clears ONLY its own disposable gateway
tables before each case; collection parallelization is disabled. CLI tests launch
the built provisioning executable with a restricted environment; build it using
the same Debug/Release configuration as the tests. They verify migration replay,
no product-connection fallback, read-only missing-catalog health and preserved
unknown history/schema mismatches. It refuses a
different catalog and skips when the connection is absent. Tests do not execute
a real agent/model/repository command or establish live callback→Review acceptance.

The queue integration case additionally sets `NEO_ORCHESTRATION_TEST_SQL` to
`NeoAgentOrchestration_Verification`; it uses the isolated product worker catalog
`NeoAgentOrchestration_WorkerVerification` and creates/retains only
`FanasaAgentGateway_JobsVerification` for actual SQL Hangfire. It proves the
existing worker/coordinator picks the three durable gateway activations with job
IDs, preserves the product store and adds no hosted scheduler. Sandbox, native
executor and callback delivery are still test stand-ins, not a live model pilot.
