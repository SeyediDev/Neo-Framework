# HR3 r8 — consumer input validation source candidate

This source candidate binds the two consumer read-controller guards committed in `2fcb18341eb6fff5f1223da3ae88b72992cd104c`, based on `21d5aea`. The all-zero consumer UUID is rejected before projection with HTTP 400/no-store when the feature is configured. Bearer authentication and disabled/incomplete configuration precedence are preserved. This is a source candidate, not a deployment or activation.

The immediate predecessor is frozen r7. No successful r7 CI, actual-host, Linux or runtime receipt is inherited. Package C#, projects, props, compatibility Program/Store and targets remain byte-identical to r7; Program and PCC source pins are unchanged. Immutable profile/PDP dependency pins and disabled defaults are preserved.

Review REVIEW-DELTA.fa.md, verify-package.py and its filesystem regression tests. The closed inventory contains exactly the same 148 host paths, with only BillingLifecycleApi.cs and ConsumerUsageReportApi.cs changed. Existing standard-host HTTP red/green evidence (111 consumer usage checks and65 billing API checks) is external and does not attest the HR3 opt-in build.

Before actual-host acceptance, run test-host.ps1/test-host.sh into a fresh r8 artifact directory, then independently verify the complete13-suite/908-assertion baseline, exact manifest hash and unchanged package/dependency/host bytes. Keep receipts, logs and generated artifacts outside this sealed directory. Financial behavior against the opt-in assembly and trusted CI/source selection remain separate release requirements.

Creating or verifying r8 does not select it in CI, update the source-publication form's21d5aea target, install an image, or change endpoints, clients, grants, routes, rates, credit, payment or deployment. Skill/MCP contracts are unaffected by this package-path successor.
