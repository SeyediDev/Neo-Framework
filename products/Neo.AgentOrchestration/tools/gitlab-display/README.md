# Private GitLab Kanban display

This is an optional, one-way display adapter, not a new task authority or an
agent executor. It reads the existing scoped work API and maintains confidential
Issues in one active **private** GitLab project. GitLab outages do not stop native
work, ownership, timers, workflow or execution. No application database is queried
directly and no source API mutations are performed.

## Identity and access

Bind the destination to the verified primary checkout/remote and active host
project label. A worktree is not another project. Discover and reuse the GitLab
project before creating one; do not silently replace the primary Git origin,
clone/import code or change another chat's pipeline for this display integration.

Use the existing central Fanasa Keycloak realm, exact GitLab OIDC callback,
authorization-code flow with PKCE and immutable `sub`. No automatic account linking
by email. Preprovision only explicitly authorized users or keep JIT accounts
blocked until approved. This adapter grants no access to other tenants. Private
project membership remains independent of SSO. Preserve restricted break-glass
access and upstream GitLab identity.

The source machine client uses `client_credentials`, the work API audience and
only `organization/workspace/read`. It has no write/configure/execute/approve
grant. The destination is a dedicated project token with `api`, Reporter role
and finite expiry; no administrator token or repository-write scope is retained.
Project tokens still allow editing their own Issues: this is not a read-only
token, although its source access is read-only. Rotate/revoke through GitLab's
supported API before expiry and update the restricted `gitlab` credential.

## Privacy and display policy

Published fields: scrubbed human-readable title/key, status, priority, parent
link, source item link and nullable time/token estimates and reported aggregate
usage inside expandable details. Free-form descriptions, acceptance text, logs,
prompts, chat IDs, owner identity, credentials and agent-private context are
never serialized to the destination or retained in mapping state. Title scrubbing
is conservative assistance, not a guarantee against a user putting a secret in a
title. Do not put secrets in task titles; use approved neutral titles where needed.

Five colored active status lists map Backlog, Ready, InProgress, Blocked and
Review. Done and Cancelled are **closed** Issues with distinct labels in GitLab's
Closed list, not two separate terminal columns. Archived items are not initially
published; already linked archived items close without deleting their Issues.
The source is authoritative for managed title/description/status. External
comments, unmanaged description suffix/prefix and unrelated labels are preserved.
Dragging a GitLab card does not change native work or start an agent; subsequent
sync restores managed status. Use the source link for authoritative changes.

## Installation

Linux with Python 3.10+ and systemd; no pip package, extra Docker service or Node
runtime. Provision the private project, labels/board, SSO/membership and scoped
credentials explicitly with administrative authority, then put:

- `config.json` under `/opt/fanasa-work-gitlab` using the example schema and
  verified IDs. There are no live installation IDs in the reusable example.
- Root-owned mode-0600 files in `/opt/fanasa-work-gitlab/credentials`: `source`
  (JSON client_id/client_secret), `gitlab` (JSON token), `mapping-key` (32+ random
  bytes). Never commit these files, put tokens in arguments, export issuer signing
  keys, use browser cookies or disable authentication/certificate verification.

Bundle the seven files in this directory flat, without caches or credentials.
Verify its SHA256 and run:

```sh
python3 install.py --archive /tmp/work-gitlab.zip --sha256 VERIFIED_SHA256 --apply
systemctl status fanasa-work-gitlab.timer
cat /var/lib/fanasa-work-gitlab/last-result.json
```

The explicit installer tests the release before activation, retains previous
release/configuration paths and installs a non-root, hardened oneshot service.
Systemd passes secrets with LoadCredential. The timer runs two minutes after each
completed cycle; changes are not websocket-instant. Only mapping state and compact
counts are persisted. A runtime error does not release native work or pause agents.
Stop the timer to disconnect; do not delete native/remote tasks or credentials
implicitly. A local internal domain requires managed internal DNS or a precise
hosts mapping, and a trusted existing TLS certificate. Do not silently alter a
user's trust store.

## Verification and reconciliation

```sh
python3 -m unittest discover -s . -p 'test_*.py'
systemctl start fanasa-work-gitlab.service
journalctl -u fanasa-work-gitlab.service -n 10 --no-pager
```

Mappings use SQLite scoped by the full connection, authenticated markers and
verified bot authorship. A pending create is committed before POST. On lost response,
the next cycle may recover one matching bot-owned Issue. With no receipt, duplicate
markers, missing mapped Issue or changed managed delimiters, it fails closed for
operator reconciliation instead of recreating blindly. Proven 4xx rejection is
not treated as an unknown create. Missing source items are not deleted remotely.
Pagination/size/mutation bounds and redirect rejection apply; source snapshot
changes defer publication. Exactly-once external delivery is not claimed.

Live acceptance requires matching source/destination counts and statuses, a second
unchanged run with zero writes, allowed-user SSO and denied anonymous access. Unit
tests alone do not establish actual login. Test status reconciliation using a
dedicated integration test Issue or authorized real transition, not by moving a
live task to Done without its acceptance. MCP/domain schemas are unchanged.
