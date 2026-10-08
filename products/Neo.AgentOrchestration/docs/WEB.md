# Independent management Web

`/product` is the public, static Fanasa-branded introduction, linked from the
shared header. It makes no API requests and exposes no tenant/task data. Its
illustrative workflow is labeled as a demo; SaaS, token metering and mobile
features are labeled as roadmap. `/` retains the configured board redirect.
`ProductPageTests` check anonymous access without private API calls and confirm
workspace selection remains protected outside opt-in local development.

The Razor Web is an HTTP client of API v1. Its only product project reference is
Contracts: it does not read SQL, import legacy data, start a worker or reference
Hyper, Neo.Bpms, Domain or Infrastructure. API permission and domain rules remain
authoritative. Native work management is independent of future GitHub/GitLab/
Azure DevOps adapters; those adapters are not implemented or activated here.

The Web also exposes a minimal installable PWA shell. It caches only versioned
static CSS, JavaScript, manifest and the brand asset. It never caches HTML,
authenticated API responses, cookies, task content or tenant data; authenticated
navigation therefore still requires network access and the normal OIDC session.

## Setup

Run the API using [API.md](API.md) and provision the independent catalog using
[DATABASE.md](DATABASE.md). An enabled organization/workspace must already exist;
bootstrap/installer is a separate milestone. Web startup does not create them.

Configure Web separately (environment variable names shown):

The Web panel starts OIDC directly from `/Login`; it does not use a separate
SSO landing page. After sign-in, `/Workspace` reads the authenticated user's
`nao_grant` claims through the API and presents only the organizations and workspaces
the identity may read. It never asks the user to type opaque GUIDs in the normal
flow. In local development the default client is
`fanasa-work-management-web` in realm `fanasa`. Register its exact callback
`http://127.0.0.1:5181/signin-oidc` (and the corresponding logout URL) in
Keycloak. Production must replace the development issuer/client settings with
the approved HTTPS values through environment or secret management.

| Setting | Value / meaning |
| --- | --- |
| `OrchestrationApi__BaseUrl` | API root; HTTPS, or loopback HTTP locally. No URL credentials, query or fragment. Default `http://localhost:5180/`. |
| `WebAuthentication__Authority` | Trusted HTTPS OpenID Connect issuer |
| `WebAuthentication__ClientId` | Registered Web client using authorization code with PKCE |
| `WebAuthentication__ClientSecret` | Server-side client credential where required; inject privately, never commit |
| `WebAuthentication__ApiAudience` | Exact delegated API audience for refreshed JWT validation; falls back to `Authentication__Audience` when hosts share the intended environment file. Required for refresh. |
| `WebAuthentication__Scopes__0`, `__1`, ... | Issuer-specific delegated API scopes; `openid` and `profile` are included |
| `OrchestrationApi__DefaultOrganizationId` | Optional workspace chooser default, not an access grant |
| `OrchestrationApi__DefaultWorkspaceId` | Optional chooser default, not an access grant |
| `AllowedHosts` | Your deployment hostname(s); defaults allow loopback only |

Register the exact HTTPS Web callback URL `/signin-oidc` with the issuer. Configure
the issuer so the access token has the API audience, subject and workspace-bound
`nao_grant` values documented in API.md. ID and access tokens must identify the
same subject. Scope names and resource/audience mapping belong to your issuer;
a successful Web login alone does not grant API access. No default identity
server, shared application token or password form is included. The explicit
local development mode described below is not a deployment authentication method.
OIDC discovery and the issuer must use HTTPS. Serve Web over HTTPS in deployment;
if hosted behind a proxy, configure trusted forwarding and the public callback
at the deployment layer, not arbitrary forwarded headers. Configuration changes
require a host restart.

Start API and Web in separate terminals:

```powershell
dotnet run --project products/Neo.AgentOrchestration/src/Neo.AgentOrchestration.Api
dotnet run --project products/Neo.AgentOrchestration/src/Neo.AgentOrchestration.Web
```

