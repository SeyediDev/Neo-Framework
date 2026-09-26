# Neo Agent Orchestration

Independent product under Neo-Framework. This first milestone provides separate
API and Razor Web hosts, a Neo-backed CQRS status endpoint, and host smoke tests.
It is a foundation, not a completed task-management or agent-execution service.

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
