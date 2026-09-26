# Neo Companion

Developer assistance for Neo Framework, maintained **inside the Neo repository**.

[شروع فارسی](docs/START-HERE.fa.md) · [Telemetry walkthrough](docs/TELEMETRY.fa.md) · [Neo Doctor](docs/DOCTOR.fa.md) · [Generic CRUD](docs/GENERIC-CRUD.fa.md) · [Validation](docs/VALIDATION.md)

## Infrastructure with runnable examples

RabbitMQ/MassTransit: [Persian walkthrough, including Hangfire comparison](docs/MESSAGING.fa.md),
[MessagingDemo](samples/MessagingDemo) and [real-broker smoke test](scripts/smoke_messaging.py).
The same sample includes an [order Saga and compensation walkthrough](docs/SAGA.fa.md):
reservation, payment, bounded compensation retry and manual review for uncertain outcomes.
Saga state and simulated effects are process-local; durable storage and transaction outboxes are not included.
The optional `AddNeoRabbitMq` registration uses the repository's MassTransit 8.4.1 version.
Retrieve this complete example through `neo_get_example` with `example: "messaging-demo"`
and the exact baseline from `neo_search_docs`. The MCP returns source/configuration and instructions;
it does not connect to a broker or publish application messages.

For persistence and restart recovery, use [DurableMessagingDemo](samples/DurableMessagingDemo/README.md)
(`durable-messaging-demo` in MCP): shared SQL transaction, MassTransit bus/consumer Outbox, persistent Saga,
and database effects. For existing job-based applications, use [HangfireOutboxDemo](samples/HangfireOutboxDemo/README.fa.md)
(`hangfire-outbox-demo`): atomic row claims, retry after interrupted delivery and persisted execution results.
Both have a runnable SQL Server CI exercise. The [DDD and event-delivery guide](docs/EVENT-DELIVERY.fa.md)
explains transaction boundaries, migration, monitoring and reconciliation. MCP 0.6.0 provides five read-only tools;
`neo_get_example` offers five examples and bundles required Saga dependency source.

Use [CrudResourceDemo](samples/CrudResourceDemo/README.fa.md) (`crud-resource-demo` in MCP) for separate DTOs,
per-operation policies, optimistic versions, SQL Server pessimistic locks, and entity/translation/Outbox transactions.
The [resource guide](docs/CRUD-RESOURCES.fa.md) explains migration and concurrency limits.
The [feature generator](docs/FEATURE-GENERATOR.fa.md) exposes `neo new feature`, reuses that sample,
and creates a compilable application in a new directory. Its runtime Doctor checks DI, EF metadata and policies
without migrations or writes. MCP diagnosis remains static. CRUD staging does not start an Outbox worker.

New infrastructure capabilities should ship with a runnable usage example, local configuration,
expected output, a failure exercise and behavioral tests. Document guarantees and omissions,
especially persistence, retries and authorization. Existing Hangfire jobs remain available;
cross-service events are an additional, explicit capability.

## Repository layout

```text
.agents/skills/neo-feature/       Feature-building skill
.agents/skills/neo-telemetry/     Telemetry skill
.agents/skills/neo-doctor/        DI and telemetry diagnosis skill
src/Neo.Domain/Features/Telementry/       Framework telemetry runtime
src/Neo.Infrastructure/Features/Telementry/  Export setup
tests/Neo.Domain.Tests/Telemetry/         Runtime regression tests
tools/Neo.Companion/
  src/Neo.Companion.Mcp/         C# MCP server (stdio)
  src/Neo.Companion.Cli/         neo new feature generator
  samples/CrudResourceDemo/     Concurrency, policies, transactions and runtime Doctor
  samples/ProductCatalog/       Domain/Application/Infrastructure/API example
  samples/TelemetryDemo/        Manual and attribute-based instrumentation
  samples/MessagingDemo/        RabbitMQ, Saga and compensation exercises
  tests/Neo.Companion.Tests/    Sample, MCP and linked runtime regressions
  knowledge/                   Source snapshots and developer guidance
  scripts/                     Snapshot check, protocol smoke test, packaging
.github/workflows/companion.yml Build/test/package on GitHub Actions
```

## Build and try it

Install .NET 10 SDK and Python 3.10+ (for the small maintenance/smoke scripts). From the **Neo repository root**:

```bash
dotnet build tools/Neo.Companion/Neo.Companion.slnx -c Release
dotnet test tools/Neo.Companion/tests/Neo.Companion.Tests/Neo.Companion.Tests.csproj -c Release --no-build
dotnet run --project tools/Neo.Companion/samples/TelemetryDemo -c Release --no-build -- manual
dotnet run --project tools/Neo.Companion/samples/TelemetryDemo -c Release --no-build -- attribute
```

The attribute mode resolves the actual `AddScopedWithTelemetry<IProductLookup, ProductLookup>()` interface proxy. Both modes should show `catalog.lookup`, successful status and request metrics including a measured duration.

Run ProductCatalog:

