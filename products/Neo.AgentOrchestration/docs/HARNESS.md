# HTTP execution gateway

The opt-in `http.<connection-key>` provider implements **Neo's own**
`neo-harness/v1` protocol. It is not a native Codex/OpenAI protocol. An operator
must supply a compatible execution gateway; installing this product does not
create a model account, authorize repository changes or start a Codex session.
Native Neo work tracking, the fake simulation provider and optional future
GitHub/GitLab/Azure work adapters remain separate choices.

The [Hermes/OpenCode native adapters](NATIVE-AGENTS.md) provide scoped protocol
clients for a compatible gateway. They are not the gateway itself and are not
enabled by API/Worker startup. Preserve the durable reservation, isolated sandbox,
approval and usage-ingestion boundaries before connecting either runtime.

The [gateway execution journal/core](GATEWAY-JOURNAL.md) now supplies independent
SQL reservation/recovery/evidence/result gates. It is internal and disabled by
default, not the required operational dispatch host or installed isolated runner.
The existing HTTP protocol and Outbox/Hangfire startup behavior remain unchanged.

## Configure API and worker

Apply the additive `HttpHarnessDispatch` migration explicitly using DATABASE.md.
Use the independent product/jobs databases and worker from RUNS.md. Configure
the same connection and secret references on both API and worker:

| Setting (environment notation) | Meaning |
| --- | --- |
| `Harness__Enabled` | Explicit `true`; disabled by default |
| `Harness__Connections__demo__Enabled` | Explicit `true` for this destination |
| `Harness__Connections__demo__OrganizationId` | Exact allowed organization GUID |
| `Harness__Connections__demo__WorkspaceId` | Exact allowed workspace GUID |
| `Harness__Connections__demo__Endpoint` | HTTPS POST endpoint, e.g. `https://gateway.example/runs` |
| `Harness__Connections__demo__CallbackBaseUrl` | Public HTTPS API root (optional path prefix) |
| `Harness__Connections__demo__DispatchSecretRef` | `env:NEO_GATEWAY_DISPATCH_KEY` |
| `Harness__Connections__demo__CallbackSecretRef` | `env:NEO_GATEWAY_CALLBACK_KEY` |
| `Harness__Connections__demo__CompactContextOptIn` | Optional explicit `true` to send the versioned `neo-harness/v2` bounded context contract |

Supply those two **different** secret values privately through the environment
or deployment secret store: 32-1024 characters, no whitespace/control characters.
References must match `env:[A-Z][A-Z0-9_]{0,100}`. No values belong in profile text,
source, task logs, CLI arguments or exported settings. Outbound HTTP logging is
disabled; raw response bodies and transport exception messages are not persisted.
The callback rejects fields containing either current transport credential;
this is not a general-purpose detector for other secrets in task/evidence text.

Connection keys use lower-case ASCII `[a-z0-9][a-z0-9_-]{0,39}`. Configure an
enabled agent with provider `http.demo`. Destinations and credentials are
server-owned configuration, not agent-editable endpoints. HTTPS is required,
without URL credentials, query or fragment; redirects are not followed. For
isolated local tests only, `Harness__AllowLoopbackHttp=true` allows loopback HTTP
in Development/Testing. It never permits remote HTTP or production HTTP.

The worker runs when either simulation or Harness is enabled; enabling Harness
does not implicitly enable `fake`. The API never starts a dispatcher.

## Start and dispatch

Use POST `items/{id}/runs` from API.md with current item/workflow versions,
stable `requestId`, selected role/profile and **`allowExternalExecution: true`**.
The caller needs read/execute grants and its chat header. The unchecked Web
consent control provides the same choice. Missing consent, unavailable secrets,
scope mismatch or disabled configuration prevents the claim. Permission to
manage a profile alone is not permission to dispatch it.

Consent covers forwarding task/history/evidence and subsequent external runs in
that workflow chain. A fake-only chain without consent cannot switch to an
external provider automatically. Gate, ownership, approval, version, dependency
and 20-hop checks still apply; consent bypasses none of them.

By default, dispatch freezes the unchanged `neo-harness/v1` maximum 256-KiB UTF-8 snapshot before sending:

- `protocol`, `runId`, `organizationId`, `workspaceId`, `roleId`, `agentProfileId`;
- `model`, `instructions`, `skillPath`, `branch`, `callbackUrl`;
- `work`: the `WorkItemDetails` contract, including logs, time and evidence.

