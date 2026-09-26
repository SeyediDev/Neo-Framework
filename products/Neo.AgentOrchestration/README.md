# Neo Agent Orchestration

Independent product under Neo-Framework. This first milestone provides separate
API and Razor Web hosts, a Neo-backed CQRS status endpoint, and host smoke tests.
It is a foundation, not a completed task-management or agent-execution service.

The domain now includes Organization, Workspace and Project factories, normalized
project keys, disabled-parent checks and a WorkspaceScope that rejects a project
operation from a different organization/workspace. These are domain invariants;
membership authorization must still be enforced by the application/API. Database
constraints, project CRUD and data import belong to subsequent milestones.

RoleProfile and AgentProfile are separate workspace-scoped domain models.
Profiles support validated updates and enable/disable; agent selection rejects
disabled, foreign-role/workspace and ambiguous candidates. Choose an explicit
profile when a role has multiple enabled agents. Profiles contain configuration,
not credentials or execution state. The contracts are ready for later API/UI
integration; this milestone does not expose profile CRUD endpoints.

The Work domain and CQRS handlers implement tasks/subtasks, dependency gates and
cycle rejection, owner/chat checks, status transitions, logs, commit/test/artifact
evidence, pause/resume time tracking, forecasts, archive/restore and stale-version
rejection. Time-budget usage is labeled separately from completion percentage.
Application tests use a scoped memory fixture, not a runtime storage fallback.
The SQL store uses Neo EF repositories, workspace transaction locks, scoped
foreign keys, unique assignment/timer indexes and optimistic concurrency. Explicit
versioned provisioning targets an independent database; see [database setup and
verification](docs/DATABASE.md). HTTP task endpoints are still pending; the API
registers only the foundation status handler until membership/endpoint work lands.

Workflow definitions now configure project-scoped, role/status-matched handoffs
from Review/Blocked to Ready and terminal Review-to-Done transitions. The pure
planner checks enabled roles, commit/test/artifact evidence and independent
reviewer approval, and selects the next agent profile. Tests/artifacts must name
the current commit when one exists; the latest result per test wins. Approvals
bind both item and workflow versions, and an owner cannot approve their own work.
These are configuration and preview capabilities, not durable dispatch. The
executor must revalidate in a transaction and enforce membership, dependencies,
children and role availability before persisting handoff/outbox. Authentication,
workflow HTTP/UI management, durable runs and harness execution remain pending.

## Run the foundation

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
the API has an authenticated fallback policy. The Web currently exposes only
public service status. User authentication and task pages are later milestones.
OrchestrationApi:BaseUrl supports HTTPS, or loopback HTTP for local development.

## Migration agreement

The current Hyper API, database, UI, skills and settings remain operational.
No redirect, dual-write, schema deletion or replacement is activated by this
foundation. Cleanup requires **explicit user approval after migration acceptance**.
See [the coexistence plan](docs/COEXISTENCE.md).

Operational ownership, status, time, commits and test evidence live in the
existing WorkManagement database, project NEO, domain agent-orchestration,
parent NAO-MIGRATION. The [planning backlog](../../docs/AGENT_ORCHESTRATION_BACKLOG.md)
describes scope; it is not a second live board.

MCP tools, an installer, data transfer and external harness adapters are planned,
not included in this milestone. Existing Companion skills/MCP contracts are unchanged.
