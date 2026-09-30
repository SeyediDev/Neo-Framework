# Operational tool contracts

These names belong to Neo Agent Orchestration's stdio HTTP client. Discover the
server's actual schemas; required IDs and versions must come from readback, not
these examples. Tool responses contain JSON text, while errors have isError.
Clients may add a server prefix to the names.

## Read before writing

| Tool | Arguments / useful output |
| --- | --- |
| `neo_work_context` | No arguments; configured API, organizationId, workspaceId, chatId. Not a connectivity/grant check. |
| `neo_work_catalog` | No arguments; projects, roles, agent profiles and workflows, including enabled flags. |
| `neo_work_board` | Optional projectId, domain, roleId, status, type, includeArchived=false, skip=0, take=50 (max 200). AND filters; page all results needed for role checks. |
| `neo_work_get` | itemId; item/owner/version, direct children, dependency IDs, logs, evidence and time entries. Read dependency items separately for status. |
| `neo_run_list` | itemId; run chain and linked deliveries. |
| `neo_run_get` | runId; result/decision/delivery diagnostics, not raw dispatch context. |

## Manual mutations

Except time/archive, tools below take an itemId and a nested `request` object.
Create takes only `request`. The API provides identity from the issuer subject
and server-configured chat; never add agentId/owner/chat properties to a request.

| Tool | Fields inside request |
| --- | --- |
| `neo_work_create` | projectId, key, title, domain; optional description, priority="Normal", parentWorkItemId, estimatedSeconds, type="Task", acceptanceCriteria (max 8000 characters), requestId (stable nonempty GUID for identical retry) |
| `neo_work_planning` | expectedVersion, type, acceptanceCriteria (null clears); preserves description and records criteria history; owner-only when assigned; rejects closed/archived items |
| `neo_work_claim` | expectedVersion, roleId; optional branch |
| `neo_work_log` | expectedVersion, message |
| `neo_work_status` | expectedVersion, status; optional note |
| `neo_work_evidence` | expectedVersion, kind, reference, outcome; optional details, commitSha |
| `neo_work_estimate` | expectedVersion, seconds (positive or null) |
| `neo_work_dependency` | expectedVersion, dependsOnWorkItemId |

`neo_work_time` takes top-level itemId, expectedVersion and start (true/false).
`neo_work_archive` takes top-level itemId, expectedVersion and archived
(true to archive eligible Blocked/Done work, false to restore).

Named statuses: Backlog, Ready, InProgress, Blocked, Review, Done, Cancelled.
Item types: Task, UserStory, Bug, Epic. Type does not replace status or bypass gates.
Existing unclassified records default to Task. Classify only with evidence from
the actual request; do not infer completion or rewrite the original description.
Priorities: Low, Normal, High, Critical. Evidence kinds: Commit, Test, Artifact.
Evidence outcomes: NotApplicable, Passed, Failed, Skipped. Use names, not numeric
enum values. Commit evidence reference must be the actual valid SHA; it is not
proof that arbitrary tests ran. Each mutation returns the new item version.

Example log call shape (replace placeholders with readback, not literal text):

```json
{
  "itemId": "<current-task-guid>",
  "request": {
    "expectedVersion": "<current-version-guid>",
    "message": "Outcome, actual test command/result, limits and next action."
  }
}
```

Task mutations need API read/write grants. With requestId, creation is atomic and
retry-safe within the workspace: identical body/agent/chat returns the existing
item, including its current status, without new logs or resetting ownership/time.
Changed content/source under the same ID returns 409. Without requestId, query
project/key after an uncertain result before retrying. Fields containing credentials
are not valid task context. Keep the original request in description and later
messages in logs; do not overwrite the original intake.

## Managed runs

All three mutation tools below require read/execute, not only write:

| Tool | Top-level ID and request fields |
| --- | --- |
| `neo_run_start` | itemId; requestId, expectedWorkItemVersion, workflowId, expectedWorkflowVersion, roleId; optional agentProfileId, branch, simulationOutcome="Succeeded", allowExternalExecution=false |
| `neo_run_handoff` | runId; requestId, expectedWorkItemVersion, expectedWorkflowVersion |
| `neo_run_return` | runId; expectedWorkItemVersion |

All listed fields other than itemId/runId go in the nested `request`.
A stable requestId identifies an identical start/evaluation; changed payload
under that key conflicts. External execution needs explicit authorization and
allowExternalExecution=true. Starting a run gives ownership to a managed run,
not to the calling chat. Handoff is gate reevaluation and can stay Waiting;
return is restricted to the initiating subject and a stopped assignment.

There are no configuration, approval, callback, arbitrary HTTP, SQL or shell
tools in this MCP. Use authorized Web/API operations for configuration/independent
approval; do not simulate those missing operations with a status change.

## Errors and limits

400: invalid schema/value. 401: renew the issuer token privately and restart.
403: denied scope/grant. 404: absent scoped resource. 409: reload and investigate
version, owner, dependency or state conflict. 503: storage/provider unavailable.
Transport failure means unconfirmed, not failed or complete; read before retry.

No command automatically retries. The client rejects remote HTTP and redirects;
loopback HTTP needs an explicit operator setting. Requests are at most 256 KiB,
responses 4 MiB. Reduce paging if needed, but do not label a partial history
complete. Installation, token acquisition/refresh and live model execution are
separate concerns; the skill does not perform them implicitly.
