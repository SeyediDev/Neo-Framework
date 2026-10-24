# PIP educational journey and comparison semantics

The organization template now supports a separate, durable PIP journey through `PipEducationalJourney.Ensure`. Its HTTP entry point is `POST /api/governance/tenants/{tenantId}/pip/journey` with a `{ "reason": "..." }` body. The API requires the administrator and antiforgery checks used by the existing example endpoint.

The journey reuses the v1 source and preserves the existing three PIP examples and 24 organization/calendar examples. It adds 15 synthetic identities and four draft policies for `tenancy.manage`, an action unused by the existing examples. No identity-provider account, direct grant, external HTTP request or published policy is created. `Simulation=true` and `Enforced=false` apply to every result.

The teaching policies are:

| Rule | Comparison | Effect |
| --- | --- | --- |
| Eligible unit | `organization.unit equals engineering`, inside the office calendar | Permit |
| Transferred unit | `organization.unit not-equals engineering` | Deny |
| Inactive employment | `employment.active equals false` | Deny |
| Mandatory office window | Office calendar is closed or a synthetic holiday | Deny |

The explicit Deny rules are necessary. An attribute Permit rule that fails to match does not revoke a separate permit or direct grant. The Deny rules override those permits. Missing, invalid or expired evidence produces `Indeterminate`; `not-equals` never turns unknown data into a successful comparison. Only `Permit` authorizes. Unit, membership and employment are separate concepts: employment values in this journey are invented teaching facts, not an inference from an organization membership record. Production sources must declare the actual fact they can establish, such as `org.member.active` or `org.assignment.active`.

`AttributeOperator` is an optional additive field of `PatternCommand` and `AccessPattern`. Its backward-compatible default is `equals`; only `equals` and `not-equals` are accepted for attribute patterns. Other pattern templates retain `equals`. Both operators use the declared attribute type: numeric spellings such as `+01` and `1`, or equivalent UTC datetime spellings, compare equal. Runtime validation rejects corrupt or unsupported stored operators before considering direct grants.

The scenarios cover active eligibility, external inactive employment with local active membership, transfer to another unit, opening and closing boundaries, a synthetic holiday, weekend closure, missing snapshots, missing required policy attributes, exact freshness expiry, an upstream outage with and without still-valid last-good evidence, a rejected foreign-tenant response, and inactive local membership. The foreign response is actually passed through the store's correlation validation and rejected before any local snapshot can be stored. Outage receipts are explicitly synthetic; no upstream service is contacted or disabled.

Each scenario returns a full `AccessDecision`, its expected decision and reason, the actual evaluation time, and a learning explanation. Calendar evidence includes the calendar revision, timezone and local time. PIP evidence includes source version, generation, observation and expiry, snapshot status, and the safe error code from that subject's synthetic receipt. These fixture times are fixed historical instants in 2026, evaluated against the template's documented calendar coverage. `GeneratedAt` is the response clock, not the time of the fictional event. Scenario source status is labelled `synthetic-*` and does not claim to describe the current aggregate health of the shared source.

Bootstrap is atomic. Existing policies, calendars, v1 source definitions, and grants are preserved. Repeating the journey validates and reads its fixtures without adding outbox events or refreshing historical observations. Changed or incompatible journey markers, policies, membership, snapshots or receipts cause a conflict rather than replacement. Calendar edits remain visible through changed scenario results instead of silently being overwritten. Fixture storage survives database reopen.

Focused coverage is in `PipOperatorTests` and `PipEducationalJourneyTests`, both invoked by the existing `Organization.Tests` executable. Operator tests include direct-grant bypass prevention, typed comparisons, default deserialization and invalid persisted data. Journey tests verify all expected decisions, v1/calendar preservation, explicit simulation, idempotence, persistence, provenance, tenant-response rejection and atomic tamper refusal.
