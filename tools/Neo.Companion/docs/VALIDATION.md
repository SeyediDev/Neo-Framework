# CRUD resources, concurrency and feature generation — 0.6.0, 2026-09-26

- Final local Companion suite at `c1c7b89`: **119 passed, 0 failed, 6 skipped** (125 total). Skips are four SQL Server cases, one Redis integration case and the Windows symlink privilege case. These are explicit environment limits; they are not counted as passing local integration tests.
- [SQL Server CRUD workflow](https://github.com/SeyediDev/Neo-Framework/actions/runs/36209558228) passed at `c1c7b89`: optimistic and pessimistic two-writer races, exclusive lock timeout/cancellation/rollback release, and bounded scope pre-read behind another writer's exclusive lock. Previous DbContext command timeout is restored after failure. SQLite tests cover page boundaries, allow-listed queries, version rejection, scope and rollback across entity, translation and Outbox.
- Eight HTTP/runtime Doctor cases passed locally. They cover explicit authorization and disabled operations, invalid input, followable create Location, stale update/delete, rollback after participant saves, missing policy/DI and unsupported pessimistic provider. Doctor metadata/connectivity checks leave SQLite schema empty; the executable `--doctor` mode also created no database.
- [Companion Windows/Linux workflow](https://github.com/SeyediDev/Neo-Framework/actions/runs/36209558213) passed at `c1c7b89`, including generated project compilation and real HTTP execution, source/skill packaging, MCP stdio, CLI packing/install and the installed `neo --help` command. The generated feature smoke also passed locally from the published CLI, including authorization, Location round trip, update, stale-version conflict and entity/translation/Outbox rollback after restart. Linux covers symlink refusal; the local Windows account cannot create symlinks.
- Published MCP **0.6.0** passed actual stdio initialization, discovery of five read-only tools, all five example IDs (including `crud-resource-demo`), exact-baseline rejection, both telemetry recipes and healthy static Doctor diagnosis. The final source fingerprint is `sha256:678445c95ddff108020fb6aa2e9188bd354f3592a82535648af51305b4a7c276`.
- The local `neo` tool was packed and installed as **0.6.0**. The ZIP contains MCP, CLI, its local tool package, canonical skills, guides and runnable sample sources. Packaging verifies required entries; the bundled manifest was compared with the current source fingerprint. The neo-feature/neo-doctor structural validators, Python syntax, workflow YAML and source snapshot checks passed.
- Runtime checks exposed and resolved standalone MVC discovery of Neo's optional versioned controllers, sample entry-point ambiguity, inconsistent UTC serialization after SQLite reads and test-harness proxy/connection cleanup issues. CI generation initially assumed Release reference DLLs already existed; it now builds the generated project's references in the requested configuration.
- Limits: pessimistic locking is SQL Server-specific and supports a normal table with a single-column key, not multi-record business invariants. LockWait bounds each SQL command, not the whole operation or connection opening. Existing schemas/writers need Version migration and consistent token handling. Demo authentication and EnsureCreated are teaching defaults; Outbox rows in CrudResourceDemo are staged only, with no worker or ProductChanged handler. Runtime Doctor does not verify physical schema and resolving user constructors can run application code. No production deployment or public NuGet release was performed.
- WorkManagement project NEO records implementation, commits and acceptance evidence in `NEO-CRUD-101` through `NEO-CRUD-104`, under `NEO-CRUD-AUDIT-001`. The checked-in backlog describes acceptance; live status remains in the database.

# DDD and durable event delivery — 0.5.0, 2026-09-26

- Complete local Companion suite: **91 passed, 0 failed, 1 skipped**. The skipped case requires `NEO_TEST_REDIS`; real Redis coordination passed in the [RabbitMQ/Redis workflow](https://github.com/SeyediDev/Neo-Framework/actions/runs/36186260450).
- Added relational regressions: seven domain-event cases, four queue cases, five local coordination cases and seven EF delivery cases. They cover cascade/cancellation/failure, Hangfire job serialization, atomic ownership, stale leases, rollback, terminal attempts and OutboxId propagation. Two catalog cases verify durable source/configuration/dependencies and baseline rejection.
- [Real SQL Server/RabbitMQ/Hangfire workflow](https://github.com/SeyediDev/Neo-Framework/actions/runs/36186260529) passed at `f92f7bf`: outer rollback, delayed committed publication after restart, two Saga replicas, durable manual review/compensation, Hangfire execution status, saved-effect rollback and repeated dispatch. Payment and inventory are simulated database effects; no real provider or automatic deadline scheduler was tested.
- First Hangfire smoke exposed missing telemetry DI in the independent sample; `f92f7bf` registers the required telemetry/requester services and resolves the worker at startup. The subsequent real runtime workflow passed. Build success alone did not establish this result.
- [Companion Windows/Linux](https://github.com/SeyediDev/Neo-Framework/actions/runs/36185218157) and [CRUD regressions](https://github.com/SeyediDev/Neo-Framework/actions/runs/36185218274) passed after the framework changes. Final companion retrieval additions were also tested locally as part of the 91 passing cases above; use the branch Actions history for subsequent runs.
- Published MCP **0.5.0** passed the real stdio smoke: five read-only tools; ProductCatalog, initial messaging, durable Saga (including sibling dependency sources), Hangfire Outbox, both telemetry modes, wrong-baseline rejection and healthy Doctor fixture. neo-feature structure, source fingerprint, Python and Compose/workflow YAML validation passed.
- Local machine has no Docker. SQL Server/RabbitMQ/Hangfire container runtime evidence comes from GitHub, while EF unit-of-work/lease regressions use real local SQLite. The lease test uses separate contexts and deterministic stale-owner interleavings; it is not a production-scale concurrency benchmark. Local restore disables NuGet audit; CI retains it. General NuGet release packaging and all legacy solution tests are outside this validation; the previously observed NU5104 release-packaging issue is not repaired by this work.
- WorkManagement project NEO stores live task ownership, commits and test evidence under `NEO-EVT-101` through `NEO-EVT-106`. The checked-in backlog is an acceptance specification, not a duplicate live board.

# Messaging and Saga validation — 0.4.0, 2026-09-24

- Companion suite: **66 passed, 0 failed, 0 skipped**, including real MassTransit 8.4.1 in-memory pipelines for fan-out, transient retry, nonretryable faults and process-local duplicate suppression.
- Saga scenarios: successful payment, declined payment with compensation, transient compensation failure, exhausted retries followed by operator recovery, unknown payment requiring reconciliation, and repeated participant operations preserving one effect.
- RabbitMQ configuration validation and endpoint naming are tested without connecting. The sample builds against the framework's pinned dependencies.
- Companion tests now use xUnit v3 to match the linked framework telemetry tests. Restored a missing invocation in the existing asynchronous-return test; previously it waited on a completion source nobody completed.
- Source snapshot and neo-feature skill structural validation passed. MCP example retrieval includes messaging configuration, Saga source and both Persian guides.
- Published MCP 0.4.0 passed a real stdio smoke test: all five tools, ProductCatalog and messaging/Saga retrieval, telemetry recipes, exact-baseline rejection and the healthy Doctor fixture. YAML and Python syntax checks passed.
- Local Docker is unavailable. The separate `Neo RabbitMQ sample` workflow starts Compose and verifies the real broker, error queue, Saga and compensation. In-memory test results alone do not establish RabbitMQ behavior; consult the workflow for the pushed commit.
- The sample deliberately has no durable Saga persistence, transactional inbox/outbox, automatic deadlines or real payment integration. Tests do not establish restart recovery or multi-worker correctness. Local restore uses NuGetAudit=false; CI retains audit.

# CRUD validation — 2026-09-12

- Full Neo.Endpoint Debug build: zero warnings, zero errors after restoring with the repository NuGet.config and clearing obsolete local fallback directories via command-line restore properties.
- GenericCrudControllerTests: **17 passed, 0 failed, 0 skipped, 0 not run**, executed with the xUnit v3 in-process runner. POST/GET integration runs on ASP.NET Core TestServer with controlled handlers; repository tests use controlled dependencies and real update/delete handlers. This does not establish atomic database transactions.
- Verified: followable MVC Location with and without a trailing slash, generated DTO ID, null create result, invalid update IDs, missing entities, translation save/cancellation, untranslated fallback and nonlocalized Guid deletion.
- Test infrastructure: added the centrally versioned xUnit Visual Studio adapter and executable test output. Replaced the placeholder test application and permissive endpoint check with the HTTP regressions. Local VSTest startup hit its 60-second child-process timeout; the same compiled tests passed using the standalone runner. CI checks both failures and a minimum executed-test count.
- neo-feature skill validation and the updated CRUD source snapshot check passed. Companion's published knowledge now includes the CRUD guide and controller/update/delete contracts.
- Local restore/publish used NuGetAudit=false; committed CI commands retain audit. CI results are reported separately after push.

For a slow local runner startup, build the test project and execute the test assembly directly:

```powershell
dotnet tests/Neo.Endpoint.IntegrationTests/bin/Debug/net10.0/Neo.Endpoint.IntegrationTests.dll -class Neo.Endpoint.IntegrationTests.Controller.GenericCrudControllerTests -trx TestResults/Crud/direct.trx
```

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
