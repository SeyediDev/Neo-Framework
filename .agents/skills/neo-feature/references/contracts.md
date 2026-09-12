# Verified source contracts

Source API reference for [SeyediDev/Neo-Framework](https://github.com/SeyediDev/Neo-Framework).
Use the content fingerprint in tools/Neo.Companion/knowledge/manifest.json for exact source evidence; this is not a verified NuGet release version.

| Concern | Actual contract at this baseline | Source under Neo |
|---|---|---|
| Entity | `BaseEntity<TKey>`; public `Id`; `AddDomainEvent(BaseEvent)` | `src/Neo.Domain/Entities/Base/BaseEntity.cs` |
| Base repository | `IRepository<TEntity, TKey>` provides `Query()` and `UnitOfWork` | `src/Neo.Domain/Repository/IRepository.cs` |
| Create | `ICommandRepository<TEntity, TKey>.Add(entity)` or `AddAsync(entity, ct)` | `src/Neo.Domain/Repository/ICommandRepository.cs` |
| Commit | `repository.UnitOfWork.SaveChangesAsync(ct)` | `src/Neo.Domain/Repository/IUnitOfWork.cs` |
| Read | `IQueryRepository<TEntity, TKey>.GetByIdAsync(id, ct)` | `src/Neo.Domain/Repository/IQueryRepository.cs` |
| EF context | Derive from `EfDbContext<TContext>` and override `ContextAssembly` | `src/Neo.Infrastructure/Data/Repository/Ef/EfDbContext.cs` |
| EF repository | Derive from `EfCommandRepository<TEntity,TKey,TContext>` / `EfQueryRepository<TEntity,TKey,TContext>` | `src/Neo.Infrastructure/Data/Repository/Ef/` |
| Controller | `AppControllerBase.Sender` | `src/Neo.Endpoint/Controller/Base/AppControllerBase.cs` |
| Result | `Result<T>.Success(data)`, `Data`, `Errors`, `ErrorMessage` | `src/Neo.Domain/Dto/Result.cs` |

The `new()` repository constraint requires a public parameterless entity constructor. Keep the business creation path in a factory; the constructor alone is not evidence of a valid aggregate. The reference sample uses `Product.Create` and private setters.

DI entrypoints are `AddNeoDomainServices(configuration)`, `AddNeoApplicationServices(configuration, assemblies)`, `AddNeoInfrastructureServices(configuration, environment)` and `AddNeoControllerServices(configuration, apiName)`. These are separate registrations, not a complete zero-configuration application bootstrap.

`AuthorizationBehaviour` currently forwards requests without active permission checks. Do not claim a command is protected merely because that behavior is registered. Authentication and endpoint authorization require their actual configured mechanisms.

`DispatchDomainEventsInterceptor` publishes during SavingChanges. It must be attached to the DbContext options to run. It does not provide post-commit delivery guarantees.

Mapster is used by the generic feature handlers. Hangfire implementation is in `Neo.Infrastructure`; there is no separate Hangfire project in this source tree.

## Generic CRUD or a dedicated feature

Use `GenericCrudControllerBase<TDto,TEntity,TKey>` for straightforward administrative data with direct DTO mapping. Prefer dedicated commands and endpoints for domain transitions, per-operation authorization or atomic entity/translation changes. Preserve the application's existing choice when it meets the requested behavior.

At this baseline the generic controller explicitly binds services from DI. Create assigns the returned key to `dto.Id` and generates Location via MVC's `GetById` action name; override `GetResourceLocation` if routing or Async suffix settings differ. Update rejects missing/mismatched IDs. Update/Delete return `Unit`, not `Result`; missing entities raise `NotFoundException`, mapped to 404 only when Neo's exception handler is enabled. Delete previously ignored missing entities, so verify the consumer's desired repeat-delete behavior.

`CultureFields` is opt-in. An empty list skips localization operations, but action service parameters still require DI registration. With localization enabled, `CultureTerm.SubjectId` requires an int-compatible key; Guid and overflowing long values are unsupported. Entity persistence and translation persistence are separate commits. Missing translations preserve the original DTO field. The `admin` route segment does not enforce authorization; generic DTO mapping does not enforce domain invariants or field-level permissions.

For the self-contained ProductCatalog example, inspect the `tools/Neo.Companion/samples/ProductCatalog` folder or call `neo_get_example` with ID `product-create` and the baseline returned by `neo_search_docs`. In an installed standalone skill without that folder or MCP, work from the target application's own files instead of assuming the example is available.