```bash
dotnet run --project tools/Neo.Companion/samples/ProductCatalog/Api -c Release --no-build -- --urls http://127.0.0.1:5099
```

POST `/products` with `{ "name": "Keyboard", "price": 49.90 }`. A valid request returns 201 plus a Location header; invalid names/prices return 400. GET that Location to retrieve the persisted SQLite record. This is a local teaching sample using EnsureCreated.

No second framework clone or `.neo` checkout is needed. `NeoRoot` defaults to this repository's root. Companion dependencies are isolated from the framework's central package versions; framework project references still use their own version management. The companion has its own solution so normal framework builds do not need to build the development tools.

## Run the MCP locally

```bash
dotnet publish tools/Neo.Companion/src/Neo.Companion.Mcp -c Release -o tools/Neo.Companion/artifacts/mcp
dotnet publish tools/Neo.Companion/src/Neo.Companion.Cli -c Release -o tools/Neo.Companion/artifacts/cli
python tools/Neo.Companion/scripts/smoke_mcp.py --server-dll tools/Neo.Companion/artifacts/mcp/Neo.Companion.Mcp.dll --project-root tools/Neo.Companion/samples
```

Use [codex-mcp.example.toml](codex-mcp.example.toml) in your client's MCP settings, with absolute paths. The command is `dotnet` and its argument is the published DLL. Set `NEO_PROJECT_ROOT` to the consuming application. The optional PowerShell launcher [run-mcp.ps1](scripts/run-mcp.ps1) locates the built/published DLL. Standard output is reserved for MCP; logging goes to standard error.

| Tool | Purpose |
|---|---|
| `neo_inspect_project` | Read project/central-package declarations without running MSBuild |
| `neo_search_docs` | Return matching guidance/source lines and a source fingerprint |
| `neo_get_example` | Retrieve ProductCatalog or messaging/Saga examples for the matching fingerprint |
| `neo_get_telemetry_recipe` | Retrieve manual/attribute telemetry setup, source and semantics |
| `neo_diagnose_project` | Inspect C# syntax and selected appsettings keys for DI/telemetry review candidates |

These tools provide developer guidance, not access to running applications' production traces. They make no model calls and require no OpenAI API key. The inspector does not evaluate imports or resolve installed versions. Recipe/example retrieval rejects an unmatched fingerprint.

Doctor uses Roslyn syntax trees without compiling or executing the application. Point `NEO_PROJECT_ROOT` at one consuming application and set `configurationSection` to its actual telemetry JSON section (default `TelemetryOptions`, empty for root). Results include rule codes, file locations, sanitized evidence, suggested checks and coverage limits. They never assert runtime health or return configuration values. Conditional compilation, external registrations and environment overrides require follow-up. See the [teaching cases](samples/DoctorCases/README.md) for broken and corrected services exercised by real runtime tests.

The skills are discoverable under the repo's `.agents/skills`. For another application, copy the desired skill folder into its `.agents/skills` directory. Try `$neo-telemetry` with a real service instrumentation request. The skills also work by reading source when MCP is unavailable.

## GitHub distribution and hosting

**A server is not required for this stdio MCP.** Users run it on their own machines through an MCP client. GitHub stores source; Actions builds/tests/packages it; an Actions artifact or a Release asset distributes the executable and skills. GitHub Actions is a build runner, not an always-on MCP host.

The Companion workflow builds on Windows and Linux, executes a generated feature and uploads a ZIP containing MCP, CLI, skills and examples. To package locally after publishing both MCP and CLI:

```bash
python tools/Neo.Companion/scripts/package_companion.py
```

The ZIP contains the published server, knowledge/examples and canonical skills. It needs .NET 10 runtime on the user's machine; no SDK is needed just to launch the packaged MCP. A human-maintained GitHub Release can attach this ZIP. Publishing/creating a release is separate from preparing this repository structure.

A shared remote MCP would require an HTTP endpoint and authentication on an application hosting service. That hosting does not have to be a purchased dedicated server. This version intentionally uses local stdio and requires no hosting subscription.

## Keep guidance aligned with code

After changing a tracked framework contract:

```bash
python tools/Neo.Companion/scripts/sync_knowledge.py
python tools/Neo.Companion/scripts/sync_knowledge.py --check
```

Review the skill and examples at the same time. The manifest identifies normalized source contents by SHA-256; its base commit is provenance, not an assertion that later working-tree changes belong to that commit. CI refuses stale contract snapshots.

Regression tests in `tests/Neo.Domain.Tests/Telemetry` are also compiled into the Companion test project, so this workflow exercises the repaired runtime. Behavioral skill evaluation with real user tasks should continue as new features are added.

References: [MCP configuration](https://learn.chatgpt.com/docs/extend/mcp?surface=cli), [Skills](https://learn.chatgpt.com/docs/build-skills), [GitHub Actions for .NET](https://docs.github.com/en/actions/tutorials/build-and-test-code/net), [GitHub Releases](https://docs.github.com/en/repositories/releasing-projects-on-github/about-releases).
