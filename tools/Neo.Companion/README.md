# Neo Companion

Developer assistance for Neo Framework, maintained **inside the Neo repository**.

[شروع فارسی](docs/START-HERE.fa.md) · [Telemetry walkthrough](docs/TELEMETRY.fa.md) · [Validation](docs/VALIDATION.md)

## Repository layout

```text
.agents/skills/neo-feature/       Feature-building skill
.agents/skills/neo-telemetry/     Telemetry skill
src/Neo.Domain/Features/Telementry/       Framework telemetry runtime
src/Neo.Infrastructure/Features/Telementry/  Export setup
tests/Neo.Domain.Tests/Telemetry/         Runtime regression tests
tools/Neo.Companion/
  src/Neo.Companion.Mcp/         C# MCP server (stdio)
  samples/ProductCatalog/       Domain/Application/Infrastructure/API example
  samples/TelemetryDemo/        Manual and attribute-based instrumentation
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
python tools/Neo.Companion/scripts/smoke_mcp.py --server-dll tools/Neo.Companion/artifacts/mcp/Neo.Companion.Mcp.dll --project-root tools/Neo.Companion/samples
```

Use [codex-mcp.example.toml](codex-mcp.example.toml) in your client's MCP settings, with absolute paths. The command is `dotnet` and its argument is the published DLL. Set `NEO_PROJECT_ROOT` to the consuming application. The optional PowerShell launcher [run-mcp.ps1](scripts/run-mcp.ps1) locates the built/published DLL. Standard output is reserved for MCP; logging goes to standard error.

| Tool | Purpose |
|---|---|
| `neo_inspect_project` | Read project/central-package declarations without running MSBuild |
| `neo_search_docs` | Return matching guidance/source lines and a source fingerprint |
| `neo_get_example` | Retrieve the ProductCatalog example for the matching fingerprint |
| `neo_get_telemetry_recipe` | Retrieve manual/attribute telemetry setup, source and semantics |

These tools provide developer guidance, not access to running applications' production traces. They make no model calls and require no OpenAI API key. The inspector does not evaluate imports or resolve installed versions. Recipe/example retrieval rejects an unmatched fingerprint.

The skills are discoverable under the repo's `.agents/skills`. For another application, copy the desired skill folder into its `.agents/skills` directory. Try `$neo-telemetry` with a real service instrumentation request. The skills also work by reading source when MCP is unavailable.

## GitHub distribution and hosting

**A server is not required for this stdio MCP.** Users run it on their own machines through an MCP client. GitHub stores source; Actions builds/tests/packages it; an Actions artifact or a Release asset distributes the executable and skills. GitHub Actions is a build runner, not an always-on MCP host.

The Companion workflow builds on Windows and Linux and uploads an installable ZIP. To package locally after publish:

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
