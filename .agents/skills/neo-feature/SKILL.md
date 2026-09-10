---
name: neo-feature
description: Build or extend application features using SeyediDev's Neo Framework for .NET, including domain models, CQRS handlers, repositories and endpoints. Use for Neo-Framework projects or explicit Neo feature requests; not for Neo blockchain or unrelated frameworks.
---

# Build a Neo feature

Deliver a working feature in the user's existing application, following its conventions and the actual Neo API version. Use the user's language. If they ask to learn, explain each layer and the important decisions as you work; otherwise keep explanations brief. Do not turn every feature into a framework migration or redesign.

## Establish the contracts

Inspect the consuming csproj files, central package versions and existing feature implementations. Distinguish package references from source references and declared versions from restored/evaluated versions. Do not choose a version from a README badge.

If the Neo MCP is available, use `neo_inspect_project` and `neo_search_docs` to locate evidence. The inspector is static and may return unresolved properties. Its bundled baseline does not establish the application's version. `neo_get_example` supplies a source-pinned example, not a universal template.

Without MCP, read the referenced Neo source or the installed package's API documentation and one working feature in the application. Read [contracts.md](references/contracts.md) for verified baseline details and common documentation mismatches. If the actual contract is unavailable, identify the unresolved API before generating code dependent on it.

## Implement the requested behavior

- Put business invariants in the domain creation/update operations. Validate input at the application boundary so the API can explain invalid requests.
- Follow the project's CQRS, key type, error/result and persistence conventions. Locate an existing handler before adding a new pattern.
- For the pinned source baseline, create through `ICommandRepository<TEntity, TKey>` and commit through its `UnitOfWork`; read through `IQueryRepository<TEntity, TKey>`. Check the exact signatures before using another version.
- Register the concrete DbContext, repository implementations and handlers required by the feature. Calling a broad Neo extension does not provide the application's repositories or all integration settings.
- If using Neo's existing validation pipeline, use its exception handling path; do not add redundant handler validation. The standalone teaching example uses explicit validation because it deliberately registers only MediatR.
- Preserve transaction and event timing semantics. `AddDomainEvent` queues an event; the current interceptor dispatches during SavingChanges. Do not treat that as post-commit delivery or automatically durable Outbox publication.
- Do not invent an API based on its expected name. In this baseline `AppControllerBase` exposes `Sender`; `Result<T>` exposes `Data`; the repository requires `new()` on the entity.

## Verify and explain

Build the affected projects. Test the behavior that matters: a valid request persists and can be retrieved; invalid input does not persist; relevant missing resources return the expected response. Use the existing test stack and isolate test data. Explain validation results accurately, including failures caused by dependencies or the environment.

In learning mode, connect the changed files to the request flow and explain one meaningful tradeoff, such as factory methods versus Neo's public parameterless constructor constraint. Give the user a small next exercise only after completing their requested feature.