The local Web launch profile is port 5181. Public `/` redirects to the configured
board when both default scope IDs are valid, nonempty GUIDs; otherwise it redirects
to `/Workspace`. This redirect does not call the API or grant workspace access.
`/health/live` is liveness only. `/Workspace` requires login (or the constrained
local mode below) and takes organization
and workspace GUIDs. Missing issuer configuration keeps public pages available,
but `/Login` returns 503 and protected pages redirect to login. No fake board is
substituted when SQL/API is unavailable.

### Local development without login

Set `NEO_LOCAL_DEVELOPMENT=true` on both Development hosts and bind them to
loopback. Web checks the actual host environment and direct request IP on every
protected request; remote, unknown-peer and forwarded/proxied requests do not
qualify. The board is not globally anonymous. The Web forwards local access
only to a loopback API destination, with the auditable `local-web` identity/chat.
Production ignores this opt-in and continues to require authenticated sessions
and delegated API tokens. Do not expose this local mode through a reverse proxy
or enable it on shared machines. Tests cover both positive and negative boundaries;
they do not replace live issuer/proxy deployment acceptance.

## Available workflow

The board supports [confirmed Kanban transitions](KANBAN.md) with pointer handles
and a keyboard/touch-accessible move menu, using the same SPA and API boundaries.

The scoped /templates page manages [versioned project templates](PROJECT-TEMPLATES.md):
immutable publication, parameterized preview, explicit atomic instantiation and
provenance. It uses the same authorization and anti-forgery boundaries.

Routes use `/work/{organizationId}/{workspaceId}`:

| Page | Operations |
| --- | --- |
| Board | Combined project/domain/role/status/archive filters, 50-row paging, full-filter time metrics, task/subtask cards |
| `/new` | Task or same-project child creation, description, priority, estimate |
| `/items/{id}` | Claim, status, timer, estimate, archive/restore, dependencies, append-only notes, commit/test/artifact evidence, children, full history |
| `/manage` | Project creation/rename/disable; roles and agent profiles; workflows, transitions, evidence/approval gates |
| `/items/{id}` execution section | Gate preview, independent approval/rejection, fake/HTTP run with explicit unchecked external-execution consent |
| `/runs/{id}` | Run and profile snapshot, delivery state/attempts/errors, chain links, versioned reevaluation and return of stopped assignment |

Writes are anti-forgery-protected POST forms; the server forwards a per-user
access token to the API. The API still requires `read` plus `write`, `configure`,
`approve` or `execute` for the respective operation. The UI is not a permission
boundary; seeing a form does not grant its permission. Foreign scopes are denied
by the API. HTML encodes text and preserves line breaks. A self-hosted JavaScript
navigation layer provides the [server-driven SPA](SPA.md); no external scripts
or browser token storage are used. Native Razor forms remain the no-script fallback.

Record versions accompany work/workflow mutations. A 409 displays a safe error
and preserves entered data in SPA mode; explicitly refresh current data and inspect
it before submitting again. Commands are not
automatically retried. Creation forms now carry a stable request ID: resubmitting
the identical form returns the existing task; different content under that ID
conflicts. After an uncertain result inspect the board before changing intent.
The original description remains unchanged and subsequent context goes into logs.
Simulation/reevaluation forms carry request IDs;
refreshing the page starts a new user command, not a retry mechanism.

Time and progress bars are **elapsed time / estimated budget**, not percentage of
work completed. Values and execution states refresh on navigation/refresh, not by live
polling. Subtasks appear as board rows and as direct children in item details.
Archived Blocked/Done items can be restored. No hard deletion is exposed.

