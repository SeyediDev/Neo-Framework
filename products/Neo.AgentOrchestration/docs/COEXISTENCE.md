# Coexistence and acceptance gate

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

No cleanup approval has been recorded. No data cutover has occurred.
Installer removal must preserve user data by default. A different machine or
account gets its own authentication; exported settings contain no credentials.
