# Identity lifecycle and access policy profile

The destination design for contract-driven rules, scoped roles and TPS/quota enforcement is defined in the [platform architecture](/E:/SJVS/Projects/Neo/docs/fanasa-platform-control-center/ARCHITECTURE.md) and [development plan](/E:/SJVS/Projects/Neo/docs/fanasa-platform-control-center/GROWTH-AND-DEVELOPMENT-PLAN.md), dated 2026-10-10. This file documents the current limited implementation. Publishing the design does not activate live enforcement or the lifecycle worker described below.

## Standard and scope

Reference: [OASIS XACML 3.0, approved core specification](https://docs.oasis-open.org/xacml/3.0/xacml-3.0-core-spec-os-en.html).

XACML separates policy administration (PAP), policy decisions (PDP), attribute information (PIP) and enforcement (PEP). Its request context describes subject, resource, action and environment attributes. Decisions are Permit, Deny, NotApplicable or Indeterminate; obligations require enforcement while advice is optional. The deny-overrides algorithm gives explicit denial priority and distinguishes errors by their possible effect.

This implementation is a **typed, limited profile inspired by those concepts**. It is not a conformant general XACML XML processor. It does not accept arbitrary XML, expressions, caller-supplied external attribute URLs, nested PolicySets or unimplemented algorithms. No XACML interoperability or certification is claimed.

### External PIP sources

PIP source definitions, schema mappings, normalized subject snapshots, source-version receipts, synchronization runs and per-subject checkpoints are persisted in the Fabric database. `/Organization#organization-pip` owns source setup and health; `/Governance` owns the `attribute` rule, independent publication and simulation. See the [Persian operator guide](PIP-EXTERNAL-SOURCES.fa.md) for the complete JSON contract and deployment configuration.

The implemented external transport is HTTPS JSON pull through `IPipExternalConnector`. Every request is correlated to a tenant and subject. A deployment-owned `Pip:Transport:Bindings` entry must approve the exact tenant, endpoint and secret reference; the form's `AllowedHost` is only a consistency check. Default bindings are empty. Credentials are resolved server-side and never copied into Fabric. Redirects, proxies, compressed bodies, arbitrary query parameters and unapproved resolved IPs are rejected. Connections use the validated IP while TLS verifies the original hostname. Private addresses require an exact deployment exception; loopback, link-local and metadata addresses remain prohibited.

Schema version 1 requires a complete envelope and explicit UTC observation time. Each mapped attribute has a normalized name, external field name, type and required flag. Unknown/duplicate fields, missing required values, invalid types, future/stale observations and excessive bodies fail validation. String, boolean, integer, decimal and UTC datetime values are supported; policy comparison uses the declared type. Only normalized attributes are stored; raw bodies are discarded.

Each snapshot retains tenant/source/subject identity, immutable source version, per-subject ETag, observation and expiry, canonical JSON SHA-256 and provenance. Reads revalidate integrity, types, source generation and expiry. The durable version ledger rejects reused versions with different content and older observations. Disabling/re-enabling a source invalidates cached evidence until a new fetch. A 304 requires the same fresh subject snapshot and never extends expiry. A failed refresh leaves the last valid snapshot usable only until its original expiry; error state is visible separately from freshness. Hashing detects inconsistent persisted content; authenticated HTTPS and deployment binding establish the producer boundary, not the hash alone.

Manual sync and opt-in polling share the same ingestion path. Polling uses explicit subject IDs, a bounded interval below maximum age, persistent checkpoints and database leases; workers restart without losing checkpoints and do not synchronize educational sources. Runs record actor, reason and sanitized error codes. Source configuration is immutable after registration apart from enable/disable; create a replacement source and a reviewed policy version when changing a contract. Snapshot/receipt archival and discovery of all upstream subjects are not implemented.

Routes under `api/governance/tenants/{tenantId}/pip` expose `GET/POST sources`, `POST sources/{id}/enabled`, `POST sources/{id}/sync`, `GET sources/{id}/status?subject=...` and `GET sources/{id}/runs`. Authenticated cookie sessions with tenant `tenancy.manage` or `platform.admin` are required; writes additionally require antiforgery and an audit reason. Status and decision responses contain evidence metadata, not unrelated attributes. The marked template organization blocks operational writes; its dedicated administrator-only `POST example` creates or refreshes isolated synthetic evidence and draft-only scenarios, without publishing or granting access.

The evaluator reads a selected source through `IPolicyInformationPoint`; it never performs network I/O during a decision. Missing, disabled, malformed, expired or missing referenced attributes return Indeterminate for an applicable rule, including in the presence of a manual grant. A complete unequal value is a no-match, so an attribute Permit is not an unconditional restriction on other grants. An explicit matching Deny takes precedence. External resource/environment adapters, LDAP/SQL drivers, webhook/CSV ingestion and general XACML XML processing are outside this implemented subject-attribute transport; existing local membership/chart/calendar providers remain authoritative for their own data.
## Current activation boundary

`/PolicyWorkbench` is a separate, administrator-protected guided laboratory connected to the actual `Fanasa.ContractPolicy` native engine and a persistent SQLite database. Its concrete example retains a proposed organization-unit name with a decision and atomically commits the name, save count, local usage, operation and idempotency receipt only after native PEP checks. Readiness and read-only configuration validation guide independent publication and explicit binding; all persisted source definitions and effect relationships are validated. These server-owned sample identities, memberships, grants and unit records remain separate from operational Fabric access and commercial counters. See the [workbench server contract](/E:/SJVS/Projects/Neo/docs/fanasa-platform-control-center/workstreams/contract-policy/implementation/ui/server/README.md).

`/Governance` supports policy authoring, independent publication/retirement, decision simulation, lifecycle request submission, independent review and cancellation. These operations persist in the same Fabric database and outbox. The current host does **not** connect policy decisions to live authentication claims, register the scheduled lifecycle worker, or expose lifecycle execution through its API. Approved requests do not change membership; published patterns do not change existing access. The UI states this boundary explicitly.

`IdentityLifecycle.Apply` and `ApplyDue` are implemented for isolated behavioral testing, but have no enabled production execution path. Live integration was rejected by automatic approval review because it changes effective access and memberships. Activation must be separately authorized after review of the tested behavior. Existing tenant grant enforcement remains authoritative meanwhile.

## Windows and patterns

| Context | Authoritative source | Implemented conditions |
|---|---|---|
| Subject | Active tenant membership and stored chart | Active role appointment, optional unit membership |
| Resource | Authorized tenant route | Exact tenant ownership; no inheritance to another tenant |
| Action | Supported Fabric domain permission catalog | Exact permission such as `organization.read` or `billing.read` |
| Environment | Server UTC clock for actual authorization; explicit time only for labeled simulation | Validity interval and optional recurring daily UTC hour window |

Four templates are available: `role` requires a real organizational role; `member-window` requires active membership and a time condition; `time-guard` imposes a calendar restriction; `attribute` requires a declared attribute from a selected PIP source. A unit condition restricts **the member's appointment**, not arbitrary resource rows. Resource-object authorization, IP/device/MFA conditions and arbitrary product permissions are not implemented by this profile. External PIP snapshots are supported by the bounded adapter described above; live policy activation still requires an explicit source binding and deployment configuration.

Validity and appointment intervals are `[start,end)`. Daily windows allow integer start hours 0–23 and end hours 0–24. `22 → 6` spans midnight; `0 → 24` covers the whole day. Both daily endpoints are required. Local browser datetime input is converted to an absolute timestamp; daily hours explicitly use UTC, with no inferred local time zone or DST rules.

Each pattern has an immutable ID, effect, target permission, author, independent publisher, status and reason. New versions are created as new drafts. Published pattern content cannot be edited. Publishing requires another platform administrator; retirement is explicit. Each mutation checks the policy-state revision and saves a historical snapshot. A tenant retains at most 200 pattern versions in this first profile; automated archival is not implemented.

The only supported combining algorithm is `deny-overrides`. Existing direct grants are considered a base Permit when valid. Explicit matching Deny wins. A missing reference on a potential Deny produces Indeterminate rather than falling through to a Permit. A missing permit-side reference does not suppress another valid Permit, consistent with that algorithm. No active membership always denies. No matching grant or pattern returns NotApplicable. Only Permit may authorize an operation; all other outcomes fail closed.

Simulation returns applicable policy IDs, reason, obligations and advice without changing access. The supported Permit obligation is `audit`; the service records an outbox event before an authorization with that obligation succeeds. No unsupported obligation can be configured. A missing policy reference returns review advice. This audit mechanism uses local transactions, not an immutable external audit archive.

## Lifecycle workflow

Commands are `join`, `move`, and `leave` with a stable request ID, expected chart revision, subject, effective timestamp and reason. Join includes a destination position and display name. Move includes the previous appointment ID and destination position. Leave has no new position or end date.

States are `pending → approved/rejected`, `pending/approved → cancelled`, and, in the tested executor, `approved → executed/failed`. Requester and target cannot approve their own change. Review requires current `tenancy.manage` and `organization.write`; submission requires both read permissions. Approval does not immediately execute a change.

Before activation, lifecycle authorization rechecks existing persisted direct domain grants and active membership; simulated policy permits cannot elevate workflow-review authority. Integrating role-derived decisions with that boundary also requires the pending explicit activation authorization.

The executor rechecks operator and original approver authority, due time, active membership, subscription, chart revision and destination capacity. Membership, appointment, grant revocation, request status, history and outbox commit together. Retries are actor/payload-bound at submission and idempotent after execution. Stale versions do not silently apply to a changed chart. A capacity or funding failure rolls back all partial changes. Rejected, cancelled and future requests cannot execute.

Join creates or reactivates the membership only at execution, then adds the time-bound appointment. Move atomically ends the selected previous appointment and starts a new one, expires all direct Fabric domain grants for that member in the tenant, and leaves new role access to published policy evaluation when that integration is enabled. Other concurrent appointments remain explicit; there is no hidden removal of unrelated responsibilities. Leave uses existing offboarding to revoke domain grants, end current appointments and cancel future appointments. Developer grants are denied by the authoritative active-membership check. The central Keycloak account is not deleted and sessions of other products are not forcibly ended.

Expired SaaS subscriptions do not block safe offboarding. The governance view hides subscribed chart content on expiration but can still collect a leave request. Join/move continue to require valid organization subscription. Offboarding cannot be blocked by an already-exceeded seat limit.

The unregistered worker is designed to inspect up to 100 due approved requests every 30 seconds. Authority/state conflicts become a durable failure requiring a new reviewed request; infrastructure failures keep an approved request recoverable. Execution can occur after the requested time because of approval, downtime or processing delay. Actual leave cutoff is execution time; no retroactive denial is claimed. Activation and operational monitoring of this worker remain pending approval.

## API

All routes are under `/api/governance/tenants/{tenantId}`. Mutations and simulation require authentication and CSRF. Read and request endpoints require both tenant-scoped read permissions. Policy authoring/publication requires `platform.admin`. A non-admin can simulate only their own subject within an authorized tenant.

- `GET /`: patterns, at most 100 latest lifecycle requests, members and authorized chart data.
- `POST /patterns`: draft `PatternCommand` with expected revision.
- `POST /patterns/{id}`: publish/retire with expected revision and reason.
- `POST /simulate`: subject, supported permission, optional absolute simulation time.
- `POST /requests`: submit an immutable lifecycle command.
- `POST /requests/{id}`: approve/reject/cancel with expected request version and reason. `apply` is deliberately unavailable before activation.

Permission failure returns 403, missing resource 404, invalid input 400, stale version or transition conflict 409. Full workflow histories remain persisted for recovery; the current UI exposes recent requests and audit summaries, not a full lifecycle archive export.
