# Post-PIP local integration evidence — 2026-10-23

Use [integration-final-verification.json](integration-final-verification.json) for the final result: 2,198 .NET checks in 13 suites, 123 JavaScript tests, identical runtime assembly copies, and unchanged hashes for 278 monitored inputs.

[startup-verification-retry.json](startup-verification-retry.json) records the actual Web process smoke with synthetic settings and isolated state: health200 and anonymous reader401. This does not attest live SSO, authorized readers, Gateway, external PIP, HR, billing or deployment.

verification.json and the original Discovery/startup logs retain the initial fixture-layout and timeout failures. The final manifest selects the successful replacement runs once; it parses 342 transport assertions from 57 groups.

The validation scripts record the exact commands. NuGet audit was disabled only for this local run. Final solution validation preserved Release on external ProjectReferences using ShouldUnsetParentConfigurationAndPlatform=false; solution/CI sources were not changed.

The eight adapter fixtures were freshly exported by the successful C# harness. cleanup.json records deletion of only this run's bulky build outputs and synthetic state. Other chats' files and recordings were not removed.

See [the Persian handoff](../../portal-post-pip-integration-2026-10-23.md) and checksums.json for durable evidence.
