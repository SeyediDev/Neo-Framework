# Scoped API v1

The independent API composes Neo.Endpoint (MVC, versioning and NSwag), MediatR
application commands and the product SQL transaction store. It does not use
Neo.Bpms, Hyper controllers, shared admin authentication or legacy tables.
No startup path migrates, seeds, imports or dispatches work.

## Configuration and access

[Project template routes](PROJECT-TEMPLATES.md) add scoped version publication,
read-only preview and atomic idempotent instantiation. Configure/write grants are
required as documented; generated workflows remain disabled.

`POST items` accepts optional `requestId` (nonempty GUID). The same ID, source
agent/chat and exact body is retry-safe across projects in the authorized workspace.
Replay returns the existing item/current state at the same creation route (201),
not another row. Changed body/source conflicts with 409; omit the ID for the legacy
create semantics. Use a new ID only for a genuinely new request. Receipt and item
are persisted atomically in the workspace-serialized transaction; receipt is a
reserved `ChatIntake` log containing ID and payload hash, never credentials.
Replay also covers archived/closed work and never reopens or reassigns it. Reads
scan workspace intake history; this initial implementation favors correctness
over large-scale indexed intake throughput. There is no hard-delete endpoint.

Work item views include optional `lastOwnerHistory` for display of imported
ownership. It never replaces `ownerRoleId` or grants ownership. Board `roleId`
filters remain current-owner-only; full history remains in item details.

Set `Authentication__Authority` to your trusted HTTPS OIDC authority and
`Authentication__Audience` to the API audience. Production refuses missing
values. JWT signatures, issuer, audience and expiration are validated by
JwtBearer with inbound claim mapping disabled. No built-in signing secret or
token-issuing endpoint is supplied. There is an explicit local development mode:
the actual host environment must be Development, `NEO_LOCAL_DEVELOPMENT=true`,
the direct peer must be loopback, and the request must carry
`X-Orchestration-Local: true`. Forwarded/proxied requests are not eligible.
A header alone is never sufficient. This local actor is `local-web`; mutations
still require the chat header and all scope, version, ownership and domain rules.
Keep this opt-in disabled on shared/deployed machines and bind development hosts
to loopback only. Ordinary grants require an authenticated principal. Fallback
routes, including OpenAPI, still require authentication in local mode.

The issuer must supply one non-empty `sub` (at most 200 characters) and explicit
`nao_grant` claims. A grant is a single string binding the organization,
workspace and permission, using lower-case D-format GUIDs:

```text
{organization-guid}/{workspace-guid}/read
{organization-guid}/{workspace-guid}/write
{organization-guid}/{workspace-guid}/configure
{organization-guid}/{workspace-guid}/approve
{organization-guid}/{workspace-guid}/execute
```

Every scoped endpoint needs `read`. Task mutations additionally need `write`,
configuration changes `configure`, approval/rejection `approve`, and run
start/evaluate/return-assignment `execute`. There is
no wildcard/admin bypass or independent claim-list cross-product. Membership
is issued by the trusted identity provider, not self-selected in request JSON.
Revocation takes effect according to that provider's token lifetime; database
membership administration and the extended policy matrix remain later work.
Organization/workspace enablement and all resource scopes are checked again in
the SQL transaction. Missing/foreign objects return 404 within an allowed route;
an unauthorized workspace route returns 403 before storage access.

Task mutations, approvals and run commands require one `X-Orchestration-Chat` header, 1-200
characters. This is correlation/ownership context, not an authentication secret.
The actor's agent identifier always comes from `sub`; clients cannot set it in
JSON. Clients sharing one subject share its authority, so autonomous agents
should have distinct subjects. An owner cannot approve its own handoff even
using another chat. Unknown JSON members are rejected (400), not silently trusted.

Set `NEO_ORCHESTRATION_SQL` or `ConnectionStrings__Orchestration` privately.
`Orchestration__DatabaseName` defaults to `NeoAgentOrchestration`; its catalog
must match and satisfy the [independent destination policy](DATABASE.md).
The host never reads Hyper settings. With no storage configured, public status
and liveness still work but authorized data requests return 503. There is no
runtime in-memory fallback. A missing/down SQL service likewise cannot become
a success response. Initial organization/workspace bootstrap and user/issuer
configuration are not created automatically; installer/seed work is NAO-017.

## Routes

All routes below are relative to:

```text
/api/orchestration/v1/organizations/{organizationId}/workspaces/{workspaceId}
```

