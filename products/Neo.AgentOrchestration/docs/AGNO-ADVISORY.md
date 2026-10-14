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

Next execution integration must be separately scoped and versioned: explicit
minimal brief/consent, tenant and actor binding, durable existing job path,
idempotency/cancellation, bounded budget, typed result validation and trusted
metering. Uncertain writes cannot be automatically rerun; an advisory draft must
not transition a coding task to Done or satisfy independent approval. Do not enable
native execution profiles or replace `DisabledGatewaySandbox` from green health.

`AgnoAdvisoryStatusTests` covers optional/foreign/ambiguous bindings, freshness,
limits, strict/private/malformed input and contradictory capability claims.
`AgentRuntimeStatusTests` retains native report/Web/API authorization regressions.
Mock HTTP tests are not actual SSO acceptance; record actual VPS/Web observations
separately on the operational task.
