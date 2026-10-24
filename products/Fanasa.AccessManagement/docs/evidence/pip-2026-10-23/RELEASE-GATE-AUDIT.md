# PIP release gate follow-up — 2026-10-23

This is a local verification of the current shared release scripts. No application source, Git branch, server configuration, credential, or deployment was changed in this continuation. The central release owner had already applied the fixes before this verification; no duplicate patch was needed.

## UI evidence acceptance

`deploy/fanasa-organization-hr/ci.py` now uses `MIN_ORGANIZATION_UI_TESTS = 31` in both execution and packaging preflight. `verify_bundle.py` independently requires the same minimum. The previous packaging acceptance of 21 checks is no longer present.

The existing regression tests cover rejection of insufficient counts (including 21 and 30), noninteger counts, failures, skips and missing evidence; 31 and 32 successful checks are accepted. These are behavioral acceptance checks using synthetic source/evidence/archive fixtures.

Fresh independent execution:

| Command | Result |
| --- | --- |
| `python -B -m unittest discover -s deploy/fanasa-organization-hr/tests -p test_ci_workspace.py` | 13 tests, OK |
| `python -B -m unittest discover -s deploy/fanasa-organization-hr/tests -p test_verify_bundle.py` | 15 tests, OK |

Total: **28 tests passed**. The initial sandboxed attempt failed while creating temporary fixture directories due to Windows ACLs, before exercising the assertions. The same commands succeeded with scoped execution permission. No network service or deployment was involved.

## Hardening suite execution clarification

`PipExternalSourceTests.Run` already calls `PipHardeningTests.Run(directory, check)`. `Program.cs` calls `PipExternalSourceTests.Run` once. Adding a second direct hardening call temporarily caused `Tenant key already exists` against the same synthetic database; the central owner removed the duplicate and added a comment explaining the existing invocation.

The earlier report that this suite was omitted was incorrect. The SHA-256 values of both `PipExternalSourceTests.cs` and `PipHardeningTests.cs` still match the original [validation manifest](validation-manifest.json). The original [successful Organization log](organization-tests.txt) contains these hardening checks exactly once each:

- Old versions cannot be disguised by newer timestamps.
- Persisted attribute tampering fails closed.
- Production HTTP transport pins connections and disables redirects, proxies, cookies and pooled reuse.
- Unsolicited HTTP 304 responses are rejected.

The historical 585 Organization/PIP checks already included hardening. No additional .NET pass count is claimed by this follow-up. The release owner still needs to finish verification and publication of its exact combined source snapshot.

See [RELEASE-CHECKPOINT.md](RELEASE-CHECKPOINT.md) for PIP file ownership, preservation requirements, external-source configuration limits and the coordinated publication boundary.