`CompactContextOptIn=true` is a connection-level opt-in and changes the frozen
payload to `neo-harness/v2`. It replaces `work` with `context: WorkContextView`:
bounded task/acceptance text, current owner/version, up to three recent logs and
evidence, and bounded child/dependency links with explicit `omitted` markers.
It does not include full time entries, owner history or the full task graph.
The gateway must explicitly support v2; no connection uses it by default, and
changing the setting changes the destination fingerprint and holds existing
snapshots for reconciliation. This is a context-size optimization, not a
measured provider-token saving or a substitute for reading omitted history.

The request uses POST JSON, `Authorization: Bearer <dispatch-secret>` and
`Idempotency-Key: <run GUID without hyphens>`. **Only 202 means accepted**, not
completed. The gateway must durably deduplicate by run ID, reject a changed
payload under that ID and return 202 for an accepted duplicate. Treat task text
as untrusted input, enforce the approved repository/action scope, and do not
interpret the snapshot as authority to publish, delete or access other systems.

The snapshot lives in `nao.AgentRuns`, not the public run response. It contains
task context, not transport credentials. Apply appropriate database access,
backup and retention controls; automated retention is not implemented yet.
Endpoint/base URL/reference names are fingerprinted. Changing them holds an
existing snapshot for reconciliation instead of sending it to a new destination.
Rotating the values behind the same references is supported; coordinate both
sides so callbacks remain authenticatable. Disabling a connection also prevents
its callbacks until safely restored; plan shutdown/rotation around active runs.

## Authenticated result

POST to the supplied callback URL:

```text
/api/orchestration/v1/organizations/{org}/workspaces/{workspace}/harness/{connection}/runs/{runId}/result
X-Neo-Harness-Key: <callback-secret>
Content-Type: application/json
```

```json
{
  "outcome": "Succeeded",
  "summary": "Completed the authorized change; attached actual verification.",
  "evidence": [
    { "kind": "Test", "reference": "targeted-check", "outcome": "Passed", "details": "Actual command and result" }
  ]
}
```

Use actual evidence, never fabricated commit/test results. Outcomes are
`Succeeded`, `Failed`, `NeedsInput`; evidence follows API.md (maximum 100 entries).
A commit reference must be an actual valid SHA; test/artifact evidence must bind
to the current commit when the workflow requires it. The body is limited to
256 KiB. Unknown members/numeric enum names are invalid. Scope/provider/run
authority comes from the configured key and route, not body fields.

Callback authentication is separate from workspace JWT: JWT alone cannot submit
a result; this callback key cannot access workspace CRUD. HTTP 200 returns
`receiptId` and `duplicate`. Exact repeated structured results return the same
receipt with no duplicate effects; changed results conflict (409). Keep the
result fields/evidence ordering stable on retries. Invalid input is 400,
unauthenticated 401, connection/run mismatch 403 and missing scoped run 404.

The Inbox receipt, evidence, status/time and successful workflow-evaluation
message commit together. Success moves the task to Review; negative outcomes
move it to Blocked and stop time without automatic handoff. A valid callback can
arrive before dispatch HTTP returns because no SQL transaction is held over
the network. Workflow decisions remain distinct from execution outcomes.

## Delivery uncertainty and verification

Network errors, timeout, 408, 429 and 5xx use Neo's bounded three attempts and
30-second retry delay, always reusing the frozen payload/key. Other responses
(including redirects) retain `AwaitingResult` with a reconciliation reason.
Processed delivery means that response was handled, not that the agent finished.
Retry exhaustion does not release ownership or invent a negative agent result;
a late valid callback can still resolve the run. There is no unsafe force-release
or blind replay endpoint. Reconcile against the gateway's run ID before recovery.

`HttpHarnessTests` use real isolated SQL, a loopback Kestrel gateway and the
authenticated API fixture: two-role execution, early/duplicate/conflicting
callback, scope/auth failures, negative results, stable retry payloads, exhausted
delivery and destination-change holds. The full suite retains fake/Hangfire/Web
regressions. These tests are **not** a live model/Codex call or production gateway
setup. Real issuer login, deployment credentials, repository permissions and
a live gateway run remain separate acceptance steps.
