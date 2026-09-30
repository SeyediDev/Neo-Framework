# SaaS architecture and acceptance roadmap

Status: design baseline, 2026-09-30. This is not a production-readiness claim.
Operational ownership and progress remain in NEO-ORCH, not this document.

## Existing implementation, inspected

Organization / Workspace / Project scope, composite SQL keys and issuer-signed
`nao_grant` values bind organization, workspace and permission together.
Work APIs validate scope and owner/version; SQL delivery uses fenced leases.
Web sessions remain single-process; no SaaS onboarding, team membership console,
runner fleet control, entitlement billing, or trusted usage ingestion is complete.
Local anonymous convenience is opt-in Development/loopback only, never a SaaS mode.

## Proposed tenancy boundary

One customer organization is one tenant. Workspaces and teams live inside it.
Pending the user's answer, this is a design assumption, not migrated data.
Projects may be granted to teams; agent RoleProfile is a responsibility profile,
not a human security role. Owner/Admin/Member/Viewer must be independently modeled.
Tenant identity comes from authenticated membership, not an arbitrary header.
A shared database is acceptable only with mandatory tenant-scoped repositories,
composite foreign keys and negative isolation tests. Dedicated databases can be
an optional deployment mode, not a per-project copy by default.

## Execution and server management

Separate control plane (identity, tasks, policies, budgets, audit) from execution
plane (approved worker pools, isolated checkouts/containers, tool execution).
Enroll runner identities with explicit administrator approval, short-lived
credentials and revocation. Store secret references, never private keys in tasks.
Prefer outbound worker connections; do not accept user URLs as arbitrary dispatch
targets. Bind pools to tenant/team/project, trusted labels, capacity and policy.
Require heartbeat, drain, offline expiry, fenced leases, replay-safe callbacks,
fair scheduling and recovery. Disabling a runner must stop future dispatch.
Cross-tenant workspaces, caches, artifacts and credential mounts must never mix.
Multiple UI/API instances require shared protected sessions and key storage;
starting a second process does not establish high availability.

## Token measurement

Keep estimates, observed usage and enforceable budget separate. Unknown is null,
not zero. Reports need request/run/source/model identity and idempotent receipts.
Input/output are additive; cached input and reasoning are normally subsets and
must not be blindly added a second time. Provider-specific adapters normalize
semantics explicitly. Report completeness and origin, including manual/imported
reports; a reported count is not a verified invoice. No extraction of unrelated
chat logs. This chat's actual billable usage is unavailable unless a supported
provider export is supplied. Parent summaries must not double-count subtasks.
Enforcement needs reserved budget before dispatch, reconciliation after completion,
concurrency-safe reservations and an explicit policy for missing final usage.
Prices are versioned by provider/model/currency; no hardcoded universal token price.

## Sequential acceptance gates (live backlog keys)

1. ORCH-SAAS-DESIGN: source-backed comparison and audited architecture.
2. ORCH-TOKEN-METER: persisted reports/estimate and honest UI; scope/replay tests.
3. ORCH-SAAS-IDENTITY: onboarding, membership, revoke/suspend and isolation tests.
4. ORCH-SAAS-TEAMS: least-privilege project/team grants, no escalation.
5. ORCH-SAAS-SERVERS: enrolled isolated runners and multi-worker failure tests.
6. ORCH-TOKEN-INGEST: trusted harness reports and budget reservations.
7. ORCH-SAAS-SCALE: distributed sessions, quotas, fair queues and HA tests.
8. ORCH-SAAS-RELEASE: development installer/CI/CD, restore and rollback exercises,
   external OIDC login, TLS, audit, operational alerts and independent security review.
9. ORCH-SAAS-BILLING: entitlement lifecycle and provider reconciliation; payment
   activation and commercial/legal choices require separate approval.

Cross-tenant denial must cover details/list/search/export, reports, callbacks,
artifacts, settings and caches. Test two tenants with overlapping names, two teams,
two runners, stale grants, cancellation/retry and server failure. Preserve imported
history; use additive migrations and verified backups. Do not expose this local
installation publicly or disable authentication to make a SaaS demo work.
