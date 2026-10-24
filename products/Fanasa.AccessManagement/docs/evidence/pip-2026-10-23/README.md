# PIP validation — 2026-10-23

The current local implementation persists source contracts, typed mappings, subject snapshots, version receipts, sync history and per-subject scheduling checkpoints. The operational source UI is in Organization; attribute policies and simulation remain in Governance. Educational data is isolated in the explicitly marked template tenant and its policy remains a draft.

## Automated verification

| Suite | Successful checks |
| --- | ---: |
| Organization.Tests, including PIP contracts, transport, policies, API, scheduling and examples | 585 |
| Organization.Temporal.Tests | 141 |
| Organization.Calendar.Tests | 62 |
| Total .NET checks | 788 |
| JavaScript governance, calendars and PIP | 31 |

All three .NET builds completed with zero warnings and errors. The [validation manifest](validation-manifest.json) records commands, source SHA-256 values and matching Web DLL hashes across the application and all three test executables. Build and test logs are alongside it.

The exact UI gate completed with zero failures, cancellations or skipped tests:

```text
node --test --test-concurrency=1 --test-reporter=tap products/Fanasa.AccessManagement/tests/governance-ui.test.cjs products/Fanasa.AccessManagement/tests/organization-calendar-ui.test.cjs products/Fanasa.AccessManagement/tests/pip-ui.test.cjs products/Fanasa.AccessManagement/tests/pip-governance-ui.test.cjs
```

Both PIP UI suites are now included in `deploy/fanasa-organization-hr/ci.py`, with a 31-test minimum in the current CI runner. The Python CI source also passed syntax validation. A remote GitLab pipeline was not triggered as part of this verification.

Important regressions cover tenant correlation, typed values, full UTC datetime parsing, canonical hashing, tampered persisted evidence, immutable versions and replay, source generations, disable-during-fetch, subject-specific ETags, 304 without extending expiry, bounded/strict HTTP responses, exact deployment bindings, network address policy, missing secrets, missing attributes in Deny rules, direct-grant bypass prevention for invalid evidence, durable restart, scheduling leases and cancellation.

## Browser verification

The final Web DLL runs in the isolated test preview at `http://127.0.0.1:5199`. This fixture uses synthetic identities and a temporary Fabric database; its authentication handler is compiled only into the test executable.

- The Organization sample refresh performed an actual authenticated/antiforgery-protected POST and displayed all three server-computed scenarios correctly.
- Subject freshness and persisted run history were read back from the API.
- Governance independently returned Permit with fresh evidence and `enforced: false`; after the actual five-minute lifetime elapsed, it returned Indeterminate with stale evidence.
- Desktop width 1440 and mobile width 390 were checked: no horizontal overflow and no browser console errors.
- The actual source form saved a disabled source in the disposable demo tenant. Enabling it without a deployment binding returned the expected localized rejection and left ingestion disabled; no external connection was made.

Screenshots are retained in the local [browser evidence folder](C:/Users/JavadSeyediPC/.codex/visualizations/2026/10/10/01a1251f-7d8f-71f2-9956-1da693f49969/pip-ui-evidence), including Organization source management and Governance Permit/expired-evidence views. The [browser QA report](C:/Users/JavadSeyediPC/.codex/visualizations/2026/10/10/01a1251f-7d8f-71f2-9956-1da693f49969/pip-ui-evidence/browser-qa.json) records the final URLs and observations.

## Delivery boundary

This is local source, build, database and browser evidence. The change has not been published to the VPS and no production external HR source or credential has been configured. External transport tests use controlled HTTP handlers. The educational source never makes an external request. Live enforcement retains the existing explicit tenant opt-in and supported organization read/write routes; no existing policy is automatically published or tenant grant changed.

See the [Persian source setup guide](../../PIP-EXTERNAL-SOURCES.fa.md) for the exact external envelope, mappings, deployment binding, secret reference, freshness behavior and supported scope.
