# Agent orchestration product

This is an independent product inside Neo-Framework. Read README.md and
docs/COEXISTENCE.md before changing its deployment or migration behavior.

During migration, existing WorkManagement is the operational task source:
project NEO, domain agent-orchestration, parent NAO-MIGRATION. Read the current
owner, chat, role, dependencies and logs before claiming or resuming an item.
Do not take another chat's work. The user-designated main chat uses develop;
concurrent workers use separate branches/worktrees. Preserve unrelated edits.
Record time, commit and test evidence in the task database. The planning
Markdown documents are not a second operational board.

The reusable product skill is
[neo-agent-orchestration](.agents/skills/neo-agent-orchestration/SKILL.md).
Read it when using an authorized Neo workspace for task/run coordination.
Its presence does not replace the migration board: this repository still uses
WorkManagement until approved cutover. Do not copy live backlog records into
the new product merely because its MCP is available.

Legacy Hyper must continue operating until migration acceptance. Removing or
redirecting the old implementation, database or settings requires the user's
explicit approval after verification. Starting a host, installing or migrating
this product must never trigger legacy cleanup or dispatch imported agent runs.

Use actual Neo contracts. Keep Domain independent of product infrastructure;
Web references Contracts and calls the API, with no database access.
Do not add Hyper or Neo.Bpms references. Keep credentials out of code, logs,
task context, release bundles and exported settings.

Build and verify the affected product behavior. Do not claim a database import,
real harness execution, installer or UI capability based on scaffolding.
Record the limits of smoke tests and external-dependency availability.
