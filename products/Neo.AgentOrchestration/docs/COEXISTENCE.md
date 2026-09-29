# Migration decisions and retained-data policy

## Current operational state (verified 2026-09-30)

The user subsequently requested migration with all values preserved, approved
removing Hyper.WorkManagement, then explicitly requested removal of its legacy
admin UI in the main development chat. The independent Neo API is now the live
coordination source; its catalog contains the imported NEO and HYPER projects
and the new NEO-ORCH product-development project. The Web reads the same API.

This supersedes the initial hold/cutover instructions below for this installation,
not for arbitrary customer deployments. The old WorkManagement database is
retained; there is no approval to delete it. Historical NAO task statuses require
evidence reconciliation, not automatic completion. Live API/catalog checks do
not establish full snapshot parity, installer acceptance or disaster recovery.

## Original acceptance gates (historical, not the current board selection)

User decision, 2026-09-26: retain the existing Hyper implementation until migration
is complete; cleanup is allowed only after the user approves the verified result.

1. Build and verify the independent Neo product alongside Hyper.
2. Provision a separate destination database. Keep WorkManagement as the live
   coordination source until an explicitly scheduled cutover.
3. Import a snapshot with source IDs, checkpoints, counts and reconciliation.
   Preserve tasks, children, projects, domains, roles, time entries, logs, chat
   intake, commits, test evidence, profiles, transitions and runs. Do not dispatch
   imported runs automatically.
4. Verify parity and restart recovery against the snapshot. Cutover uses a short
   agreed write pause and final delta reconciliation so late legacy edits survive.
   Concurrent uncoordinated dual writes are not a migration mechanism.
5. Present the independent Web and API, data comparison and installer results
   for acceptance. A passing build alone is not migration acceptance.
6. Keep the rollback copy, legacy deployment and configuration through cutover.
   Record the exact user approval before removing legacy source/deployment/data.
   No automatic cleanup script runs as part of startup, migration or install.

These were the original pre-cutover gates; see the current-state section above
for the subsequent user decision. Do not infer permission for further cleanup.
Installer removal must preserve user data by default. A different machine or
account gets its own authentication; exported settings contain no credentials.
