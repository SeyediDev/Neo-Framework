# Product acceptance audit — 2026-09-30

This is an evidence snapshot, not a second task board. Current status, ownership,
time and follow-up belong in the scoped API. Scope: NEO-ORCH and the historical
NEO migration backlog. Hyper accounting/provider scenarios owned by other chats
are not reclassified by this product audit.

## Executed acceptance

Product commit d78e81e: fresh product build, zero warnings/errors; full product
suite 156 passed, zero failed/skipped, using isolated SQL verification catalogs.
These are actual HTTP/Razor form, SQL, worker and stdio tests, not just GET smoke
checks. They do not establish external model, GitHub Projects or issuer acceptance.

| Requested behavior | Executed coverage |
| --- | --- |
| Create, description, children, filters, time, history, archive/restore | WebManagementTests.Sql_web_forms_manage_task_history_time_children_filters_and_archive_through_the_api; WorkItemTests; ApiContractTests |
| Role/agent configuration and workflow transitions | WebManagementTests.Sql_web_settings_configure_roles_agents_workflows_and_show_a_real_simulation_run; WorkflowTests; SqlRunTests |
| Authentication, stale writes, missing grants, foreign scope, antiforgery | ApiContractTests; LocalDevelopmentAccessTests; SqlApiTests; WebManagementTests |
| Historical role display without invented assignment | RoleHistoryTests |
| Chat intake replay and immutable original request | ChatIntakeTests, including concurrent SQL and repeated Web submission |
| Typed stories/bugs/epics and acceptance criteria | WorkItemPlanningTests; WorkItemPlanningApiTests |
| Template preview, versions, atomic creation, replay, rollback, provenance | ProjectTemplateTests |
| Independent task/run MCP lifecycle | OperationalMcpTests (real stdio, HTTP, JWT and SQL) |

Live deployment applied versioned schema and retained all 93 verified historical
ownership rows with unchanged checksum. The earlier 94-row description is not
a verified baseline and has not been rewritten. Live template preview returned
three items, two roles and one workflow without creating a project; page HTTP200.
Launcher cold start/restart/replay and foreign-port refusal were tested separately.
Visual browser layout and a real external OIDC login are not claimed by these tests.

The seven identified audit children (documentation, root routing, local access,
launcher, migration drift, role history, full live-fixture verification) have
completion evidence on their individual records. No new duplicate defects were
created for already tracked unfinished capabilities.

## Historical reconciliation

Readback covered all 44 NEO and all 18 NEO-ORCH items, including archived items;
neither project had Review/Blocked items at this snapshot. Preserve historical
owners and statuses. Absence of those statuses is not proof of product completion.

| Existing item | Verified boundary / remaining acceptance |
| --- | --- |
| NAO-015 | Imported live records and preserved history exist; complete source/destination snapshot parity and restart/delta reconciliation remain unproven. |
| NAO-016 | Old Hyper compatibility path was superseded by user-approved removal; retain original item pending explicit disposition, do not recreate UI or silently cancel it. |
| NAO-017 | Existing Done retained; provisioner init/migrate/seed/health is implemented and ProvisioningTests pass. Owner not changed. |
| NAO-018 | No clean-machine bundle installation evidence; developer launcher is not an installer. |
| NAO-019 | No product container/compose acceptance; local host startup is not container acceptance. |
| NAO-020 | No signed release artifact/upgrade acceptance; local commits are not published releases. |
| NAO-021 | Scope, auth, local-boundary and concurrency tests pass; full policy/tamper-aware audit acceptance remains. |
| NAO-022 | Item time metrics exist; distributed trace correlation/operations dashboard remains separate. |
| NAO-023 | Workflow forms/planner exist; project-template revisions do not implement workflow draft/publish/rollback. |
| NAO-024 | Existing configured run-provider contracts do not prove additional production providers. |
| NAO-025 | Loopback HTTP harness tests pass; real gateway/model/account execution still needs operator configuration and acceptance. |
| NAO-026 | Retained legacy database is not a backup/restore drill; recovery and retention acceptance remains. |
| NAO-027 | Local 156-test suite is reproducible; load/security CI acceptance is not established. |
| NAO-028 | Provider-neutral coexistence is documented; durable connection/link/field-authority/conflict integration remains. |
| NAO-029 | Approved sanitized Issue bootstrap exists; continuous sync/webhooks/Projects permissions and live two-way acceptance remain. |
| NAO-030 | GitLab adapter remains planned. |
| NAO-031 | Azure DevOps adapter remains planned. |

LSP, ACP and agent-strategy stories remain on NEO-ORCH. Do not mark them supported
from their design notes. GitHub publication must use approved public summaries,
never raw task descriptions, private context or logs. External permission/config
gates must be resolved without disabling native Neo operation or execution gates.
