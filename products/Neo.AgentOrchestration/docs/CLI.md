# Explicit installation CLI

`neo-agent` is the product's provisioning executable (.NET 10), not the model
gateway or a global client registration. From the repository use:

```powershell
dotnet run --project products/Neo.AgentOrchestration/src/Neo.AgentOrchestration.Provisioning -- --help
dotnet run --project products/Neo.AgentOrchestration/src/Neo.AgentOrchestration.Provisioning -- init bootstrap.json NeoAgentOrchestration
```

`init` is offline and only creates a **new** manifest file. Its parent directory
must exist; an existing file is never overwritten. Review/edit the template's
organization, workspace, project and roles before using it. The version-1 JSON
contains keys/names/scope descriptions and a database name, no connection string,
issuer token, password or generated identity. Do not put secrets in these fields.
Unknown, duplicate, missing required or null required fields and files over 64 KiB are
rejected; role keys are normalized and must be unique (1–50 roles).

After privately setting `NEO_ORCHESTRATION_SQL` with the **same explicit catalog**:

```powershell
dotnet run --project products/Neo.AgentOrchestration/src/Neo.AgentOrchestration.Provisioning -- migrate NeoAgentOrchestration
dotnet run --project products/Neo.AgentOrchestration/src/Neo.AgentOrchestration.Provisioning -- health NeoAgentOrchestration
dotnet run --project products/Neo.AgentOrchestration/src/Neo.AgentOrchestration.Provisioning -- seed NeoAgentOrchestration bootstrap.json
```

A published executable supports the same arguments (`neo-agent init ...`);
packaging/installation on a clean machine is a separate milestone. Do not put a
connection string on the command line. No command reads Hyper configuration.

## Semantics

- `migrate` is the existing additive EF provisioner. It may create the named
  database if permitted, refuses legacy/mismatched destinations and unrelated
  tables, and never seeds or imports data. Take deployment backups before upgrades.
- `health` only reads SQL migration history and the modeled table inventory.
  It rejects missing/unknown migrations, table mismatch and pending model changes.
  It does not repair schema or verify every column/index, API availability,
  issuer login, account grants, model execution or backup readiness. SQL permission
  to see the metadata is required; inability to connect is operational failure.
- `seed` requires a current schema; it does **not** migrate. It atomically creates
  missing organization/workspace/project/roles and returns their actual IDs and
  created-record count as JSON. Repeating identical normalized input returns the
  same IDs and zero new records. Existing names/scope descriptions must match;
  disabled/conflicting entries fail without overwriting or re-enabling them.
  Additional existing entities are left alone. Concurrent seed calls serialize
  under a database bootstrap lock; existing workspaces also use the API's regular
  workspace transaction lock. A failure rolls back all seed changes.

Seed creates no users, membership grants, agents, workflows, tasks, runs or
deliveries. Use the returned organization/workspace IDs to configure authorized
issuer grants and Web/MCP scope via [API](API.md), [Web](WEB.md) and [MCP](MCP.md).
Then manage projects, role/agent profiles and workflows through the authenticated
Web/API. Roles alone do not launch agents; providers and external-execution consent
remain explicit. No authentication bypass, token minting or automatic model setup
is provided by the installer credentials.

| Exit | Meaning |
| --- | --- |
| 0 | Command completed; health means schema-current only |
| 1 | Operational failure (file access, SQL connectivity, permissions, migration/seed failure) |
| 2 | Invalid arguments/manifest/destination or missing environment configuration |
| 3 | Health read succeeded but schema is not current |
| 4 | Existing seed data conflicts or is disabled; no seed changes committed |
| 130 | Cancellation; reconcile persisted state before retrying |

Errors do not echo arguments, manifest text, exception messages or connection
details. Network loss/output failure after a commit may mean the command took
effect: reread before retrying. Migration itself can commit earlier migrations
before a later failure; health diagnoses readiness, not a whole-upgrade rollback.

No command imports or removes legacy data, changes the designated board, starts
a dispatcher, installs global settings or registers an operating-system service.
The [migration acceptance gate](COEXISTENCE.md) is unchanged. Verification uses
real child processes and the isolated SQL verification database; it does not
claim real issuer/model or clean-machine installation acceptance.
