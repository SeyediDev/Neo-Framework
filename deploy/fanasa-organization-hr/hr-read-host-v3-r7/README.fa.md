# HR3 r7 — financial host source candidate

This source candidate binds the locally integrated PAYG/billing host. It inherits no successful CI, test or runtime receipt from r6. The package C#, projects, props, compatibility Program/Store and targets remain byte-identical to frozen r6. The immutable profile/PDP dependencies retain their original pins. Runtime defaults stay disabled.

Review REVIEW-DELTA.fa.md, verify-package.py and its filesystem tests before running test-host.ps1/test-host.sh with a fresh r7 artifact directory. Existing local standard-host financial test provenance is recorded outside this package; it does not attest HR3 opt-in compilation. Run the complete actual-host 13-suite/908-assertion baseline on this new seal. Keep results outside the sealed directory and bind every receipt to the manifest hash. Re-test financial behavior against the opt-in host before eventual release if the build differs.

Candidate creation and local verification do not select r7 in CI or activate endpoints, clients, grants, secrets, payment, routes or deployment. Trusted CI and operational acceptance remain separate.
