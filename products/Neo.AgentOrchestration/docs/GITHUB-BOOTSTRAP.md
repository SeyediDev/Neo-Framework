# Public backlog bootstrap

`tools/Publish-GitHubBacklog.ps1` publishes reviewed public summaries as repository
Issues and appends the remote links to the corresponding Neo task history. It is
an **initial one-way publication tool**, not the planned continuous work provider.
It does not implement Projects boards, webhooks, conflict resolution or reverse sync.
Native task data, claims, roles, timers and private context remain authoritative in Neo.

## Prerequisites and execution

Use PowerShell 7, a running scoped Neo API, an authorized chat identity and a
GitHub credential with issue write access. `GH_TOKEN` takes precedence over the
existing Git Credential Manager credential. The token is not saved or printed.
Normal Neo connections require HTTPS and `NEO_ORCHESTRATION_TOKEN`; the explicit
`-LocalDevelopment` option works only with loopback addresses and still requires
the server's authorized local-development setup.

Prepare a JSON array manually, e.g.:

```json
[{"key":"PRODUCT-101","title":"Improve project planning","summary":"Public acceptance criteria, approved for publication."}]
```

Never copy raw task descriptions, security reports, chat transcripts or private
logs into this file. A public repository makes the entire manifest content public.
No fields are automatically exported from Neo except task identity/status.

```powershell
$options = @{
    NeoWorkspaceApi = 'https://neo.example/api/orchestration/v1/organizations/<organization-guid>/workspaces/<workspace-guid>'
    ProjectId = '<project-guid>'
    ChatId = '<real-chat-id>'
    Repository = 'owner/repository'
    PublicManifest = './reviewed-public-items.json'
}
./tools/Publish-GitHubBacklog.ps1 @options          # preview, no GitHub request/write
./tools/Publish-GitHubBacklog.ps1 @options -Apply   # explicitly publish
```

Items already owned by another chat or archived are refused. Existing issues
are reconciled by a stable internal-ID marker, not their mutable title. Ambiguous
markers or markers published by another account require manual review. Replays
do not rewrite existing issues or append duplicate link messages. New completed
items are closed after creation; an uncertain close must be inspected manually.
If a remote write succeeds but the local link write fails, rerun after checking
the item version/ownership to recover the link. Run only one publisher per scope;
there is no distributed lease or exactly-once guarantee.

The full provider remains tracked under NAO-028/029 and ORCH-US-GITHUB. It needs
durable mappings/outbox, field ownership, conflict handling, signed webhooks and
Projects capability checks. Projects API access is separate from repository access:
https://docs.github.com/en/issues/planning-and-tracking-with-projects/automating-your-project/using-the-api-to-manage-projects