The role board groups by current owner first, then the latest imported ownership
record when no current owner exists. Historical role keys/IDs are matched to the
catalog; unknown historical roles stay visibly separate rather than disappearing.
Historical labels never imply active/planned assignment. The role-board filter
covers project, domain, type, role and status after retrieving all filtered pages.
Each catalog role also shows its configured concurrent capacity, active current
work count and a capacity bar; historical-only items do not consume that bar.
API `roleId` continues to mean current owner only, so agent availability checks
stay unchanged.

Simulation requires explicit API/worker opt-in from [RUNS.md](RUNS.md); a queued
request does not mean an agent executed. Configured `http.<connection-key>`
providers require the separate [Harness setup](HARNESS.md) and explicit consent
to forward task/history/evidence and execute the external workflow chain. The
simulation outcome control affects only fake runs. Endpoints/secret references
are server-owned configuration, not credentials to enter in agent instructions.

## Session limits

The browser receives an HttpOnly protected opaque session key. Access, refresh and
ID tokens stay in a server-side memory ticket store. Sessions expire after 30
minutes without sliding expiration. Within that unchanged deadline, a saved
access token is refreshed when less than 45 seconds remain. A per-ticket gate
serializes retrieval/refresh, atomically retains rotated refresh tokens, and
prevents a late renewal or concurrent logout from resurrecting a session.
Discovery issuer and token endpoint must match the configured authority (HTTPS;
explicit loopback development is the only HTTP exception). Refreshed access JWTs
are checked for signature, issuer, exact API audience, expiry and the original
subject. An optional new ID token is checked against the Web client and subject.
The API still validates every delegated request and remains the grant authority.
No offline_access scope, token lifetime extension, application password store,
provider error-body logging or automatic API-command replay is introduced.

Revoked/missing refresh tokens, invalid tokens, timeouts, oversized responses or
ambiguous refresh failures invalidate the local ticket; they do not retry a
possibly consumed refresh token. The SPA login action opens a separate login
window and uses a same-origin, expected-window completion signal to GET the
current page with its in-memory drafts/filter/position retained. It never stores
drafts or tokens persistently and never replays a POST. If popups are blocked it
keeps the original page and asks the user to allow the login window. Native
no-script reauthentication does not preserve in-memory drafts.

Logout removes only the local ticket/cookie;
it does not log the user out of their identity provider.

This store is **single-instance**: restart signs users out; replicas require a
shared ticket store and shared data-protection configuration before deployment.
The Web work actor uses a stable hash of issuer and subject as its chat context,
so reauthentication/restart does not change ownership of already claimed work.
All browsers for the same signed-in subject represent the same Web work actor;
distinct autonomous workers should use distinct identities and their own chat IDs.
This correlation identifier is not a credential or substitute for API grants.

## Verification and acceptance

`WebManagementTests` exercise actual Razor forms through TestServer against the
JWT-protected API and isolated SQL store: task/child/history/time/filter/archive,
stale versions, antiforgery, permissions, scope separation, configuration and a
two-role simulation chain. Separate tests check OIDC code/PKCE challenge and safe
return URLs with test metadata, server ticket storage and stable Web ownership.
The authenticated form fixture exists only in the test assembly; runtime Web has
no test login endpoint. SQL tests require `NEO_ORCHESTRATION_TEST_SQL` per DATABASE.md.

Optional `NEO_WEB_PREVIEW_DIR` exports rendered fixture HTML/CSS for local visual
review, not deployment or a live authenticated session. The fixtures contain no
access/ID token, but their generated antiforgery values and test data should remain
local. A live issuer login, production proxy/callback setup, actual user acceptance
and legacy cutover must still be verified separately. Do not claim these from a
passing build, mocked identity or rendered fixture.

Manual acceptance: sign in with a real delegated API identity, select an allowed
workspace, create/claim a task and child, stop/start time, filter the board, inspect
history, block/archive/restore, configure a fake two-role workflow and run it with
the opt-in worker. Confirm delivery and final state, then sign out/in and resume
owned work. Repeat with a read-only identity and a foreign workspace: writes and
foreign access must be denied. No legacy cleanup is part of this procedure.
