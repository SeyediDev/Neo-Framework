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
- Reports are append-only observations, not verified invoices. Trusted ingestion,
  corrections, costs, reservations, hard limits and tenant provider credentials
  remain separate work.

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
