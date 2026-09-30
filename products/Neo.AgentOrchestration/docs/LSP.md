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
  "timeoutSeconds": 60
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
