# Neo Agent Orchestration

Independent product under Neo-Framework, with a scoped SQL-backed API and a
separate Razor management Web. Task/configuration APIs and the independent P0
management Web and opt-in HTTP execution gateway adapter are implemented.
A compatible external gateway and its account/setup remain operator-supplied.

The domain now includes Organization, Workspace and Project factories, normalized
project keys, disabled-parent checks and a WorkspaceScope that rejects a project
operation from a different organization/workspace. These are domain invariants;
membership authorization is enforced by issuer-signed workspace permission
grants at the API. The SQL model enforces scope constraints. Data import and
organization/workspace installer bootstrap remain subsequent milestones.

RoleProfile and AgentProfile are separate workspace-scoped domain models.
Profiles support validated updates and enable/disable; agent selection rejects
disabled, foreign-role/workspace and ambiguous candidates. Choose an explicit
profile when a role has multiple enabled agents. Profiles contain configuration,
not credentials or execution state. Scoped API endpoints now create/update and
enable/disable profiles and the independent Web exposes these operations.

The Work domain and CQRS handlers implement tasks/subtasks, dependency gates and
cycle rejection, owner/chat checks, status transitions, logs, commit/test/artifact
evidence, pause/resume time tracking, forecasts, archive/restore and stale-version
rejection. Time-budget usage is labeled separately from completion percentage.
Application tests use a scoped memory fixture, not a runtime storage fallback.
The SQL store uses Neo EF repositories, workspace transaction locks, scoped
foreign keys, unique assignment/timer indexes and optimistic concurrency. Explicit
versioned provisioning targets an independent database; see [database setup and
verification](docs/DATABASE.md). [API v1](docs/API.md) exposes the task lifecycle,
filtered/paged board, nested history, time and configuration via authenticated
workspace-scoped routes. It has no implicit runtime memory fallback.

Workflow definitions now configure project-scoped, role/status-matched handoffs
from Review/Blocked to Ready and terminal Review-to-Done transitions. The pure
planner checks enabled roles, commit/test/artifact evidence and independent
reviewer approval, and selects the next agent profile. Tests/artifacts must name
the current commit when one exists; the latest result per test wins. Approvals
bind both item and workflow versions, and an owner cannot approve their own work.
The opt-in simulation executor revalidates these gates in its SQL transaction,
including dependencies, children, versions and next-role availability. Workflow
configuration/preview/independent approval and durable fake runs are implemented;
the Web exposes these operations; the HTTP adapter uses the same gates. Preview
does not perform a handoff.

Durable delivery now stages product changes and Neo Outbox messages atomically.
Workspace-scoped Inbox receipts reject conflicting keys and acknowledge duplicate
callbacks without repeating their database effects; a follow-up can be queued in
that same transaction. The executor reuses Neo's EF delivery engine for fenced
leases, recovery and bounded retries; terminal failures remain inspectable.
See [delivery semantics and limits](docs/DELIVERY.md). A separate opt-in worker now
connects Neo Hangfire to the simulation executor. See [run setup and limits](docs/RUNS.md)
for the create/dispatch/internal-callback/handoff/done scenario and return of
stopped assignments. No dispatcher starts from API startup or schema migration;
the opt-in [HTTP Harness](docs/HARNESS.md) adds connection-scoped authenticated
callbacks and immutable dispatch snapshots. No real model execution is claimed.

## Run the hosts

From the Neo repository, with .NET 10:

```powershell
dotnet build products/Neo.AgentOrchestration/Neo.AgentOrchestration.slnx
dotnet test products/Neo.AgentOrchestration/tests/Neo.AgentOrchestration.Tests
dotnet run --project products/Neo.AgentOrchestration/src/Neo.AgentOrchestration.Api
dotnet run --project products/Neo.AgentOrchestration/src/Neo.AgentOrchestration.Web
```

Run API and Web in separate terminals. Local launch profiles use ports 5180 and
5181. The Web calls the API through HTTP and references only public contracts.
The API composes Neo.Endpoint and MediatR. Domain/Application/Infrastructure
projects reference the corresponding Neo libraries. No Hyper or Neo.Bpms
project, layout, static asset or database is required to start these hosts.
No database is created or migrated on startup.

The public system endpoint and liveness expose no task data.
Production requires Authentication:Authority and Authentication:Audience;
the API has an authenticated fallback policy and explicit workspace grant
policies. See [API configuration](docs/API.md) for claims, SQL and OpenAPI access.
The Web exposes service status, OIDC login, board, tasks/history/time, configuration
and simulation/HTTP run monitoring. See [Web setup and limits](docs/WEB.md) for issuer
configuration, workspace selection and single-instance server-side sessions.
HTTP tests do not establish a real external issuer login.
OrchestrationApi:BaseUrl supports HTTPS, or loopback HTTP for local development.

## Optional work-provider direction

Native Neo work management will remain independently usable. GitHub, GitLab and
Azure DevOps are planned optional adapters, not replacements; Neo and an external
provider may be enabled simultaneously. Work tracking, repository/PR management
and agent execution are separate choices. See [coexistence and field authority](docs/WORK-PROVIDERS.md).
External integrations follow core product readiness and are not implemented yet.

## Legacy migration agreement

The current Hyper API, database, UI, skills and settings remain operational.
No redirect, dual-write, schema deletion or replacement is activated by this
foundation. Cleanup requires **explicit user approval after migration acceptance**.
See [the coexistence plan](docs/COEXISTENCE.md).

Operational ownership, status, time, commits and test evidence live in the
existing WorkManagement database, project NEO, domain agent-orchestration,
parent NAO-MIGRATION. The [planning backlog](../../docs/AGENT_ORCHESTRATION_BACKLOG.md)
describes scope; it is not a second live board.

The [operational MCP](docs/MCP.md) exposes scoped work/run tools over stdio,
calling API v1 with the user's issuer token. It cannot access SQL or execute
repository/model work itself. It does not change any global client settings.
An installer and data transfer remain subsequent milestones.
Existing generic Companion skills/MCP contracts are unchanged.

The portable [neo-agent-orchestration skill](.agents/skills/neo-agent-orchestration/SKILL.md)
documents intake, ownership, time, evidence and gated runs using these tools.
It includes its own tool reference and can travel with the product; no global
skill/client installation or current-board cutover is performed here. See
[skill use and verification](docs/SKILL.md).
