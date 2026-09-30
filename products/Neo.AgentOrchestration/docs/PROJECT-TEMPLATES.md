# Versioned project templates

Templates are a separate domain inside the independent product. SQL tables
nao.ProjectTemplates and nao.TemplateInstantiations preserve published versions,
content hashes, creator identity, parameters and generated entity mappings.
Migration is explicit and forward-only; it never changes prior projects or starts
an agent. The existing workspace transaction provides atomic creation and replay
serialization; unique indexes and scoped foreign keys protect persistence.

The Web scope navigation has an “الگوهای پروژه” page at /templates. Its initial
editor contains a complete sample definition. Publish a new key with expected
latest revision zero; publish an update with the actual latest revision number.
A stale revision conflicts. Each revision is a new immutable ID; there is no
in-place editing/deletion of published versions. An old revision remains usable.

The definition is strict JSON containing parameters, roles, items and workflows.
Parameters are simple unique identifiers; values are supplied at preview/build.
Text supports literal substitution using dollar-brace parameter notation; the
built-in project.name parameter is available for item/workflow text. Keys and role
definitions are static. This is text substitution, never code/shell execution.
Limits: 64,000 definition characters, 16 parameters (2,000 characters per value),
32 roles, 200 items, 16 workflows and 64 transitions per workflow. Domain bounds
still apply after substitution. Invalid/missing keys, cycles and unknown fields
are rejected. Parent/child and dependency graphs use template-local item keys.

Preview renders the actual item titles/types/descriptions/criteria, parents and
dependencies without saving projects, roles, tasks or workflows. Instantiation
pins the immutable template ID and takes a stable nonempty requestId. Identical
source/body retries return the original receipt; changed content or source under
that requestId conflicts. Existing project keys are never overwritten.
Existing roles are reused only when key, name, scope and enabled state match;
conflicts require explicit operator reconciliation, not silent replacement.

Generated tasks start in Backlog, with no owner or active timer. Generated flows
start disabled; activate only after configuring real agent profiles and reviewing
gates. No model call, worker start, repository edit or external publication occurs
from preview/instantiation. Each task log names its template revision/hash, while
the template page shows the parameter snapshot and generated IDs. Editing a later
template version cannot modify these original records.

API routes under the authorized workspace:

- GET templates: versions and instantiation history; read grant.
- POST templates: Key, Name, ExpectedLatestRevision, DefinitionJson; configure.
- POST templates/{id}/preview: RequestId, ProjectKey, ProjectName, Parameters; configure.
- POST templates/{id}/instantiate: same request; configure AND write.

All routes also require read and enforce organization/workspace boundaries.
Web writes require anti-forgery tokens. The operational MCP has no template
configuration tool; use these explicit Web/API operations. Its existing 19 tools
can manage generated work afterward. Do not put credentials in template text or
parameters; they are saved as private workspace context, not sent to GitHub.

The first UI uses a JSON definition editor, not a drag-and-drop designer. Template
catalog/history is workspace-local and currently unpaged. SQL/HTTP/Web tests cover
preview without mutation, atomic concurrent replay, rollback, revision immutability,
permissions, scope and rendered provenance. Real agent execution remains separate.