| Method / route | Result / behavior |
| --- | --- |
| GET `catalog` | Workspace, projects, roles, agents, workflows and transitions |
| GET `items` | Paged board plus metrics over **all filtered** items |
| GET `items/{id}` | Item, direct children, dependencies, full logs, evidence and time intervals |
| POST `items` | Create a task/subtask; 201 with a resolvable Location |
| PUT `items/{id}/planning` | ExpectedVersion, Type (Task/UserStory/Bug/Epic), AcceptanceCriteria (null clears, max 8000); owner/version checks and retained history |
| POST `items/{id}/claim` | ExpectedVersion, RoleId, Branch; identity from token/chat |
| POST `items/{id}/status` | ExpectedVersion, named Status, optional Note |
| POST `items/{id}/logs` | ExpectedVersion, Message; append, not overwrite description |
| PUT `items/{id}/estimate` | ExpectedVersion, positive Seconds or null |
| POST `items/{id}/time/start` or `time/stop` | ExpectedVersion; owned InProgress timer |
| POST `items/{id}/archive` or `restore` | ExpectedVersion; Blocked/Done archival rules |
| POST `items/{id}/dependencies` | ExpectedVersion, DependsOnWorkItemId; same-project cycle checks |
| POST `items/{id}/evidence` | ExpectedVersion, Kind, Reference, Outcome, optional Details/CommitSha |
| POST `projects`; PUT `projects/{id}` | Create project; rename (Key is immutable) |
| POST `projects/{id}/disable` | Disable without deleting history |
| POST `roles`; PUT `roles/{id}` | Create or update role configuration |
| PUT `roles/{id}/enabled` | IsEnabled |
| POST `agents`; PUT `agents/{id}` | Create or update agent profile (configuration, not credentials) |
| PUT `agents/{id}/enabled` | IsEnabled |
| POST `workflows`; PUT `workflows/{id}` | Create; update name/enabled with ExpectedVersion |
| PUT `workflows/{id}/transitions/{key}` | ExpectedVersion plus roles, named statuses and evidence/approval gates |
| POST `workflows/{id}/preview` | WorkItemId, TransitionId, optional PreferredAgentProfileId |
| POST `workflows/{id}/approvals` | Both expected versions, WorkItemId, TransitionId, Approved, Reason |
| GET `workflows/{id}/approvals?workItemId=...` | Persisted approval history for the scoped item/workflow |
| GET `items/{id}/runs`; GET `runs/{id}` | Scoped run details and linked delivery outcomes |
| POST `items/{id}/runs` | Opt-in fake or configured HTTP start with stable RequestId and versions; HTTP requires AllowExternalExecution; 202 |
| POST `harness/{connection}/runs/{id}/result` | Separate connection-scoped HarnessKey authentication; idempotent result/evidence receipt (200) |
| POST `runs/{id}/evaluate` | Explicit versioned gate reevaluation with stable RequestId; 202 |
| POST `runs/{id}/return-assignment` | ExpectedWorkItemVersion; initiating subject recovers its stopped assignment; 200 |

Configuration writes return the committed workspace catalog (200). Their
readback is a subsequent transaction and can include intervening authorized
changes. Project/role/agent edits are serialized last-writer-wins; only work
items and workflows currently have client-visible version preconditions.
There are no hard-delete endpoints. Work-item creation supports the explicit
body `requestId` described above, not an arbitrary HTTP idempotency header.
Without it, query by project/key after uncertainty before creating again.
Unique constraints still reject duplicate keys with 409. No command is
automatically retried by the server.

Board filters are `projectId`, `domain`, `roleId`, `status`, `type`, `includeArchived`,
`skip` and `take` (default 50, maximum 200). Filters combine with AND. Sorting
is updated-time descending, then identifier. Metrics count the same filtered
set before pagination. Subtasks are ordinary board rows with ParentWorkItemId;
item details return direct children even if their domain differs from a board
filter. ElapsedSeconds includes any open interval. BudgetUsedPercent measures
time consumed against the estimate, **not percent of work completed**.
The current P0 store reads workspace project graphs to preserve dependency
rules; output pagination is not a claim of database-side large-board paging.

Write requests require the latest GUID `ExpectedVersion` returned by the item
or workflow. Re-read after 409; never retry with an invented replacement version.
Enum input uses names, not numbers: statuses Backlog, Ready, InProgress, Blocked,
Review, Done, Cancelled; priorities Low, Normal, High, Critical; evidence kinds
Commit, Test, Artifact; outcomes NotApplicable, Passed, Failed, Skipped.
Request bodies are limited to 256 KiB. Invalid input is 400, missing token 401,
insufficient grant 403, missing scoped resource 404, stale/illegal/owned state
409, unconfigured/unavailable storage 503. Problem responses do not expose raw
exceptions/provider messages. Avoid credentials in user-entered task text too.

## OpenAPI and verification

Neo's generated document is `/swagger/v1/swagger.json`, available in Development
or when `OpenApi__Enabled=true`, and requires authentication. It describes the
request/response DTOs, JWT requirement, required chat header and per-operation
`x-workspace-permissions`. There is no second hand-maintained JSON specification.
Public `/api/orchestration/v1/system` and `/health/live` expose no task data.

Tests use ASP.NET TestServer and real JWT signature/issuer/audience/lifetime
validation with ephemeral keys only in test code. HTTP contract/lifecycle tests
cover grants, filtering, child/history/time/evidence, stale versions and OpenAPI.
SQL HTTP tests additionally persist configuration/approvals, verify scope FKs
and re-read using fresh contexts in the isolated verification catalog.
These are not a live external identity-provider login, browser verification,
public deployment or execution by a real agent/harness.

Preview and approval are not dispatch. [Simulation runs](RUNS.md) and
[HTTP execution](HARNESS.md) require their respective explicit host opt-in and
read/execute grants. Unavailable providers return sanitized 503; HTTP dispatch
also requires explicit external-execution consent. Reserved `neo-run:` subjects cannot use
workspace endpoints; the run initiator cannot independently approve its own work.
The authenticated callback uses `X-Neo-Harness-Key`, not workspace grants/chat;
its key grants no access to CRUD. OpenAPI describes that separate API-key scheme.
A compatible real gateway is operator-supplied, not an embedded model client. The independent
[management Web](WEB.md) now consumes these HTTP contracts with per-user OIDC
access tokens. Bootstrap installer, legacy import, live gateway setup and user
acceptance remain pending. Existing Companion MCP/skills contracts are unchanged.
