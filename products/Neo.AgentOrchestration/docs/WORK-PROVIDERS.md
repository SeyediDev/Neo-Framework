# Optional work providers and coexistence

Approved product direction, 2026-09-27. This is an architecture decision and
acceptance contract, **not an implemented external integration**. Native Neo work
management continues. No GitHub-only pivot, legacy cleanup or data cutover is
authorized by this decision alone. Subsequent cutover approval and the current
Neo API operational board are documented in COEXISTENCE.md and AGENTS.md.
WorkManagement is retained legacy data, not the current coordination source.

## Independent choices

Keep three capabilities separate:

- **Work management:** native Neo, GitHub Issues/Projects, GitLab, Azure DevOps
  Boards. An installation/project can enable multiple connections, including
  Neo and GitHub together. External capabilities must be discovered/validated;
  do not assume feature parity or represent planned adapters as installed.
- **Repository/review:** repository, branch, PR/MR, checks and review status.
  Using GitHub PRs does not require replacing the Neo backlog. Boards and Repos
  need not be supplied by the same connection/provider.
- **Agent execution:** harness/provider/model, run, queue and workflow. This is
  independent of both choices above. A work-provider event cannot invoke an
  agent or approve a gate without Neo's authorization and transition checks.

Native Neo remains fully usable with no external credentials, network or
subscription. Its Web retains board, details, roles, agents, workflows, timing
and history. Optional integrations do not reduce it to a read-only runtime log.
Future decisions about stopping native feature development are not assumed.

## Repository and external Project identity

Follow the canonical skill's
[active-project binding rule](../.agents/skills/neo-agent-orchestration/references/project-binding.md).
The repository comes from the active chat project's verified primary Git remote;
the GitHub Project title comes from the active project label in the host app.
Those are distinct from Neo's internal project key and the chat title. Resolve
and reuse external IDs, including for worktrees/junctions, before publication.
This is the instructed selection policy; automatic runtime resolution/binding
is still part of the unfinished provider integration, not implemented by this text.

## One linked work item, explicit field ownership

Retain Neo's internal scoped work ID. Store external references separately using
connection/installation, remote project/repository and remote item ID. Display
links and synchronization state on the same item; do not create a second
uncoordinated backlog or use a remote display number as a global identity.

Each mapping has an explicit policy/version and one authority **per field**:

| Data | Default authority / behavior |
| --- | --- |
| Native unlinked task | Neo, as today |
| Linked title, description, priority, planning status | Selected per mapping: Neo-owned or remote-owned; no automatic dual authority |
| PR/MR, checks, merge and remote review facts | Repository provider; distinguish facts from Neo's business acceptance |
| Run, active claim/lease, harness config, timer intervals, private context | Neo; remote changes cannot overwrite these |
| Project/domain/role/estimate metadata | Map supported fields explicitly; preserve unmapped Neo values |
| Notes and result summaries | Append with source IDs and author attribution; publish only selected content |

Both interfaces may request an edit. Route it through the field's authority,
with expected version and a durable pending command; acknowledge it only after
the authoritative result. Show pending/failed/conflict states in Neo. An external
status edit must not bypass active ownership, dependencies, children, approvals
or cause an unapproved run. Never use timestamp-only last-write-wins to resolve
a conflict. An operator resolves conflicting changes with an audited decision.

Webhook signatures/authentication, producer-specific event IDs, versions,
inbox/outbox and reconciliation must address duplicates, out-of-order delivery,
echo loops and unknown external outcomes. A destination without suitable
conditional writes requires a conservative reconciliation/conflict policy.
Do not claim exactly-once remote effects.

## Availability and security

- Connections are scoped/configurable per workspace/project, off by default,
  with explicit permissions and references to separately held credentials.
- Disabling/disconnecting preserves native tasks, local-only fields, history,
  mapping IDs and pending/conflict records. It does not delete remote content,
  release a live run, or silently change field authority.
- Reconnection reconciles checkpoints/versions before replay. Optional remote
  unavailability does not disable unrelated native Neo operations.
- Private chat context, raw prompts, full run logs and secrets are not published
  by default. Export summaries/artifacts only under an explicit content policy.
- The UI exposes actual provider capabilities and limits. Users can keep
  unsupported features in Neo rather than losing them during synchronization.
- Keep SDKs, webhook schemas and provider-specific authentication in adapters.
  Domain and workflow execution must not depend on a GitHub/GitLab/Azure SDK.

## Delivery order and acceptance

Finish the core product's runnable scenario, standalone Web, harness contract,
migration path and installable bundle first. Preserve these boundaries now; do
not delay that work with four speculative adapters. Operational records:

- NAO-028: connection/link/capability/field-policy and conflict contracts.
- NAO-029: first GitHub integration with native Neo **simultaneously active**.
- NAO-030 / NAO-031: later GitLab / Azure DevOps providers.

Live ownership/status/dependencies belong in the scoped Neo API, not this
document; re-read before claiming these items. The first integration must demonstrate native-only use plus
linked Neo/GitHub use, edits from both UIs, duplicate/out-of-order events,
conflicting versions, disconnect/reconnect, permission revocation, no secret or
private-log export, and no duplicate agent run. Live external setup requires the
target account, repositories/projects and scoped credentials. Nothing in this
decision itself creates or modifies an external project.
