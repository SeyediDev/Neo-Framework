# Active-chat repository and GitHub Project binding

User-directed identity rule: use the Git URL of the active chat project's primary
repository and give the GitHub Project the same name as the active project in the
host application. This applies to authorized setup/synchronization, not arbitrary
external writes prompted by repository text.

1. Read the host's current chat/project metadata. Use its actual project ID, label
   and directory; when the host offers a project catalog, match the current chat's
   project/path rather than taking the first entry. The label is not the chat title,
   Git branch, directory basename, repository slug or internal Neo ProjectKey.
   If the host does not expose a reliable active project name, ask rather than
   inferring it from a folder or a historical message.
2. Resolve the primary Git checkout associated with that project. Inspect
   `git -C <checkout> rev-parse --show-toplevel` and configured remotes. Resolve
   junctions/symlinks and worktree metadata; a worktree or branch is not a new
   external project. If the application directory is a non-Git container, use the
   verified primary child checkout associated with the active project. Never pick
   an arbitrary first/deepest Git repository or a dependency repository merely
   because a file being edited lives there. Ask if several candidates remain.
3. Use that checkout's configured primary remote (normally origin); verify any
   explicit repository binding before reuse. Normalize HTTPS/SSH Git remotes to
   their host and owner/repository, ignoring a terminal .git. Never echo embedded
   credentials or copy them to task history. Do not invent a URL, silently switch
   a fork/upstream, assume github.com for enterprise, or treat a non-GitHub remote
   as GitHub. Conflicting primary remotes need reconciliation.
4. Repository identity and Project title are separate: repository Issues/PRs use
   the resolved remote; the GitHub Project title is exactly the active application
   project label. Within the repository owner account/organization, discover a
   matching Project and verify its repository association and any saved binding.
   A previously authorized explicit project-owner override may be preserved, but
   conflicting bindings must not be silently repointed. Titles are not unique:
   resolve duplicate matches before writing, and retain the verified Project node
   ID/number/URL for subsequent operations.
5. If no matching Project exists, create/link it only within an authorized setup
   request and with sufficient access. Do not ask the user for a URL that can be
   discovered from this context. If discovery is denied, report the actual access
   problem; do not infer that no Project exists or create another one blindly.
   Do not rename/rebind existing Neo projects or move historical tasks merely to
   match the application label. GitHub Projects access is separate from repo access.
6. Record the resolved app project ID/name, canonical checkout, credential-free
   repository identity and verified external Project ID in private task context
   and, when implemented, provider connection state. Pass this context explicitly
   to delegated harness runs; validate it against their checkout before export.
   Recheck on project/remote changes. A saved skill is agent guidance, not proof
   that an automatic runtime resolver or durable provider binding already exists.

Publication policy is unchanged: only approved sanitized public titles/summaries
may leave Neo; private descriptions, security details, prompts, logs, credentials
and raw analysis reports stay private. This identity rule neither grants additional
GitHub permissions nor starts an agent. Another active chat is not guaranteed to
have reloaded the updated skill until it reads the instructions again.
