# Portable coordination skill

The canonical skill lives at
[`.agents/skills/neo-agent-orchestration`](../.agents/skills/neo-agent-orchestration/SKILL.md).
It is self-contained (entrypoint, tool reference and UI metadata), without
machine paths or dependencies on Hyper documents/global skills.

It applies when a project has explicitly chosen an authorized Neo workspace
for operational coordination. It does not silently migrate an existing board.
This installation uses the independent Neo API as its operational source;
WorkManagement is retained legacy data, per AGENTS.md and COEXISTENCE.md.
The skill's presence is guidance, not a claim that every running chat loaded it
or a substitute for the API's ownership and version checks.

For a consuming repository, copy the complete skill folder into that
repository's `.agents/skills/` using its normal authorized setup workflow and
reference it from the repository's AGENTS.md when Neo is the designated board.
Do not overwrite an existing skill with local changes without review. A global
installation or MCP-client registration is a separate explicit setup operation;
this change does neither. New/current sessions should load the correct skill;
already-running chats must explicitly reread changed instructions.

Use the operational [MCP setup](MCP.md), not the generic read-only Neo Companion.
The generated tools/list schema is authoritative. The skill distinguishes manual
coding claims from managed execution, and gate reevaluation from reassignment.
Normal implicit skill discovery remains enabled; no installation-specific server
identifier, credential or fabricated identity is embedded in metadata.

Validation uses skill-creator's quick_validate.py plus reference/schema review.
Real stdio/Kestrel/JWT/SQL behavior is covered by OperationalMcpTests, not inferred
from matching documentation strings. Decision checks include another chat's
active claim, resuming one open timer, stale versions, uncertain creates/runs,
missing evidence/approval, foreign grants and preserving a legacy board.
These checks do not establish a real issuer/model execution or migration acceptance.
