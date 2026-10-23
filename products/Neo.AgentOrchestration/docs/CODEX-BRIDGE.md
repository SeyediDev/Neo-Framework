# Private Codex CLI bridge

The optional Codex lane reuses the product HTTP Harness, gateway journal,
Outbox/Hangfire, ownership and Review boundary. It is not a second board,
scheduler, upstream Codex HTTP API, A2A reviewer or deployment authority.
The HTTP protocol here is **Fanasa-owned** `fanasa-codex/v1`.

The local bundle includes source/tests and `codex.runtime.template.json`, always
disabled and intentionally invalid until all operator pins/paths are supplied.
It contains no model profile, transport key, acceptance suite or installed CLI.
Do not turn the template on or copy synthetic pilot pins into a production lane.

## Immutable binding and build

Default remains disabled. Explicitly configure `AgentGateway:CodexBridgeEnabled`,
the existing gateway/native opt-ins and an exact scoped `Engine=codex` connection.
Retain separate dispatch/callback/native/model secrets and verified production
HTTPS. Additional pins: `NativeAgents:Connections:<key>:CodexRuntimeVersion` and
`AgentGateway:Bindings:<key>:CodexAcceptanceSha256` (lowercase SHA-256 of the
operator suite). Repository URL, immutable revision, sandbox identity and image
digest are mandatory. Changed pins fence old handles/reservations; opted-out
Hermes/OpenCode fingerprints are unchanged.

Build `deploy/agents/Dockerfile.codex-bridge` from the reviewed installed base.
Verify a local base reference's immutable ID before AND after the network-disabled
build; pin the resulting image ID, never a mutable tag. Check helper readability
as UID 10000 before inference. No credentials in context/layers. Verify the CLI
supports `codex_workspace.py`'s flags; 0.162.1 is the tested pin, not a claim about
every future release. Docker's local log driver uses bounded rotation with
`compress=false` for `max-file=1`; verify actual container startup, not just create.

## Trusted runtime boundary

`codex_runtime.py` runs as the dedicated rootless Docker owner. SQLite holds only
native intents/frozen receipts, not product tasks or a competing queue. It does
not call product SQL or impersonate callbacks. Operator configuration fixes local
repository/revision, image, scope, engine ID/version, seccomp hash, acceptance
directory/hash/count, model route and private credential references. Task input
supplies only reviewed bounded instructions/context.

Capacity is one; exact bytes deduplicate. Intent precedes Docker mutations and
container identity precedes start. Conflicting bodies, interrupted native writes
and validation crashes are held, not resent under new IDs. Pre-start/post-exit
inspection enforce the same policy. `ClosedBeforeExecutor` is an operator-only
resolution for a proven never-started intent, NOT an exit/success receipt or
permission to callback. No generic recovery/approval endpoint is exposed.

Executor: nonroot, read-only root, one dedicated workspace volume, ephemeral
`/state` and `/tmp`, capabilities dropped, reviewed seccomp, 768 MiB/1 CPU/128 PIDs,
no host mounts/socket/provider key. An internal network connects only its per-run
relay; no published ports. The relay receives the real selected profile on private
stdin; executor receives a short-lived relay key. Relay fixes HTTPS host/path/model,
validates/pins public DNS, validates TLS, denies redirects/remote tools and bounds
request size/lifetime/count. These are NOT token/spending limits. Each deployment
still needs real network-isolation acceptance; flags alone are not proof.

The loopback server requires TLS and exact transport auth, run, binding and
idempotency headers. Ready/submit/observe/evidence/stop routes live under
`/v1/codex`; no task-selected commands, paths or URLs. Stop acknowledgement never
proves exit. Shared C# deadline is 30 seconds including response body; uncertain
writes/collection require reconciliation, not replay.

Native POST bodies are bounded UTF-8 buffers with a known `Content-Length`,
not chunked `JsonContent`. The private server deliberately rejects transfer or
content encoding and ambiguous length headers before admission. A rejected
HTTP request is not proof of native absence: reconcile the same run against
the private ledger and daemon resources before any explicit operator recovery.
Never reset gateway/ownership rows or fabricate a callback to clear a hold.

