# Permanent project cascade deletion

This is an explicit administrative operation, never startup/import cleanup.
It requires the same exact workspace's `read`, `configure` and `write` grants;
preview needs `read` + `configure`. Actor identity comes from the issuer and the
mutation requires `X-Orchestration-Chat`. No SQL credentials reach Web or MCP.

1. From workspace management, choose **حذف پروژه و کارهای وابسته…**.
2. Review counts of every work type, including archives and nested subtasks.
   Subtask counts are subsets, not an additional work count.
3. Type the exact immutable project key. If manual work is in progress, explicitly
   confirm its deletion too; this grants no ownership/takeover to another agent.
4. Submit once. API rechecks the scoped graph snapshot under the workspace lock
   and commits all dependent deletions in one transaction, or none. The SQL
   deletion batch checks every table's affected-row count against that snapshot;
   any mismatch or foreign-key failure rolls back all earlier deletions.

`GET projects/{id}/deletion-preview` returns `ProjectDeletionPreview` with a
SHA-256 `snapshot`, record/type counts, active assignments/runs and pending
deliveries. SQL hashes complete rows including content/versions without exposing
those rows to the client. A count is not a concurrency version. The workspace
application lock fences business changes; Outbox row/range locks also prevent
delivery leases changing between validation and commit without range-locking
all empty child tables in unrelated workspaces.

`DELETE projects/{id}` accepts:

```json
{
  "expectedSnapshot": "64 hexadecimal characters from the current preview",
  "confirmProjectKey": "EXACT-KEY",
  "includeActiveManualAssignments": false
}
```

The result identifies the deleted project, per-table deleted counts and the
completion time. Operational logging records identifiers/actor/counts, not a
copy of deleted content. Wrong key/input returns 400; changed snapshot, unresolved
run/delivery or missing manual confirmation returns 409; absent/foreign project
returns 404 within an authorized workspace. Missing grants return 403 before
storage. A repeated successful DELETE returns 404, never recreates the project.
After an uncertain response, read the catalog before retrying. Preview on a new
state requires a fresh deliberate confirmation; neither Web nor API replays it.

Queued/awaiting runs and uncertain external `NeedsInput` runs block deletion.
Pending/nonterminal messages or any retained delivery lease also block it, even
with manual-work confirmation. Stop/reconcile execution through the existing
run lifecycle; this endpoint does not force-release native execution.

The cascade covers all tasks/stories/bugs/epics, subtasks, dependencies, owner
history, logs, evidence, time and manual token reports; project workflows,
transitions/approvals; stopped/terminal runs, run usage, Inbox/Delivery/Outbox
records; repository binding and project-template instantiation receipts. It
preserves workspace/organization, other projects, roles/agent profiles and shared
template versions. Scoped RESTRICT foreign keys remain enabled: an unknown/custom
dependent makes the transaction fail and roll back rather than orphaning data.

There is no trash/restore or automatic backup. This does not remove repository
files, remote repositories/Issues, deployed software, native gateway resources or
product/accounting/retained legacy databases. Deleting intake/template receipts
also ends their old idempotency/provenance history; old create/instantiate
requests must not be automatically replayed as a restoration mechanism.
No dedicated destructive MCP tool was added; existing coordination tools and
their ownership/version rules remain unchanged. Operators use authenticated Web
or these scoped API routes, not ad-hoc SQL in normal operation.

Focused tests are `ProjectDeletionTests`: real isolated SQL transactions, all
known graph tables, same-workspace preservation, cross-scope/stale/key rejection,
active-work/run gates, unknown-FK partial-deletion rollback, authenticated HTTP,
antiforgery and ordinary/SPA Web submissions. Fake-run fixtures are not live
external-agent acceptance.
