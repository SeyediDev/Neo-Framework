# Private VPS native runtime installation

The explicit installer in `deploy/agents/runtime-admin.py` prepares pinned
Hermes/OpenCode/Codex **installation/readiness** images. It is not a repository
executor, tenant sandbox lifecycle, native gateway adapter for Codex or a scheduler.
The existing product Outbox/Hangfire remains the only future dispatch path.

## Pins and installation boundary

`runtime-pins.json` records official release/archive URLs, SHA256 and a resolved
Python image digest. OpenCode uses the Linux x64 baseline binary; Codex uses the
complete native package, including its bundled resources. Hermes source is
revision `5f045f842a60184748dda30acb9fecbd961cc18b`; its minimal Python core/API
environment uses the upstream uv lock, Python 3.14 and the same fixed SQLite
source/checksum as the reviewed upstream Dockerfile. Upstream's `sms` extra is
used solely for its minimal locked aiohttp dependencies, not an enabled SMS
integration. No Chromium/desktop, third-party account, model, cron or second
Kanban is enabled.

The source archive checksum is a local integrity pin for the immutable GitHub
commit archive, not an upstream signature. apt packages in the pinned build base
are recorded by the build log but are not from a time-frozen apt snapshot; do not
claim bit-for-bit reproducible native images. Pin deployed local image IDs as
well as the reviewed archive/checksum manifest before operational execution.

Prerequisites: Debian host, dedicated `fanasa-agent-runner` with nologin, no
docker-group membership, subordinate UID/GID ranges, enabled user linger and a
rootless daemon. Keep existing rootful Docker, unrelated containers and services.
The official rootless installer can coexist using `install --force` **as the
dedicated non-root user**, never by disabling the existing rootful daemon/socket.
Check cgroup v2, memory/swap/CPU/PID enforcement. Use an installation-specific
user-slice ceiling to keep native image builds away from existing service memory.
The health-unit example's `user@1000` is this VPS's verified account ID, not a
portable fixed UID; resolve it on each installation.

Place only reviewed operator files in root-owned, non-linked
`/opt/fanasa-agent-runtime/operator`; download/build context is under `build`,
owned by the dedicated runner. `runtime.dockerignore` excludes unrelated context.
Run `runtime-admin.py install` explicitly as operator; no API startup runs it.
The command verifies every hash before building and validates actual CLI versions
or Hermes core/SQLite imports without inference. It refuses different existing
container identities instead of replacing/deleting them. Failed builds retain
bounded operator logs for diagnosis. Native secrets are generated separately and
remain in mode-0600 files; values never enter source, arguments or task evidence.

Readiness containers use numeric UID/GID 10000:10000, read-only root, ALL
capabilities dropped, no-new-privileges, 512 MiB RAM/no extra swap, 1 CPU,
128 PIDs, bounded logs/temp, and **empty** named state/workspace volumes. No host
directory, operator home, model credential or Docker socket is mounted.
The dedicated network is Docker `--internal`; no native host ports are published.
Docker does not implement host publication for this internal bridge. The operator
samples the authenticated loopback endpoint inside the container as its existing
non-root user; it does not change network policy or mount a socket into the agent.
No raw agent API is exposed by Nginx/public ingress.
This network intentionally has no model egress: it is not the networked execution
sandbox policy or evidence that per-run tenant isolation is implemented.
Codex is a verified offline CLI image, not a persistent native HTTP API.

## Panel observation connection

`runtime-admin.py collect` performs bounded, authenticated, read-only in-container loopback
health reads and also requires anonymous rejection. It writes an atomic,
root-owned 0600 observation at `/opt/fanasa-agent-runtime/status.json`, without
endpoint URLs, credentials, prompts, native raw responses or host paths in the
public DTO. Optional `fanasa-agent-runtime-health.service/.timer` refreshes that
observation every minute; this is health sampling, not a model/job dispatcher.
The API's existing service account must have private read access. Do not broaden
filesystem permissions or workspace grants just to display health.

Operator config under `AgentRuntimeStatus:Bindings:<key>` contains exact
OrganizationId, WorkspaceId, ProjectId and absolute StatusFile. No request can
choose a path/endpoint. API GET
`projects/{projectId}/agent-runtimes` requires existing project membership plus
workspace `read` and `configure` permissions. Missing/ambiguous scope, malformed,
linked, oversized, older-than-three-minute or future reports fail closed. The Web
calls this scoped API, never SQL/Docker directly. Navigation **اتصال ایجنت‌ها**
and the settings link open a SPA-compatible GET project selector/status page.

The v1 observation contract **always sets ExecutionReady=false**. Installation,
private authenticated liveness, and task execution readiness are separate
fields. No report can unlock a task, release a lease, fabricate token usage or
become result evidence. A stale report does not stay green. Codex explicitly
shows CLI installation with managed connection still pending.

## Operational execution remains gated

Do not replace `DisabledGatewaySandbox`, enable Worker/bindings or start a
native task because this report is healthy. Remaining acceptance includes:
per-run scoped repository/revision/workspace, restricted model egress with scoped
credentials, trusted collector bound to journal lease/payload/handle and actual
executor exit, real diff/tests, authorized permission/reconciliation handling,
authenticated result delivery/trusted usage ingestion, and a small pilot → Review.
Codex requires a supported optional adapter before a panel profile can select it.
Do not point `http.codex` directly at an arbitrary CLI/websocket or claim the
Hermes/OpenCode adapter already supports Codex.

Verify actual versions, container limits/mounts/network, auth rejection, private
ports, persistence/restart and existing API/Web health on the target installation.
Tests with mocks, fresh image labels or HTTP 202 do not establish live task
acceptance. Keep unresolved acceptance on the operational board, not this guide
as a second backlog.
