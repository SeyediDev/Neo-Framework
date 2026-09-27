# Durable work delivery

The product uses **Neo's `OutboxMessage` and `EfOutboxStore<TContext>`**, not a
second retry/lease state machine. Schema migration `DurableWorkDelivery` adds
`nao.OutboxMessages`, `nao.Deliveries` and `nao.InboxReceipts`. It neither imports
legacy messages nor dispatches them. Provisioning still targets only the separate
product catalog; [DATABASE.md](DATABASE.md) describes the explicit command.

## Atomic boundaries

`IDurableWorkStore.EnqueueAsync` stages same-session business changes and a Neo
Requested message in one SQL transaction. `ApplyInboxAsync` stages the callback's
business changes and an applied receipt, optionally with a follow-up Outbox
message, in that same transaction. Errors or cancellation before commit leave
none of those writes. These callbacks may not do HTTP, publish to a broker,
open another work-store transaction or commit independently.

The receipt key is `(WorkspaceId, Source, MessageId)`. Source is a normalized
producer/operation namespace that the future HTTP adapter must derive from its
authenticated producer and operation, not accept as authority from a body field.
Opaque message IDs are case-sensitive after trimming. A SHA-256 fingerprint
includes the target, operation/follow-up kind, inbound/outbound mode and payload.
The payload is limited to 256 KiB UTF-8 and is not stored in the ledger. Equivalent
JSON with different byte representations is a conflict: adapters must use a
stable representation. Never use source/message identifiers to carry secrets.

A duplicate returns the original receipt ID, resulting work version and optional
Outbox ID; it does not re-run the callback. Reusing its key with a changed target,
payload or operation raises `DeliveryConflictException`. Failed transactions have
no applied receipt, so a retry can perform their effect. Workspace transaction
locks serialize competing receivers across processes; SQL unique indexes and
composite scoped foreign keys provide a second constraint. These APIs currently
target existing work items, not idempotent creation of a new item.

## Worker contract

Outbox content contains only a `WorkDeliverySignal` operation ID. Its linked
Delivery row supplies scope, task, requested task version and kind. The product
executor checks that envelope, acquires the same workspace lock and supplies a
shared-context `IWorkItemSession` to its handler. It uses Neo's execution claim and
`ExecuteClaimedAsync` to commit database effects and Processed status together.
A lost/replaced lease cannot commit effects. A repeated completed job is a no-op.
Handlers must revalidate current domain state and the requested work version;
the delivery kernel does not decide whether a stale workflow request is valid.

Neo's durable dispatch path is Requested -> Dispatching -> Queued -> Processing
-> Processed. It also handles a worker arriving before the dispatcher records
Queued. Lease duration is five minutes, execution cancellation four minutes,
retry delay thirty seconds, with at most three handler attempts or interrupted
dispatch attempts. Expired leases are recoverable; terminal exhaustion remains
in Failed, which is this SQL path's dead-letter state, not a RabbitMQ error queue.
Failed records are not deleted or automatically reset. Reconcile the cause and
any external outcome before adding a controlled replay operation.

Handler exception details are replaced with a fixed diagnostic before Neo stores
its ProcessError, so provider responses/tokens are not persisted there. Inspection
should use operation IDs, state and counters, not raw external payloads.

The installation worker is trusted and processes installation-wide messages;
its methods are **not** user-facing unscoped endpoints. The opt-in simulation
[worker](RUNS.md) connects Neo's dispatcher to `RunOutboxJob` and
`SqlWorkDeliveryExecutor`, using its supplied session. Do not route this contract
to a generic MediatR handler that creates a second DbContext/transaction.
No worker, recurring schedule, HTTP callback or harness adapter starts from
`AddOrchestrationSql`, API startup or database migration. The separately enabled
worker registers its schedule; external HTTP callbacks/harness remain pending.

## Delivery guarantees and verification

Only same-database effects are atomic. Queue delivery can repeat after a crash;
external HTTP effects are not rolled back. Reuse the stable operation ID with
an idempotent provider and reconcile uncertain responses; this is not a claim
of exactly-once external execution.

SQL tests use the separate verification catalog and fresh contexts. They exercise
concurrent duplicates, key conflicts, rollback/cancellation, outgoing atomicity,
competing dispatchers, fast-worker races, retry exhaustion and stale lease owners.
Lease expiry is simulated by changing timestamps on test rows; no real process
is killed and no external queue/harness execution is claimed. Existing scopes,
task time/history and migration replay remain in the regression suite.
Companion MCP/skills contracts are unchanged by this product-specific stage.
