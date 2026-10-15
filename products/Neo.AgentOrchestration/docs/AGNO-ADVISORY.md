# Private Agno advisory observation

Agno is an optional **advisory** runtime, not another board, scheduler or repository
executor. The native work API, ownership, approvals and Outbox/Hangfire remain the
control plane. `task-planner` and `task-preparation` currently draft suggestions;
no code/shell/deploy tools, task writes or independent approval are supplied.

The existing project-scoped runtime GET additionally accepts an operator-owned
`AgentRuntimeStatus:Bindings:<key>:AdvisoryStatusFile`. Without that setting,
the unchanged three-engine `fanasa-agent-runtime/v1` report behaves as before.
An invalid native report does not make a valid advisory unhealthy, or vice versa.
The API reads a file only; it never calls Agno, Docker or a shell. Exactly one
org/workspace/project binding, existing project membership and read/configure
grants are still mandatory. No new dispatch or MCP tool is introduced.

The separate observation uses `fanasa-agno-advisory/v1`, strict unique JSON keys,
maximum 8 KiB, no unknown fields or links through parent directories, and a
three-minute freshness limit (maximum 15 seconds future skew). Fields are:

```json
{
  "schema": "fanasa-agno-advisory/v1",
  "observedAtUtc": "2026-10-14T17:00:00Z",
  "mode": "advisory-only",
  "version": "3.1.3",
  "installed": true,
  "transportHealthy": true,
  "reason": "advisory-healthy"
}
```

`reason` is `advisory-healthy`, `advisory-unreachable` or `not-installed`,
consistent with the booleans. The public DTO always has `ExecutionReady=false`.
No endpoint, host path, prompt, token, model draft or raw native response appears
in this observation. The collector needs operator access; the API needs only
private read access to the resulting report, not Agno credentials or Docker.

`deploy/agents/agno-status.py` performs read-only, bounded installation checks and
authenticated private health reads. Invoke it explicitly or after the **existing**
native health collection with a narrowly scoped systemd drop-in. It is not a new
task dispatcher. Preserve the original unit, rootless runtime and public ingress.
Check actual file group/read access and deadline on each installation. Keep model
invocation, usage/evidence collection and task acceptance out of health sampling.

## Operator pilot and limitations

The VPS pilot installed Agno 3.1.3 with PostgreSQL 17 and the existing local
`phi3:3.8b` model. The private operator client can call `plan` or `prepare` with a
short reviewed brief; do not paste raw chats or secrets. Real planner/workflow
execution and provider counters were measured separately from installation health.
The CPU model was slow and hit the output limit; drafts are not quality-accepted.
These results do not establish panel dispatch, human Agno SSO, tenant isolation
for model sessions, coding readiness, production token billing or independent review.

## Private task-linked requests (separate opt-in)

The source now includes `AdvisoryRequests`, scoped MediatR/API handlers and the
task's **مشاورهٔ خصوصی** page. This is not a coding AgentRun. API/Web do not hold
the Agno token, read Docker state or invoke a model. The Worker uses the existing
Neo Outbox lease/retry engine and Hangfire; no second queue or usage ledger is
introduced. Apply the reviewed migration before enabling this feature. Source
implementation alone does not establish a successful live panel pilot.

Routes under the existing organization/workspace prefix:

- GET `items/{id}/advisories`: `{submissionEnabled, requests}` with the latest 25
  requests of the authenticated subject. The flag is configuration availability,
  not a claim of worker/provider health or a replacement for execute authorization.
- GET `advisories/{id}`: requester-only result; foreign subject gets 404.
- POST `items/{id}/advisories`: read+execute grants and chat correlation, body
  `{requestId, expectedWorkItemVersion, brief, consent, budgetSeconds}`.
- POST `advisories/{id}/cancel`: read+execute and `{expectedVersion}`.

The brief is 1–2000 characters. Consent is mandatory. The default deadline is
180 seconds from intake; valid budgets are 30–180 seconds, including queue time.
No task description/history/chat is appended. Same request ID and exact body/chat
return the existing receipt even after the original task version changed;
changed input conflicts. There is at most one Queued/Submitting/Held request per
requester/task. Task scope/version/archive/cancellation and enabled project are
rechecked before submission. No task owner, state, timer, log, evidence, approval
or consumption report is written by this lane.

Worker/API operator settings:

```text
AgnoAdvisory:Enabled=true
AgnoAdvisory:Bindings:pilot:OrganizationId=<authorized organization>
AgnoAdvisory:Bindings:pilot:WorkspaceId=<authorized workspace>
AgnoAdvisory:Bindings:pilot:ProjectId=<authorized project>
```

Worker-only settings:

```text
AgnoAdvisory:BaseUrl=http://127.0.0.1:17840/
AgnoAdvisory:TokenFile=<systemd LoadCredential file>
Orchestration:SimulationEnabled=false
Harness:Enabled=false
AgentGateway:WorkerEnabled=false
```

Retain separate product/jobs catalogs, existing queue and recurring registration.
The enabled-lane dispatcher filters before lease acquisition/expired-final-lease
handling. Advisory-only startup does not touch imported coding deliveries; queued
jobs also recheck the lane before execution. Do not start a generic framework
dispatcher against this catalog in parallel. Health sampling does not dispatch.

Native POST is fixed to `agents/task-planner/runs`, loopback-only, redirects off,
`stream=false`, `background=false`, with a unique server-generated session ID.
Only Worker reads its scoped advisory credential. The SQL intent commits BEFORE
HTTP. Recovery from Submitting moves to Held without another POST. Timeout, 5xx,
invalid/oversized responses and uncertain writes are Held, never blindly retried.
401/403/413/429 are known pre-execution rejections in the pinned private runtime.
There is no automatic Held replay/reconciliation endpoint: operator investigation
is required. Do not manufacture success or manually erase the uncertainty.

Queued cancellation prevents submission. In-flight cancellation records intent,
not a confirmed native stop; returned text is discarded. Expired returned text is
also discarded. Nullable real input/output counters are retained once on the
private request (never added again to task/run usage); unknown is not zero.
No token price/billing estimate is implied. Results are plain Razor-encoded text,
no-store, visible only to the requester through the API. Authorized infrastructure
operators still have technical access to persisted data; this is not end-to-end
encryption or a separate Agno human SSO implementation.

Project cascade deletion includes the new table and rejects Submitting/Held
requests and active delivery leases. These safeguards do not authorize deletion.
A draft must not transition a coding task to Done or satisfy independent approval.
Do not enable coding profiles or replace `DisabledGatewaySandbox` from green health.

`AgnoAdvisoryStatusTests` covers optional/foreign/ambiguous bindings, freshness,
limits, strict/private/malformed input and contradictory capability claims.
`AgentRuntimeStatusTests` retains native report/Web/API authorization regressions.
Mock HTTP tests are not actual SSO acceptance; record actual VPS/Web observations
separately on the operational task.
