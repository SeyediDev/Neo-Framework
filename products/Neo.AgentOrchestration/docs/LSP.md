# Read-only language tools

`neo-agent lsp <private-config.json> --allow-server-execution` runs an explicitly
selected local language server over stdio. The API/Web never accept executable
paths or start a process. Domain/workflow code has no language-server dependency.
The adapter lives in Infrastructure/LanguageTools and can use caller-owned streams.

Configuration (absolute paths are installation-local, not portable defaults):

```json
{
  "workspace": "C:\\approved\\repository",
  "document": "src/Example.cs",
  "languageId": "csharp",
  "executable": "C:\\approved\\tools\\csharp-ls.exe",
  "arguments": [],
  "line": 0,
  "character": 0,
  "timeoutSeconds": 60,
  "readinessProfile": "none"
}
```

The executable, its arguments and repository build scripts are trusted operator
configuration. Explicit permission to execute that server is required. A language
server can evaluate project/build configuration; use an OS sandbox/separate account
for untrusted repositories. Path validation is NOT a sandbox for the child process.
No server is installed or launched implicitly. CLI clears inherited environment
except essential OS/runtime/home paths; it does not forward API/database secrets.
User-home access still exists, so this does not isolate all account credentials.
Child stderr is drained/discarded, never copied into task history. Only the process
tree started by this invocation is stopped; no global dotnet/editor process kill.

Protocol baseline: [official LSP 3.17](https://microsoft.github.io/language-server-protocol/specifications/lsp/3.17/specification/).
This is a bounded subset, not a claim of full 3.17/3.18 implementation. LSP negotiates
capabilities, not a numeric protocolVersion field. Initialize advertises UTF-16
positions; a different server selection is rejected. Static definition/references
and pull diagnostics are negotiated. Versioned push diagnostics are supported for
the opened document only; stale, foreign and unversioned reports cannot establish
freshness and are ignored. Unsupported navigation is reported as unsupported, not
an empty successful result. Dynamic registration, edits, commands, rename, save,
code actions, metadata URIs and remote documents are deliberately unsupported.

One immutable document is opened at version 1 per session. The client accepts only
sequential calls from one owner (the session is not a concurrent editor client).
It reads only
files contained in the configured root and rejects symlink/junction ancestors,
UNC/non-file URIs, alternate streams, traversal and foreign navigation results.
Input is limited to 2 MiB; frames 4 MiB; headers 8 KiB; timeout at most 300 seconds.
Concurrent hostile filesystem replacement is not covered by lexical/link checks;
do not run on a directory writable by untrusted concurrent processes.
Document content hash is checked again before the CLI emits its report.
Request cancellation sends $/cancelRequest and invalidates the session rather than
reusing a possibly partial frame. Normal disposal sends didClose/shutdown/exit.
workspace/applyEdit is always refused; unsupported server requests get JSON-RPC
method-not-found. Notifications/logs cannot execute application actions.

`workspace/configuration` is supported with server defaults only: one null result
per valid item, at most 256 items, with section/scopeUri strings at most 1024
characters. Foreign scopes and malformed items are rejected; no local settings
or credentials are returned. Dynamic registration remains unsupported.

`readinessProfile` accepts `none` (default) or `roslyn-project`. The latter waits
before opening the document for Roslyn's `workspace/projectInitializationComplete`
notification, using the existing cancellation/deadline. This is an explicit
Roslyn extension, not a generic LSP readiness guarantee. Configure Roslyn project
autoload explicitly and use a supported solution/project workspace. A missing
notification times out without emitting a completed report. The default profile
does not establish project readiness, so an early empty diagnostic snapshot must
not be treated as evidence that project analysis completed.

## Experimental React snapshot server

The optional [React snapshot server](../tools/react-lsp/README.fa.md) uses pinned
TypeScript Language Service diagnostics over the existing read-only stdio client.
It is a manually configured local tool, not an API/MCP or installer launcher.
Install its locked dependencies explicitly with npm ci --ignore-scripts in its
tool directory, using an approved registry; it never downloads dependencies on
startup. Node 24.12.0 and Windows are the tested runtime. Configure node.exe as
executable and the absolute server.mjs path as an argument, with readinessProfile
set to none. No Roslyn readiness notification is involved.

The server accepts one immutable version-1 snapshot, returns full pull reports
correlated to the diagnostic request, and checks the document/read dependency
hashes before and after analysis. It does not consume unversioned push reports.
Changed documents/dependencies invalidate the session; start a fresh invocation.
The synchronous compiler API relies on the caller's deadline/process cleanup for
long-running cancellation. An inferred strict/checkJs profile is used without a
project config; its diagnostics do not replace the frontend's build/lint/test.
See the tool README for limits and the independent freshness regression test.

## Evidence and panel

The JSON report contains document hash/version, observation time, diagnostic counts
by severity and workspace-relative navigation locations. No source text, diagnostic
message or server logs are exported. A diagnostic report is not a build or test
pass; zero errors do not mean the task is complete. Use the existing scoped API/MCP
evidence operation to attach the report or a reviewed summary as **Artifact /
NotApplicable**, with a reference `lsp:<document-content-hash>` and the actual
verified commit when appropriate. The normal task evidence/history panel displays
it. Attach only to the currently authorized/owned item using its current version.
On uncertain upload, inspect evidence before retrying. Do not publish these private
workspace reports through the public GitHub bootstrap.

There is no additional LSP MCP tool or browser process launcher in this release.
The existing evidence API is the storage/display boundary; no extra database table
is required. A caller chooses an approved local server and invokes the CLI/library.
Tests cover framing, scope, negotiation, diagnostics, navigation, rejected edits
and cancellation. A real server probe is recorded separately from fixture tests.
