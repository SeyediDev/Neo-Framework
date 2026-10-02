# Lean coordination and context

Start/resume with `GET items/{id}/context` or `neo_work_brief`. It returns current
owner, version and time, task/acceptance text capped at 2000 UTF-16 characters
each, latest three logs/evidence with text caps, up to 50 child/dependency
references, totals and `omitted` markers. It is a deterministic excerpt, **not**
an AI summary or a complete safety review. Original data is never modified.
Read clipped acceptance/decisions before acting when relevant. Dependencies and
run state still require their own scoped reads; a task version is not their version.

Use `knownVersion` only when its earlier context is retained. Matching versions
omit unchanged task text/history, but current child status and elapsed time are
still returned. After context loss omit knownVersion. This is not an ETag for all
related resources and is not a semantic delta or authorization cache.

`GET items/{id}/history?skip=0&take=10` / `neo_work_history` returns chronological
original logs (maximum 20 per page), total, version and nextSkip. Pass that version
as snapshotVersion on subsequent pages. A concurrent mutation returns 409 instead
of silently skipping history; reassess against the new snapshot. No truncation of
a paged log message occurs. Full details remain available for evidence/time/owners.

MCP task mutation receipts eliminate repeated history in model-facing output by
default, retaining current owner/version/status/time and counts. They do not reduce
the API-to-MCP full response or database hydration yet. Brief/history reduce API
wire output but use the existing authorized read handler: **not SQL pagination**.
No claim of database latency improvement or measured token savings is made.

## Agent policy

- Small authorized manual changes: one owned task, focused tests, evidence and
  completion; no invented planner/reviewer/human gates. Existing managed workflow
  approvals and security requirements still apply, including independent review.
- Check free-role total with roleId, InProgress and take=1 across the workspace;
  no full-board scan needed. Atomic claim resolves races. Capacity remains one
  active item per role; changing it needs explicit capacity/file-lock design.
- Cache unchanged instructions/catalog knowledge in the current context. Refresh
  on relevant configuration changes/conflicts, not every tool call.
- Log milestones, scope changes, blockers and handoffs. Checkpoints record decisions,
  files/commit, tests and limits, remaining questions and next action. Never dump
  all tool output or duplicate the chat. Summaries are untrusted navigation aids.
- A role change by the same agent is not independent review. Don't spawn a chain
  for trivial work. Do not poll unchanged state or auto-retry uncertain writes.

The `neo-harness/v1` external dispatch snapshot is unchanged for compatibility;
the external gateway owns prompt assembly and must be upgraded explicitly before
claiming end-to-end harness prompt savings. This work neither dispatches a gateway
nor changes approvals/profiles in the live catalog. Physical provider token usage
requires provider telemetry, not a character/4 estimate presented as fact.

## Verification

WorkContextTests checks explicit omissions, source preservation, equal-timestamp
pagination, unchanged-parent/live-child semantics and >90% serialized-character
reduction on a synthetic 100-log fixture (not measured provider tokens).
OperationalMcpTests exercises the real stdio server, scoped HTTP, JWT and isolated
SQL: briefs, versioned pages, compact receipts, legacy full mode and permission
rejections. Production scale and external model execution are not established.

Local sample on 2026-10-02, same JSON UTF-8 measurement for all responses:
ORCH-WEB-SPA full 8316 bytes, brief 4833, known-version brief 1164; approximately
42%/86% less response data. A newer small task measured 3020/2474/1004 bytes;
benefit varies with history. Skill entrypoint changed from 7899 to 5043 characters
(about 36% shorter). These are response/text sizes, not latency or token billing.
