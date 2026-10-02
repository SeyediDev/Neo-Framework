# Local bundle

`tools/Package-Local.ps1` creates a framework-dependent bundle containing API,
Web, Worker, MCP and `neo-agent`. It is a local deployment artifact, not a
production installer. It requires the .NET 10 runtime and a separately
provisioned independent database.

```powershell
$env:NEO_ORCHESTRATION_SQL = '<private connection string>'
dotnet .\cli\neo-agent.dll migrate NeoAgentOrchestration
dotnet .\cli\neo-agent.dll health NeoAgentOrchestration
dotnet .\cli\neo-agent.dll seed NeoAgentOrchestration .\bootstrap.json
.\Start-Local-Bundle.ps1 -OrganizationId '<guid>' -WorkspaceId '<guid>'
```

Migration and seed are explicit commands; the launcher performs a read-only
health check and starts only loopback API/Web processes. It does not import
legacy data, start the Worker, install an operating-system service, register an
MCP client or write credentials. Set MCP `NeoMcp__*` settings privately for
the MCP client. GitHub Actions publishes the same bundle as a workflow artifact
on product changes.
