# Dual-agent durable execution journal

This is the implemented **internal journal/core** for Hermes and OpenCode, not
an installed runner or enabled HTTP gateway. The API, Web and Worker do not call
`AddGatewayJournal`; startup and schema provisioning never dispatch a model.
OpenCode is the proposed coding lane, Hermes the automation/general lane.
Fanasa keeps the only board, workflow, task ownership, approvals and scheduler.

## Domain and storage boundaries

`Domain/ExternalExecution` owns the pure execution state machine, independent of
native adapters and persistence. `Application/ExternalExecution` defines scoped
journal, operator binding, sandbox evidence and result-delivery contracts.
Infrastructure implements native binding validation, SQL journal and authenticated
HTTP result transport. It does not write task ownership or identity tables.

`GatewayDbContext` reuses Neo EF infrastructure with its own explicit SQL catalog
(default name `FanasaAgentGateway`) and `gateway.Runs`/`gateway.Usage` schema.
It contains no WorkItems, SQL/SSO/model credential values or second task catalog.
Scope is an authenticated control-plane assertion, not a cloned tenant registry.
Usage has a composite run/org/workspace/project FK to the immutable reservation.
No cross-database FK or independent membership authority is invented.

The generated `InitialGatewayJournal` migration belongs to this context, not
`OrchestrationDbContext`. Apply it explicitly to a separately authorized catalog
after backup/database-user configuration; **it has not been applied to the VPS**.
No auto-create, auto-migrate, import or cleanup on application startup.

## Durable behavior

- Exact run ID/scope/body hash/binding/sandbox deduplication; changed duplicates
  conflict, including different bytes/whitespace. A future dispatch host returns
  202 only after `ReserveAsync` commits AND durable worker activation is staged.
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
30-second timeout, no redirects/outbound loggers, and an 8-KiB receipt bound.
Stable error codes contain no raw upstream response or exception chain.

`DisabledGatewaySandbox` is the default and rejects execution before capacity or
native intent. A binding is metadata, **not** isolation proof. An actual trusted
collector must verify pinned non-root sandbox/runtime, exact run/revision and
executor exit; arbitrary text from an agent cannot supply evidence authority.

## Remaining before operational enablement

The authenticated dispatch HTTP host with bounded request parsing, atomic durable
worker activation and the connection to the existing Outbox/Hangfire executor are
not yet implemented. Neither a second recurring scheduler nor API dispatch loop
is introduced. SSE/paging, authorized human approval replies/reconciliation,
production sandbox lifecycle/evidence collection, usage ingestion, installed
health/schema probes and live pilot also remain gates. A successful journal test
must not mark `ORCH-DUAL-AGENT-GATEWAY`, installation or pilot fully accepted.

## Verification

`GatewayRunTests` exercise the pure state machine. `GatewayBindingTests` exercise
operator configuration/authentication/fingerprints without an upstream call.
`SqlGatewayJournalTests` use fresh SQL contexts, native/sandbox/delivery test
doubles and a dedicated `FanasaAgentGateway_Verification` catalog:

```powershell
$env:FANASA_GATEWAY_TEST_SQL = 'Server=(localdb)\MSSQLLocalDB;Database=FanasaAgentGateway_Verification;Integrated Security=true;TrustServerCertificate=true'
dotnet test products/Neo.AgentOrchestration/tests/Neo.AgentOrchestration.Tests --filter 'GatewayRunTests|GatewayBindingTests|SqlGatewayJournalTests'
```

The SQL fixture explicitly migrates and clears ONLY its own disposable gateway
tables before each case; collection parallelization is disabled. It refuses a
different catalog and skips when the connection is absent. Tests do not execute
a real agent/model/repository command or establish live callback→Review acceptance.
