# Local bundle

`tools/Package-Local.ps1` creates a framework-dependent bundle containing API,
Web, Worker, MCP, `neo-agent` and the private `Fanasa.AgentGateway`. It is a local deployment artifact, not a
production installer. It requires the .NET 10 runtime and a separately
provisioned independent database.

Use a **new** output directory (default `.artifacts/fanasa-agentic-work-local`).
Existing directories/archives, source destinations/ancestors and paths through
symlinks/junctions are refused before publishing. Earlier or partially failed
releases are never deleted. The manifest records the source commit and whether
tracked source changes exist; a dirty bundle is not an immutable SHA-only build.
If the commit or tracked-file status changes during publication, no archive is
created. This check does not lock other chats or detect every concurrent content
edit that leaves the same status; use a clean isolated checkout for a release.

The bundle includes the complete Markdown guide directory, portable skill and
its references, plus disabled Hermes/OpenCode model templates. Credentials and
real operator endpoints must remain outside the source and release bundle.
The manifest's `containsCredentials=false` is that packaging policy, not a
general secret-scanning attestation for arbitrary locally modified binaries.

```powershell
$env:NEO_ORCHESTRATION_SQL = '<private connection string>'
dotnet .\cli\neo-agent.dll migrate NeoAgentOrchestration
dotnet .\cli\neo-agent.dll health NeoAgentOrchestration
dotnet .\cli\neo-agent.dll seed NeoAgentOrchestration .\bootstrap.json
.\Start-Local-Bundle.ps1 -OrganizationId '<guid>' -WorkspaceId '<guid>'
```

Migration and seed are explicit commands; the launcher performs a read-only
health check and starts only loopback API/Web processes. It does not import
legacy data, start the Worker/gateway/native agents/model, install an operating-system service, register an
MCP client or write credentials. Set MCP `NeoMcp__*` settings privately for
the MCP client. GitHub Actions publishes the same bundle as a workflow artifact
on product changes.

Gateway delivery is packaging only: the gateway default exits without listening.
Its explicit opt-in requires an independent, reviewed schema and private binding;
the default sandbox still rejects execution. The included guides are
`docs/GATEWAY-JOURNAL.md`, `docs/MODEL-CONNECTIONS.md` and `docs/VPS-AGENT-PLAN.md`.
Do not enable a gateway or worker simply because its files are present. Native
runtime installation, real sandbox/evidence, permission replies, metering
ingestion and live pilot acceptance remain separate work.
