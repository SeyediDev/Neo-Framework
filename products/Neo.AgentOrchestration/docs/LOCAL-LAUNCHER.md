# Windows local development launcher

Use PowerShell 7 and .NET 10. Build the product first into an explicit binary
directory containing API, Web and `neo-agent.dll`. Supply the already provisioned
database connection privately as `NEO_ORCHESTRATION_SQL`; never commit or paste it
into a task. The launcher does not create/migrate/seed databases or run agents.

```powershell
./products/Neo.AgentOrchestration/tools/Start-Local.ps1 `
  -BinaryDirectory '<absolute-build-output>' `
  -OrganizationId '<authorized-organization-guid>' `
  -WorkspaceId '<authorized-workspace-guid>'
```

The default ports are 5180/5181, loopback only. It checks schema health with the
read-only CLI, validates both port owners before stopping anything, starts missing
hosts with the correct content roots, and waits for a scoped API read and rendered
board. Healthy matching hosts are reused on replay. A different build/process or
wildcard listener is refused; inspect it yourself rather than terminating generic
`dotnet` processes. `-Restart` replaces only verified exact-build processes after
rechecking their creation identity. Concurrent launches on the same port pair are
serialized by a named mutex. Unique stdout/stderr files retain earlier logs.

The launcher enables the constrained Development local mode only for its children
and restores the parent process's environment. Its process credentials may be
inherited by the child hosts; use only your private development account. Logs live
under local application data by default; protect this directory and do not publish
raw logs. No secrets are placed in command arguments or returned status.

If readiness fails, hosts/logs remain available for diagnosis; it does not destroy
data or stop unrelated applications. Existing healthy processes are not hot-updated
when files/settings change: build into a fresh directory, then explicitly stop the
known old hosts or restart using their exact build. Production requires normal
authenticated deployment and is outside this local-only script.
