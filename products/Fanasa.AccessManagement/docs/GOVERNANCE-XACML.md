# Identity lifecycle and access policy profile

## Standard and scope

Reference: [OASIS XACML 3.0, approved core specification](https://docs.oasis-open.org/xacml/3.0/xacml-3.0-core-spec-os-en.html).

XACML separates policy administration (PAP), policy decisions (PDP), attribute information (PIP) and enforcement (PEP). Its request context describes subject, resource, action and environment attributes. Decisions are Permit, Deny, NotApplicable or Indeterminate; obligations require enforcement while advice is optional. The deny-overrides algorithm gives explicit denial priority and distinguishes errors by their possible effect.

This implementation is a **typed, limited profile inspired by those concepts**. It is not a conformant general XACML XML processor. It does not accept arbitrary XML, expressions, external attribute URLs, nested PolicySets or unimplemented algorithms. No XACML interoperability or certification is claimed.

## Current activation boundary

`/Governance` supports policy authoring, independent publication/retirement, decision simulation, lifecycle request submission, independent review and cancellation. These operations persist in the same Fabric database and outbox. The current host does **not** connect policy decisions to live authentication claims, register the scheduled lifecycle worker, or expose lifecycle execution through its API. Approved requests do not change membership; published patterns do not change existing access. The UI states this boundary explicitly.

`IdentityLifecycle.Apply` and `ApplyDue` are implemented for isolated behavioral testing, but have no enabled production execution path. Live integration was rejected by automatic approval review because it changes effective access and memberships. Activation must be separately authorized after review of the tested behavior. Existing tenant grant enforcement remains authoritative meanwhile.

## Windows and patterns

| Context | Authoritative source | Implemented conditions |
|---|---|---|
| Subject | Active tenant membership and stored chart | Active role appointment, optional unit membership |
| Resource | Authorized tenant route | Exact tenant ownership; no inheritance to another tenant |
| Action | Supported Fabric domain permission catalog | Exact permission such as `organization.read` or `billing.read` |
| Environment | Server UTC clock for actual authorization; explicit time only for labeled simulation | Validity interval and optional recurring daily UTC hour window |

Two templates are available: `role` requires a real organizational role; `member-window` requires active membership and at least one time condition. A unit condition restricts **the member's appointment**, not arbitrary resource rows. Resource-object authorization, IP/device/MFA conditions, external PIP integrations and arbitrary product permissions are not implemented by this profile.

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
