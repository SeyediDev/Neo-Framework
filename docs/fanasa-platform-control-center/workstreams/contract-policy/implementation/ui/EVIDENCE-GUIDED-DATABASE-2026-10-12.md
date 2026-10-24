# Guided configuration and database delivery

Verified and published on 2026-10-12. Delivery: `xacml-guided-db-20261012-04`, development VPS `62.60.199.135`.

The Persian UI explains a concrete goal: a valid member with `ApiInvoker` saves a unit owned by the same consumer tenant. It explains the data sources, native decision, mandatory enforcement, real saved name and transaction evidence. The server example uses 2 requests/second, a burst of 4 and a monthly UTC quota of 1000 (50 saves/day × 20 days, explicitly an example assumption). Loading the example is explicit. Field validation, independent sample reviewer labels, immutable publication and explicit contract binding are guided separately.

The actual native `Fanasa.ContractPolicy` engine is used. SQLite persists the accepted definitions, trusted sample sources, policies, drafts, records, decisions, operations, receipts, local usage and audit. A retained proposed unit name is saved with the effect, save count, admission and receipt in one transaction. A replay does not consume again. A failed effect changes neither the unit nor usage. A changed trusted source invalidates a retained Permit immediately; legacy decisions without a source fingerprint require fresh evaluation.

Every read checks SQLite structure, foreign keys, normalized columns and the complete state checksum. Semantic proof includes native schemas/replay, references, binding, ownership and effect/receipt/usage coherence; only successful exact full-state SHA proofs can be reused. Runtime readiness and deadlines are never cached. The SQL transaction checks both mutation version and previously read content and rechecks the native PEP deadline immediately before commit. Invalid storage fails closed and cannot be silently reseeded or bypassed with reset.

Validation completed:

- Native build: 0 warnings, 0 errors. The complete 15-group suite passed; after the final source/freshness guards, all 8 affected groups passed again. Coverage totals 18 unique groups, including 3 new maintenance/revocation/legacy-source cases and 4 typed probe assertions.
- Native evidence: `.build/workbench-runtime-integrity-final-20261012.tap` (15/15) and `.build/workbench-runtime-freshness-final-20261012.tap` (8/8). Tests cover actual content, failure rollback, replay, restart, same/two-process concurrency, migration, expired/revoked sources, running SQL corruption, write-lock expiry and immediate source-change refusal.
- Authenticated proxy boundary: 23/23 checks passed, including protected readiness, validate CSRF, fixed upstream, bounded requests, header isolation and unavailable-engine handling.
- Final browser verification: 37/37 passed in Chrome 155 with no console, external-request or failed-request errors. Fresh desktop and mobile form screenshots were inspected; ISO timestamps are isolated correctly in RTL text. Report: `evidence/qa-workbench-results.json`.
- Release builds and packaging succeeded. The Web host and workbench carry identical native engine DLLs, all ten schemas and the Linux x64 SQLite native library. Archive SHA-256: `21a0564284c9e3e31d458d437b890e0a74f19917d2f37851fef99264cd8c4f8b`; release-manifest SHA-256: `ef700593bf66e4ce27efa28de4fb059357d978d6119a05c226a2f10ec9052797`.

VPS verification completed:

- Every packaged file passed manifest verification. A disposable copy of the current VPS data passed valid import, readiness, configuration validation, real name change, failed-effect preservation, replay, four native decision outcomes and restart persistence. Original VPS source files remained unchanged by this test.
- Existing data was then migrated once to the active SQLite store without reset. At verification: revision 2, 1 policy, 2 unit records, 4 trusted source categories, 0 operations, 0 admitted usage and 3 audit entries. The original JSON was retained.
- The prior release `xacml-workbench-20261012-03` and `pre-migration-state.tar.gz` were retained in the new release directory. The existing service identities, state paths, SSO configuration and loopback ports remain in use.
- Both services are active. Engine 4187 and Web 5191 listen on loopback. HTTPS health is healthy; anonymous APIs return 401, protected shells redirect to sign-in, and the private HTML shell is not a static public file.
- Restarting the actual active engine retained identical exported database state: SHA-256 `85bead3b564a512640438cb881f0cb0b9c027a042e94e7ec3882b8594cf50ab2`. Evidence: `.build/vps-guided-verification-20261012.log`.
- The existing authenticated browser session loaded the published guide with healthy SQLite and all 15 readiness checks. Loading the example and validating it through the deployed SSO/CSRF proxy returned valid settings without saving a draft. Screenshot: `evidence/workbench-vps-guided-validation.jpg`.

Open [the published workbench](https://platform.fanasa.net.local/PolicyWorkbench#workbench). The native typed policy profile is limited; arbitrary XACML XML is not supported. The persistent sample identities, memberships, grants, records and local counters belong to this isolated laboratory. Operational grants, membership lifecycle and commercial admission activation are outside this delivery.
