# Independent SQL persistence

The destination is `NeoAgentOrchestration`; `NeoAgentOrchestration_<installation>`
is also accepted by the initial provisioner. Each installation has one database,
with organizations/workspaces/projects inside it. Product tables use schema `nao`.
The local installation now uses the Neo API operational board after user-approved
cutover; `WorkManagement` remains retained legacy data (see COEXISTENCE.md).
`TypedWorkItems` adds Type (default Task) and nullable AcceptanceCriteria. It also
formally adopts the import-created owner-history table in place, repairs its
known timestamp/rowversion drift and adds scoped constraints/indexes. Existing
history rows are retained. This migration is forward-only: downgrade cannot
delete imported history or acceptance data; use a reviewed forward repair or
an explicitly approved backup restore.
The forward-only ProjectTemplates migration adds immutable template revisions
and scoped instantiation receipts. Unique workspace/request IDs make replay
durable; generated projects remain linked to their original template version.
It adds tables only and does not update existing tasks or start workflows.
The additive delivery migration reuses Neo Outbox and adds scoped delivery/inbox
records; see [DELIVERY.md](DELIVERY.md) for atomicity, retries and worker boundaries.
There is no automatic import, dispatch, redirect, dual write or legacy cleanup.

`HttpHarnessDispatch` adds only `AllowExternalExecution` (default false),
`HarnessBinding` and `HarnessPayload` to `nao.AgentRuns`. Existing simulation
requests retain their idempotency fingerprints. The immutable payload contains
historical task context, not transport secrets, and is absent from public run
views. Protect database/backup access; automatic context retention is later work.

## Explicit provisioning

Configure `NEO_ORCHESTRATION_SQL` privately in the process environment or through
the deployment secret store. Its catalog must exactly match the explicit command
argument. Do not put a real connection string in source, CLI arguments or logs.
Then run from the Neo checkout:

```powershell
dotnet run --project products/Neo.AgentOrchestration/src/Neo.AgentOrchestration.Provisioning -- migrate NeoAgentOrchestration
```

The migration command creates the destination if absent and permitted, and
applies versioned EF migrations. Repeating it preserves data. It refuses
legacy/system catalogs, a mismatched catalog and unrelated tables. Startup of
the API/Web never runs this command. Exit 0 confirms current schema; exit 2 is
invalid/missing configuration; exit 1 is migration failure. The separate explicit
[CLI seed command](CLI.md) bootstraps organization/workspace/project/roles after
migration. It does not create users/grants, register credentials or start agents.

## Transaction contract

`OrchestrationDbContext` derives from Neo's `EfDbContext`; the work store uses
Neo EF query/command repositories. Each work operation gets a fresh context and
a read-committed transaction. A transaction-owned SQL application lock serializes
all workspace business operations, including claims and dependency graph changes
with empty candidate sets. This avoids serializable key-range locks overlapping
between independent workspaces. Every writer must use this scope-lock protocol;
raw DbContext access is reserved for provisioning and controlled internal work.
Errors/cancellation roll back and discard the context; commands are not silently
replayed. The role active-work index is non-unique because each role has a
configured capacity from 1 to 16; the workspace transaction lock plus an atomic
count enforce that capacity. A separate unique index still enforces one open
timer per item. Row versions and item/workflow GUID versions reject stale writes.
Composite foreign keys reject foreign-project parents/dependencies and
foreign-workspace project/role/agent/workflow relationships. All deletes restrict.
Membership authorization still belongs to the API; a scope object is not a token.

SQL registration is opt-in (`AddOrchestrationSql`) and does not migrate a catalog.
The API selects this store only with an explicit independent connection and
validates its catalog. Scoped task/configuration endpoints and their issuer
grant contract are documented in [API.md](API.md). No organization/workspace
is automatically seeded by API startup.

## Verification

Live tests require a separate `NeoAgentOrchestration_Verification` database via
`NEO_ORCHESTRATION_TEST_SQL`; the provisioner creates it if permitted. Fixtures
create uniquely keyed verification organizations and leave them for inspection.
They never delete a database or use the legacy catalog. Without this environment
setting SQL tests are explicitly skipped, not reported as SQL success.

```powershell
dotnet test products/Neo.AgentOrchestration/tests/Neo.AgentOrchestration.Tests
```

Tests cover migration replay, model drift, full history/evidence/time roundtrip,
parallel role claims, rollback, stale contexts and scoped foreign keys. These do
not establish browser parity, an external harness, import reconciliation or
migration acceptance. Versioned migration changes should be generated/reviewed
with the installed EF tool against this infrastructure project, never Hyper.
