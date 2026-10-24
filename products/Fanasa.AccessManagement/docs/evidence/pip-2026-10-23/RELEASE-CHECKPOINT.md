# PIP release checkpoint — 2026-10-23

Follow-up: [release gate audit](RELEASE-GATE-AUDIT.md) independently confirms the corrected 31-test UI acceptance floor with 28 passing packaging/archive tests and clarifies that the original hardening suite was already invoked through the external-source suite. Historical source hashes below describe the earlier checkpoint; the central owner continues validating its combined release snapshot.

The PIP implementation is ready for the coordinated Neo release. The central **Fanasa Platform Control Center** task is coordinating Git and VPS publication. This continuation has not committed, pushed, checked out a branch, activated a release, changed server configuration, or modified live data. Keep a single release writer; use the existing trusted CI/CD flow.

## Validated scope

- [PIP validation manifest](validation-manifest.json): 788 .NET checks (585 Organization/PIP, 141 Temporal, 62 Calendar), zero build warnings/errors. On this continuation all **185 recorded source hashes still match** the shared workspace.
- [PIP validation report](README.md): 31 JavaScript tests and actual browser/API/database checks with isolated synthetic state; desktop and mobile evidence linked there.
- [Independent integrated Release validation](../../../../../docs/fanasa-platform-control-center/workstreams/portal-post-pip-integration-2026-10-23.md): 2,198 .NET checks and 123 JavaScript tests across the central host and Portal. That report identifies its own manifest, source inventory, matching runtime DLLs and startup evidence. Its build outputs were deliberately cleaned up; rebuild from the reviewed source for publication.

## PIP file ownership

New Web implementation files (under `src/Fanasa.AccessManagement.Web/`):

- `PipInformationPoint.cs`, `PipContracts.cs`, `PipTransport.cs`, `PipTransportOptions.cs`.
- `PipStoreScheduling.cs`, `PipSyncWorker.cs`, `PipApi.cs`, `PipEducationalExample.cs`.

PIP integration edits are in `AccessPolicies.cs`, `GovernanceApi.cs`, `FabricServices.cs`, `appsettings.json`, Organization/Governance page models and Razor pages, and their JS/CSS. These shared files also contain other work: preserve whole-file concurrent changes and review the combined release. The competing unused PIP implementations and their project exclusions were removed.

Tests: `Organization.Tests/PipApiTests.cs`, `PipDatetimeTests.cs`, `PipEducationalTests.cs`, `PipExternalSourceTests.cs`, `PipHardeningTests.cs`, `PipPolicyTests.cs`, `PipSchedulingTests.cs`, plus registration in `Program.cs` and test-only preview changes. JavaScript suites: `pip-ui.test.cjs`, `pip-governance-ui.test.cjs`; both are included in `deploy/fanasa-organization-hr/ci.py`'s UI gate. The deployment CI source and `docs/GOVERNANCE-XACML.md` also include concurrent non-PIP work.

Setup contract: [PIP-EXTERNAL-SOURCES.fa.md](../../PIP-EXTERNAL-SOURCES.fa.md). No additional PIP source-code work is pending at this checkpoint.

## Publication requirements and remaining operational work

1. Preserve the existing Fabric/Access/Workbench databases, Data Protection keys, SSO, service identities and configuration. Keep a rollback release and use the existing release verifier and readiness checks.
2. Keep `Pip:Transport:Bindings` empty until an actual approved tenant/HTTPS endpoint/secret-reference binding exists. No real external PIP source or credential was supplied or activated in this task. Synthetic tests do not attest connectivity to a real HR system.
3. After publication, verify authenticated Organization PIP management, anonymous/unauthorized rejection, source status/history, and Governance simulation. Refresh the educational example only in the explicitly marked **سازمان الگو** tenant via the protected API; policies must remain drafts and `Enforce=false`.
4. Do not activate policies, expand live enforcement, renew the expired HR development grant, or change other tenants as part of publishing PIP. Existing explicit tenant opt-in and supported organization read/write enforcement boundaries remain.
5. The 503 diagnosed for live Portal readers belongs to central runtime/client configuration. PIP's local build/test success is not evidence that the live reader issue is fixed.

The bundled VPS helper was attempted for read-only inspection but rejected the local reference format before making a connection (`Invalid host field`). A safe structural check found eight nonempty lines and the IP address on line two, while the helper expects the first three lines to be host/user/password. No credential was printed, copied, or altered; there was no authentication attempt and no live inspection result from this continuation. The coordinated release owner should use the current connection reference with a format-aware reader.
