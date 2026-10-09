# Hermes + OpenCode native adapters

User-approved direction: use both runtimes behind one Fanasa control plane.
OpenCode is the proposed coding lane; Hermes is the automation/general lane.
This is routing policy, not a benchmark or mandatory nested-agent chain.
Task ownership, workflow/approval, authoritative scheduling and metering remain
in the existing product. Do not enable Hermes cron/kanban as a parallel board.

## Implemented boundary

`Application/ExternalAgents` defines provider-neutral preparation, submission,
observation and stop requests. `Infrastructure/ExternalAgents` translates native
HTTP for both engines. Existing `http.<connection>` dispatch, v1/v2 snapshots,
callback and durable Outbox/Hangfire remain unchanged. The adapters are **not**
registered by API or Worker startup and are **not** a durable execution gateway.
No installed agent, model/account, live inference or task execution is implied.

Only an authorized gateway may explicitly call `AddNativeAgentAdapters` and
`NativeAgentAdapterFactory.Create(key, scope)`. These are internal service APIs,
not new workspace API/MCP tools. Handles must come from the gateway's trusted
store, not client-supplied fields. Preserve these sequence boundaries:

1. Durably reserve run ID, scope, immutable task body/model/binding and sandbox.
2. Prepare native identity. OpenCode creates an empty session, without inference;
   Hermes prepares a deterministic local correlation identity without a network call.
3. Persist preparation before submitting. Persist the returned Hermes native run ID.
4. Observe the original run; report stable completed-message usage once only.
5. Collect actual diff/test evidence in the sandbox, scrub it, then use the existing
   authenticated callback. Native completion alone is not successful acceptance.

These clients deliberately do not implement durable reservation, sandbox setup,
callback delivery, a full SSE consumer, paging, approval replies or UI controls.
Those must be implemented and tested before enabling external execution.

## Configuration (gateway only; disabled by default)

| Setting under `NativeAgents` | Meaning |
| --- | --- |
| `Enabled` | Explicit global opt-in; absent means disabled |
| `Connections:<key>:Enabled` | Explicit connection opt-in |
| `Connections:<key>:Engine` | Exactly `hermes` or `opencode` |
| `Connections:<key>:OrganizationId/WorkspaceId/ProjectId` | Exact allowed GUID scope |
| `Connections:<key>:Endpoint` | Server-owned HTTPS root/profile prefix, no URL credentials/query/fragment |
| `Connections:<key>:SecretRef` | `env:FANASA_HERMES_TRANSPORT_KEY` or `env:FANASA_OPENCODE_TRANSPORT_KEY`; values stay outside source |
| `Connections:<key>:ModelProvider/ModelId` | Explicit operator-approved model route |
| `Connections:<key>:Username` | OpenCode Basic username; default `opencode` |
| `Connections:<key>:OpenCodeDisjointTokenAccounting` | Explicit source-reviewed normalization opt-in; absent means no converted OpenCode usage |
| `Connections:<key>:OpenCodeOmittedStatusIsIdle` | Source-reviewed status-map opt-in; absent entry may mean idle only after this is explicitly enabled |
| `AllowLoopbackHttp` | Test/development loopback only, never production HTTP |

Connection keys match `[a-z0-9][a-z0-9_-]{0,39}`. Directional gateway dispatch,
callback, native transport and model credentials must remain distinct. Resolve
references privately; no secret values in profile/instructions/tasks/CLI/logs.
Connections fence metadata, **not OS isolation**. Each execution needs a dedicated
non-root sandbox/worktree and bounded filesystem/network/resources; never mount
host credentials, root keys, SQL/SSO secrets or a Docker socket. An engine's profile
or memory key alone does not provide safe multi-tenant execution.

## Recovery, permissions and accounting

- Both use a bounded 30-second HTTP client, no redirects, no outbound loggers,
  64-KiB prompt/16-KiB instruction limits and 1-MiB response limit. Upstream output,
  command strings, reasoning and raw errors are not returned as task evidence.
- Writes are not retried automatically. An uncertain response exposes a stable
  code and `RequiresReconciliation=true`, without secret-bearing exception chains.
- Hermes sends run GUID as `Idempotency-Key` and a per-org/workspace/project/run
  memory key. Native keys expire 24 hours after last status update: gateway records
  must outlive them; never blindly recreate after expiry or interrupted shutdown.
- OpenCode persists session before `prompt_async`, using a stable `msg_<run-guid>`.
  Stable message identity is correlation, not proven exactly-once execution.
- Stop acknowledgement is not cancellation. Reconcile native state **and sandbox
  executor exit** before releasing a lease; `Unknown` is not success/failure.
  An idle OpenCode session without a correlated completed final turn stays Unknown.
  The reviewed OpenCode status map deletes idle entries; the explicit opt-in above
  handles this without treating an arbitrary missing status as success. A correlated
  completed final message is still required, and sandbox-exit gating remains separate.
- Hermes waiting state and pure scoped OpenCode permission-event translation signal
  AwaitingApproval. The gateway must durably latch this state until an authorized
  human resolves the matching request. Do not override it with a stale status poll,
  auto-approve, grant persistent permission, or reinterpret it as independent review.
  Hermes capability/event schema must be verified against the installed version;
  unsupported event shapes do not become grants. Reply methods are not implemented.
- Usage identities are actual served provider/model, not requested route defaults.
  Hermes usage is a terminal run report, including failed/cancelled/interrupted runs
  when the provider exposes counters/runtime. OpenCode usage is one immutable report per
  completed correlated assistant message, not both message and step-finish totals.
  A saturated 100-message page yields Unknown and no incomplete report set; full
  paging/reconciliation must precede acceptance of longer runs.
- OpenCode's reviewed source uses disjoint input/cache-read/cache-write and
  output/reasoning. Normalize inclusive input = input + read + write and inclusive
  output = output + reasoning **once**, leaving cache-read/reasoning as subsets.
  Missing categories remain null. Opt-in must match the installed pinned source.
- Observed usage is provider-reported, not verified billing. Stable report IDs must
  pass trusted run-scoped ingestion and conflicting retries must be rejected.
  Repeated observation must not add the same report again. Neither adapter writes SQL
  task lifecycle or fabricates budget/progress. Hard spending enforcement is separate.

## Reviewed upstream contracts — 2026-10-09

- [Hermes API](https://hermes-agent.nousresearch.com/docs/user-guide/features/api-server/)
  and [capability/approval source](https://github.com/NousResearch/hermes-agent/blob/5f045f842a60184748dda30acb9fecbd961cc18b/gateway/platforms/api_server.py).
  Source pin is a review anchor, not an installed runtime claim.
- [OpenCode Server](https://opencode.ai/docs/server/), generated SDK types linked
  there, and [usage normalization](https://github.com/anomalyco/opencode/blob/388406238bd5ca15564a762840a2362c3a45bd9c/packages/opencode/src/session/session.ts).
  [Status-map semantics](https://github.com/anomalyco/opencode/blob/388406238bd5ca15564a762840a2362c3a45bd9c/packages/opencode/src/session/status.ts)
  were checked at the same revision.
  Check the deployed `/doc` and capability/schema before enabling a pinned release.

`NativeAgentAdapterTests` run actual isolated loopback Kestrel HTTP, not real agents
or a model. They verify request/auth/scope/correlation/recovery/error/usage behavior.
Live acceptance needs both pinned runtimes, sandbox, authorized model credentials,
durable gateway and a small genuine board task with real evidence → Review.