## Evidence, tests and usage

Only trusted daemon inspection, CLI JSONL and the stopped-volume collector supply
receipts. Tool output/final prose cannot forge top-level completion. Exit, scope
and isolation are rechecked before freeze. Bounded snapshots reject traversal,
links and private-store paths. Independent tests run in another offline container
against an isolated snapshot with an immutable operator `check.py`, not model tests.
Acceptance directory contains only that file and is not group/world writable.

The trusted suite must capture untrusted subprocess output, not import repository
code into its supervisor. It emits exactly one JSON report: `protocol` equal to
`fanasa-acceptance/v1`, actual `acceptanceSha256`, observed `testCount` and boolean
`passed`. Exit/count must agree; configured count or prose alone is insufficient.
Receipts contain hashes of snapshot, deterministic **change manifest** (not a full
patch) and test output. Success requires actual changed files and positive tests.
Raw source, prompts, model prose and private errors do not enter task evidence.
Private artifacts are retained; automatic retention/cleanup is not implemented.
Cleanup failure never rewrites a frozen result.

CLI usage has one stable report per terminal turn, nullable inclusive input/output
and included cache/reasoning subsets. With both `UsageReportsOptIn` settings,
journaled observations enter the authenticated product callback atomically with
result/evidence; see [METERING.md](METERING.md). Never duplicate through manual
task usage. Billing, hard budgets and the broader `ORCH-TOKEN-INGEST` story remain
separate; source implementation is not live callback acceptance.

## Verification and live limits

From `deploy/agents`, run:

```powershell
python -B -m unittest -v test_codex_runtime test_codex_evidence test_codex_model_proxy
```

C# filter: `NativeAgentAdapterTests|GatewayRunTests|GatewayBindingTests|HarnessProtocolTests`.
Python tests exercise actual temporary SQLite, subprocess bounds and loopback
HTTP; Docker/model fixtures are doubles. C# tests use real loopback HTTP, not live
agent execution. A successful explicit synthetic VPS pilot is NOT a panel-owned
run. Production TLS, gateway schema/activation, deployed usage ingestion and an
authentic panel → executor → evidence → callback → Review run remain gates. A
green runtime page does not enable execution.

### Explicit synthetic VPS acceptance — 2026-10-23

Codex CLI 0.162.1 with the user's selected private `gpt-6.1-sol` route completed
one synthetic Python file and passed three externally held offline cases.
Runtime reservation `da9ecdfa-88f9-4ae5-a4a7-7d45a206b824` froze its receipt;
exact-body duplicate and a reopened runtime returned the identical result without
a second inference. Reported usage: input 33086, output 169, cached input 30720,
reasoning 0. These are provider counters, not verified billing or total chat usage.

Fresh rootless inspection and a separate no-credential network probe verified
nonroot execution, no published ports, no real key in executor inspect/logs/private
artifacts, stopped relay and rejection of direct WAN/host-control-plane connections.
Receipt SHA-256: `08b50c5d73132ecf6b2ff50edae7db3420febe9a8a67916535fa31eee46d743c`.
The frozen workspace and manifest remain private; this was not product source or
a managed panel run. No product callback, gateway schema migration or production
dispatch activation occurred. Two earlier attempts failed before executor startup
(helper permissions, Docker log-driver compression); they were explicitly
reconciled against daemon state, not blindly replayed or called successful.

For pinned CLI 0.162.1, the collector recognizes only the exact recoverable
`Reconnecting... waiting for network (Connection failed: error sending request)`
notification inside a single turn that subsequently ends with `turn.completed`.
Unknown error events, `turn.failed`, missing completion and post-completion output
still fail closed. Executor exit, immutable pins and independent offline tests
remain mandatory. This parser repair never rewrites an already frozen receipt.

Upstream: [non-interactive Codex](https://learn.chatgpt.com/docs/non-interactive-mode),
[credential separation](https://developers.openai.com/api/docs/guides/agents-api/environments/security).
