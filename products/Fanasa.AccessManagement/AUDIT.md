# Tenant audit view

`/Audit` reads committed Fabric outbox events from SQLite. Each tenant must be active and the signed-in user must hold the corresponding scoped read permission: `organization.read` for chart events, `billing.read` for accounting/payment events, or `tenancy.read` for membership, subscription and grant events. Explicit unauthorized tenant selection is forbidden. Unsupported or unauthorized event filters and invalid cursors return HTTP 400.

The view returns 30 summaries per page with a descending event-ID cursor. New inserts do not shift older pages. Queries filter ownership and event kinds in SQL before applying the bounded limit; they do not load the complete outbox into memory. Known nested tenancy result payloads are supported; global pricing-plan events have no tenant ownership and are omitted.

Summaries contain event ID, type, actor when recorded, and UTC time. Raw payloads, payment references, amounts and free-text reasons are not exposed. Existing accounting events without an actor show a system-recorded label. This is an operational history, not an immutable external audit archive. Retention/export, external outbox dispatch and platform-global audit reports remain separate work.
