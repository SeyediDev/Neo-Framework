# Time and token metering

Budgets are nullable positive estimates, not consumption, completion or billing
limits. Web time input accepts minutes/hours/seconds and rounds up to stored
integer seconds. Existing owner/version rules govern claim and active intervals.
A monotonic local clock updates active time without a request each second;
new API responses remain authoritative. Tokens are never extrapolated.

Tasks/subtasks have independent meters; board totals cover all filtered items
before pagination, once each, without rolling children into the parent. Coverage
counts show which items have estimates/reports. These totals do not establish
complete provider billing or untracked human time.

## Scoped API v1

- Creation accepts optional positive `estimatedTokens`.
- `PUT items/{id}/token-estimate`: `{ expectedVersion, tokens }`; null clears.
- `POST items/{id}/token-usage`: `{ expectedVersion, requestId, provider,
  reference, model?, inputTokens?, outputTokens?, cachedInputTokens?,
  reasoningTokens? }`.

Both require read/write grants. Assigned estimates are owner-only. New manual
reports require owning subject/chat, current version, editable item and enabled
project. Stable nonempty requestId identifies delivery. Identical payload/source
retries return the existing result even at their stale original version; changed
content/source returns 409. New IDs mean new reports, not retries. Invalid ID,
negative counts, no known counter, cache above input or reasoning above output
return 400. Provider/reference/model limits: 80/500/200 characters.

Manual reports are for work without a managed run. With a run, use the existing
`POST runs/{id}/token-usage` route (read/execute). Never submit a provider report
through both paths. Conflicting run idempotency retries now return 409 instead
of acknowledging a changed payload.
Each report must cover a non-overlapping provider request/delta, not another
cumulative snapshot of already reported consumption. Stable IDs deduplicate
delivery, not independently submitted cumulative totals.

## Counters

- Null is unknown, including no reports. Zero is known zero.
- Summaries include reported/imported/manual reports, not `estimated`.
- Total is input + output. Cache/reasoning are included subsets, not extra use.
- `knownTotalTokens` is a known subtotal; `totalTokens` is null unless every
  included report has input and output. Partial subtotals are labeled incomplete.
- `reportCount`/`completeReportCount` describe report coverage, not whether all
  calls were observed. Board `tokenReportedItemCount` describes item coverage.
- Reports are append-only observations, not verified invoices. Corrections, costs,
  reservations, hard limits and tenant provider credentials remain separate work.

## Authenticated harness result ingestion (operator opt-in)

Set `Harness:Connections:<key>:UsageReportsOptIn=true` on the product API/worker
and `AgentGateway:Bindings:<key>:UsageReportsOptIn=true` on the private gateway.
Both default false. Each changes its destination fingerprint; reconcile existing
reservations rather than changing a live binding or replaying a dispatch.
The API accepts usage only against the run's exact frozen connection fingerprint.
This setting neither enables execution nor grants model/repository access.

`HarnessResult` optionally carries `usage`, at most 100 entries, each with stable
`reportId`, native `provider`, `model` and nullable `inputTokens`, `outputTokens`,
`cachedInputTokens`, `reasoningTokens`. The gateway journals immutable completed
message/turn reports (at most 100 per run), orders by report ID and freezes all
of them with the result after verified executor exit. Collectors cannot inject
their own totals. Opted-out results omit this member, preserving legacy result
serialization; old APIs therefore remain usable with opted-out bindings.

The callback authenticates the connection and derives workspace/run identity
from its route. Result, evidence, time, usage rows, Inbox receipt and successful
follow-up commit in one workspace transaction. Rejected evidence or usage rolls
back every effect. Exact callback retries acknowledge the same receipt; changed
reports/results conflict. Failures and stopped executions retain any reported
usage too; missing counters stay null. There is no interim consumption delivery
for a still-running/uncertain/held execution in this version.

Persisted usage uses `reported-harness` and the authenticated run's `http.<key>`
provider. The native model route remains provenance in the immutable Inbox body;
it cannot impersonate a different harness provider. Deterministic scoped report
IDs/keys survive callback retries. Run reports flow into existing item/board
meters without a second manual task report. This is authenticated source
reporting, not independent verification of provider billing.

The run page distinguishes this run's reports from the whole task's estimates
and budget meter, labels automatic/manual/estimated sources, and explicitly
shows unknown consumption when there are no reports. No new MCP write tool,
public dispatch endpoint, source credential or SQL schema is added. Deployment
and a genuine managed VPS run remain separate acceptance from local SQL/HTTP
fixtures and the prior standalone synthetic Codex pilot.

Item views and compact MCP receipts/briefs include `estimatedTokens`, `tokenMeter`
and `measuredAtUtc`. Details include `tokenUsage` provenance; briefs explicitly
omit rows. Run reports are fetched through one scoped bulk query, not per task.

## Agent and UI behavior

`neo_work_token_estimate(itemId, request)` and
`neo_work_token_usage(itemId, request)` map directly to these contracts.
`neo_work_estimate` remains seconds-based. Read the newest receipt version.
Only record counters actually exposed by the provider/harness; conversation
availability is not a supported billing counter. Never relabel an estimate as
consumption, duplicate a run report or poll every second. Usage delivery IDs
survive SPA draft recovery; versions and CSRF tokens are refreshed, not restored.

## Schema / verification

Explicit additive EF migration adds nullable `WorkItems.EstimatedTokens` and
`nao.WorkTokenUsage`. Existing rows/run reports/ownership/time remain unchanged.
No startup migration or execution dispatch. Back up first; deploy API/Web
together. Tests cover nullable/partial/zero, subsets, ownership/scope/version,
idempotency and full-filter totals. The MCP smoke test starts actual stdio and
Kestrel processes with test JWT/memory storage, not proof of SQL persistence.
SQL roundtrip tests require isolated `NEO_ORCHESTRATION_TEST_SQL`, never the
operational database.
