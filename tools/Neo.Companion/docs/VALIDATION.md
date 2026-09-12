# Validation record — 0.3.0

Validated locally on 2026-09-10, Windows x64, .NET SDK 10.0.401 / runtime 10.0.12.

| Check | Result |
|---|---|
| Complete Companion suite including Doctor | **52 passed, 0 failed, 0 skipped** |
| Direct-registration fixture | Business result preserved, span absent, NEO-TEL001 reported |
| Missing-requester fixture | Actual DI resolution fails on IRequesterUser; NEO-DI001 reported |
| Invalid-endpoint fixture | Actual trace-provider resolution fails on malformed URI; NEO-CONFIG001 reported |
| Corrected fixture | Service resolves, orders.lookup span has Ok status, no Doctor findings |
| Real stdio MCP on broken and corrected fixtures | All five tools discovered/called; expected Doctor results |
| neo-doctor skill structure | Passed |
| Framework source fingerprint and maintained doc links | Passed |

Doctor tests also exercise evidence line numbers, qualified attributes, manual wrappers, duplicate type names, comments/string literals, generated outputs, conditional compilation, malformed/oversized files, configurable JSON sections, withheld config values, read-only behavior and the finding budget. No user application project is executed by Doctor itself.

This is syntax-level diagnosis. Aliases, registration control flow, external modules, preprocessor settings and environment overrides are not resolved. Symlink rejection is implemented but was not independently exercised by these local tests. No independent model-based skill evaluation was performed. This record reports local checks; the GitHub workflow runs Release build/test/publish and the broken/corrected MCP cases on Windows and Linux.

The local restore again used an official-package local feed with NuGetAudit=false due to the environment's .NET TLS limitation. CI retains NuGet audit. SQLitePCLRaw.bundle_e_sqlite3 remains pinned to 2.1.13 from the previous CI repair. Full legacy Neo test suites were not run.

## Previous telemetry validation (0.2.0)


Validated on 2026-09-10, Windows x64, .NET SDK 10.0.401 / runtime 10.0.12.
Base commit: `e34c0ad5336c71e0cf3be4bccb7f425aecccea9d`. Repaired source is identified by the SHA-256 fingerprint in knowledge/manifest.json; the base commit alone does not include these changes.

| Check | Result |
|---|---|
| Four runtime regressions against the original telemetry | All four failed, reproducing defects |
| Companion suite against the repaired framework | **35 passed, 0 failed, 0 skipped** |
| Framework dependencies and Companion projects compiled by test build | Passed |
| Actual console demo: manual / attribute | Both returned available=True, catalog.lookup status=Ok, balanced inflight events and nonzero duration |
| Actual MCP stdio discovery and all four tools | Passed; protocol 2025-06-18 |
| Manual and attribute recipes / wrong fingerprint | Recipes retrieved; unmatched fingerprint rejected |
| Source snapshot consistency | Passed |
| Both skill structure validators | Passed |

## Behavioral coverage

Telemetry tests exercise active scoped registration, interface and implementation attributes, Task/Task<T>, ValueTask/ValueTask<T>, synchronous returns, void, generic methods, parameterless methods, null requests and successful null responses. They check cancellation in the third parameter, unchanged business arguments, original exception identity, calls returning before asynchronous work completes, duration measurement, and metadata isolation between overlapping operations. Compatibility factory and DispatchProxy entry points are exercised too.

The exporter integration test resolves actual OpenTelemetry providers through AddNeoOpenTelementry, verifies bound options and service.version, and proves that custom ActivitySource and Meter names are subscribed. The console demo uses the real framework behaviour and proxy. Its measured durations are not benchmarks.

ProductCatalog tests cover validation, HTTP 201/400/404, persistence after host restart, and no insertion for invalid input. MCP tests cover bounded project inspection, XML DTD rejection, source lookup, fingerprints and examples. The protocol smoke test launches the actual server process; no model endpoint is involved.

## Environment and limits

Validation used a source archive in a scratch directory. SourceLink warnings reflect its missing .git metadata. The local .NET HTTP stack could not authenticate TLS to NuGet here; official packages were downloaded with Python certificate validation and restored from a local feed plus the existing cache. Versions were preserved. NuGetAudit=false was used for validation restore, so this is not a vulnerability assessment. The delivered configuration uses the official NuGet source normally.

The local build uses Debug. The supplied GitHub workflow specifies Release on Windows and Linux. These results describe local validation before GitHub execution; see the Actions run for CI results. Neo's full existing test suite was not run; the new framework telemetry regressions were linked into and executed by the Companion suite. Skills were structurally validated and their example workflows executed, without independent model-based evaluation. Global MCP client configuration was not changed.

Async-stream enumeration is not instrumented. Cancellation currently records Error while preserving the cancellation exception/token. Request/response logging and user/correlation metric tags remain part of existing behavior; see TELEMETRY.fa.md for application considerations.

## Reproduce

Use the build, test, demo, publish and smoke commands in the Companion README from the repository root. Run `python tools/Neo.Companion/scripts/sync_knowledge.py --check` before packaging.
