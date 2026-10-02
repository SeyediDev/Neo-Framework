# Agent orchestration product

This is an independent product inside Neo-Framework. Read README.md and
docs/COEXISTENCE.md before changing its deployment or migration behavior.

The current operational task source is the independent Neo orchestration API
backed by NeoAgentOrchestration. Product development is project NEO-ORCH;
historical migration records remain in project NEO under NAO-MIGRATION. Read the current
owner, chat, role, dependencies and relevant context before claiming or resuming.
Prefer the bounded context endpoint/neo_work_brief and milestone checkpoints;
read older history only when needed. Do not reload unchanged instructions or the
whole board for each edit. A compact receipt supplies the latest mutation version.
Do not take another chat's work. The user-designated main chat uses develop;
concurrent workers use separate branches/worktrees. Preserve unrelated edits.
Record time, commit and test evidence in the task database. The planning
Markdown documents are not a second operational board.

The reusable product skill is
[neo-agent-orchestration](.agents/skills/neo-agent-orchestration/SKILL.md).
Read it when using an authorized Neo workspace for task/run coordination.
Use the current installation's authorized organization/workspace. For the local
development installation the organization is 2b9b57ca-ce22-8456-b465-f71a74f66825
and workspace is 10d4741c-b839-7f5a-8ad6-4a4f065e5b9b. Resolve projects and roles
from the catalog, never hardcode these identifiers into the reusable product.
Use the scoped API/MCP for task changes; do not write ownership through SQL.
For GitHub destinations, follow the skill's
[active-project binding](.agents/skills/neo-agent-orchestration/references/project-binding.md):
primary project Git remote selects the repository; the current application's
active project label selects the GitHub Project title, not the internal backlog key.
WorkManagement is retained legacy data, not a parallel live backlog. Do not
recreate historical NAO tasks or silently mark them complete from their age.

The user subsequently approved independent cutover and removal of the legacy
Hyper WorkManagement project/UI in this main chat. That approval does not
authorize deletion of the legacy database or unrelated Hyper runtime. Preserve
retained data. Starting a host, installing or migrating this product must never
trigger cleanup or dispatch imported agent runs. See docs/COEXISTENCE.md for
historical gates and current verification limits.

Use actual Neo contracts. Keep Domain independent of product infrastructure;
Web references Contracts and calls the API, with no database access.
Do not add Hyper or Neo.Bpms references. Keep credentials out of code, logs,
task context, release bundles and exported settings.

Build and verify the affected product behavior. Do not claim a database import,
real harness execution, installer or UI capability based on scaffolding.
Record the limits of smoke tests and external-dependency availability.
