# Preparation verification — 2026-10-25

State: **proposal only; no runtime changes or live human acceptance**.
Historical proposal baseline: `c4211dbbfb1fedf46dfcf996bf1facc2ec35c27b`.
The scope-request implementation is now accepted in `cb4629b`; the c421 checks
below remain historical preparation evidence and are not live SSO acceptance.

- Five proposal tests pass, including mutation cases for assignment, audience,
  issuer, mapper, enabled state and fabricated organization/product bindings.
- Offline source review passes its seven baseline checks in both the main
  checkout and the `fanasa-billing-integration-20261024` coordinator worktree.
  The checked files have identical hashes; `review.py` prints their SHA-256.
  Its reported gaps remain open and do not count as operational readiness.
- The proposed scope-request patch was superseded by the implemented
  `ConsumerBillingOidc.ConfigureScopes` and removed during integration.
  Six focused real-handler authorization-request cases pass (36 assertions);
  the Portal Release build has zero warnings and errors. Metadata is synthetic.
- Portal `BillingIntegration.Tests` passes 67 checks using signed synthetic
  credentials and accounting fixtures in memory. This is not live OIDC.
- PCC `ConsumerUsage.Api.Tests` passes 111 checks against isolated local fixtures
  and a local HTTP test host, including forged/service/cross-consumer credentials,
  expired grants, inactive membership, no-store responses and read-only storage.
  Initial output-directory access failures were resolved by building serially
  with isolated temporary artifacts; the build has zero warnings and errors.

PCC reproduction (PowerShell; existing package cache required):

```powershell
$reviewArtifacts = Join-Path ([System.IO.Path]::GetTempPath()) 'fanasa-sso-financial-review-20261025'
dotnet restore products/Fanasa.AccessManagement/tests/ConsumerUsage.Api.Tests/ConsumerUsage.Api.Tests.csproj --artifacts-path $reviewArtifacts --configfile products/Fanasa.AccessManagement/tests/BillingLifecycle.Tests/offline-nuget.config -p:NuGetAudit=false -m:1 -nr:false
dotnet build products/Fanasa.AccessManagement/tests/ConsumerUsage.Api.Tests/ConsumerUsage.Api.Tests.csproj --artifacts-path $reviewArtifacts --no-restore -m:1 -nr:false
dotnet (Join-Path $reviewArtifacts 'bin/ConsumerUsage.Api.Tests/debug/ConsumerUsage.Api.Tests.dll')
```

`NuGetAudit=false` applies only to this offline restore invocation; no source
package policy was changed. These tests do not acquire real human credentials.

Proposed scope `consumer.financial.read` and product key `fanasa-unified-portal`
match the coordinator's activation draft, not an approved registration. Runtime
flags remain disabled. No Keycloak client, scope or assignment, central product,
tenant, grant, trust or deployment was changed by this preparation.

Activation still requires an explicitly selected consumer organization, approved
product owner and official center, durable central product/client registration,
runtime verification of the administrative client-registration route, effective
installed Keycloak mappers/flows, deployment of the Portal fix, Gateway/TLS binding and
live human positive/negative acceptance. Preserve the existing five-minute vault
cap and current issuer policy; longer central SSO does not extend this vault.

The original preparation changed no shared Portal/PCC source. The integrated
Portal fix is recorded separately above; no Skill/MCP contract changed.
