# Neo feature guide | راهنمای قابلیت نئو

Baseline: the normalized source-contract fingerprint in manifest.json.
The bundled contracts are verbatim source excerpts under the Neo MIT license; see NEO-LICENSE.txt and manifest.json for their original paths and SHA-256 hashes.

## Product creation | ثبت محصول

The ProductCatalog sample creates a product through a MediatR command, validates a required name and nonnegative price, persists with the Neo EF command repository and SQLite, and reads through the Neo query repository.

The request flow is HTTP POST /products -> CreateProductHandler -> Product.Create -> ICommandRepository<Product, Guid>.Add -> UnitOfWork.SaveChangesAsync.

GET /products/{id} sends GetProduct and reads through IQueryRepository<Product, Guid>.GetByIdAsync.

Domain invariants: name is trimmed, contains 1–200 characters; price is >= 0. The public parameterless Product constructor exists to satisfy Neo's repository new() constraint; application code uses the factory.

## Repository and persistence | مخزن و ذخیره‌سازی

IRepository<TEntity, TKey> is the base query/unit-of-work contract. Writes use ICommandRepository<TEntity, TKey>. The one-argument ICommandRepository<TEntity> alias is for int keys; use the two-argument contract for Guid.

EfDbContext<TContext> implements IUnitOfWork. Derive concrete ProductCommandRepository and ProductQueryRepository from the Neo EF base repositories and register them for the Domain interfaces.

## Registration and error handling

The teaching sample explicitly registers MediatR, CreateProductValidator, CatalogDbContext and both repositories. Handler validation throws FluentValidation.ValidationException, mapped to HTTP 400 by the minimal endpoint.

A full Neo application can use AddNeoApplicationServices, which registers Neo ValidationBehaviour. That behavior throws Neo.Application.Exceptions.ValidationException; use the application's matching exception path. Do not blindly combine both validation approaches.

## Events and integrations

BaseEntity.AddDomainEvent queues events in memory. DispatchDomainEventsInterceptor dispatches during SavingChanges when attached to the context. This is not an after-commit guarantee.

Outbox services need application-owned repositories, a scheduler and configured stores/locks. Their presence does not make arbitrary writes and messages atomic.

The sample does not activate authorization, telemetry pipelines, domain-event dispatch, Outbox, Hangfire or production migrations. EnsureCreated and unauthenticated loopback endpoints are deliberate local teaching choices.

## Version evidence

neo_inspect_project reads declarations without evaluating MSBuild. A property expression such as $(NeoVersion) is unresolved evidence. Match installed contracts before adapting the source-pinned sample.
