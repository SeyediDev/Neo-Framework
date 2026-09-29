# Typed work and acceptance criteria

Task, UserStory, Bug and Epic share the same scoped lifecycle, ownership,
subtasks, dependencies, timing, evidence and history. A type is planning
metadata, not a workflow stage or an approval. Existing records default to Task;
the migration must not guess types from titles or silently rewrite descriptions.

`POST items` accepts optional `type` (default `Task`) and `acceptanceCriteria`
(maximum 8000 characters). Original intake stays in `description`.
`PUT items/{id}/planning` accepts `expectedVersion`, `type` and nullable
`acceptanceCriteria`; null clears criteria. Criteria changes are append-only
history snapshots. Wrong versions, foreign owners, closed or archived work are
rejected; edits invalidate version-bound approvals. Item and child projections
return both fields. Old callers can omit them.

`GET items?type=UserStory` combines with existing filters before pagination and
metrics. Unknown or numeric type names are rejected. The MCP equivalents are
`neo_work_create`, `neo_work_planning` and the `type` filter of `neo_work_board`.
The Web exposes creation, versioned editing, cards, separate acceptance text and
type filtering in both task and role boards. No automatic claim/run is triggered.
