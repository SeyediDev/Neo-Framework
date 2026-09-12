using Neo.Application.Features.GenericEntity.Commands;
using Neo.Application.Features.GenericEntity.Queries;
using Neo.Common.Extensions;
using Neo.Domain.Dto;
using Neo.Domain.Entities.Base;
using Neo.Domain.Entities.Common;
using Neo.Domain.Features.Client;
using Neo.Domain.Repository;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Neo.Endpoint.Controller.Base;

[AppRoute("admin", "[controller]")]
public abstract partial class GenericCrudControllerBase<TDto, TEntity, TKey>
    : AppControllerBase
    where TDto : class, IDto<TKey>, new()
    where TEntity : class, IEntity<TKey>, IDomainEventEntity, new()
    where TKey : struct
{
    protected virtual List<string> CultureFields { get; } = [];

    [HttpGet]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status406NotAcceptable)]
    public virtual async Task<Results<Ok<GetAllGenericEntityResponse<TDto, TKey>>, NotFound>> GetAllAsync(
        [FromServices] IGetAllGenericEntityCommandHandler<TDto, TEntity, TKey> handler,
        [FromServices] ICultureTermQueryRepository cultureTermRepository,
        int pageNumber = 1, int pageSize = 10, CancellationToken cancellationToken = default)
    {
        GetAllGenericEntityResponse<TDto, TKey>? entities = await handler.Send(new GetAllGenericEntityCommand<TDto, TEntity, TKey>
        {
            //TODO
            //Predicate = Predicate,
            //OrderBy = OrderBy,
            PageNumber = pageNumber,
            PageSize = pageSize
        }, cancellationToken);
        if (entities?.Items is { Count: > 0 })
        {
            await GetCultureTerms(entities.Items, cultureTermRepository, cancellationToken);
        }
        return TypedResults.Ok(entities);
    }

    [HttpGet("{id}")]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status406NotAcceptable)]
    public virtual async Task<Results<Ok<TDto>, NotFound>> GetByIdAsync(
        [FromServices] IGetGenericEntityCommandHandler<TDto, TEntity, TKey> handler,
        [FromServices] ICultureTermQueryRepository cultureTermRepository,
        TKey id, CancellationToken cancellationToken = default)
    {
        TDto? entity = await handler.Send(new GetGenericEntityCommand<TDto, TEntity, TKey> { Id = id }, cancellationToken);
        if (entity != null)
        {
            await GetCultureTerms([entity], cultureTermRepository, cancellationToken);
        }
        return entity == null ? TypedResults.NotFound() : TypedResults.Ok(entity);
    }

    [HttpPost]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status406NotAcceptable)]
    public virtual async Task<Results<Created<TDto>, BadRequest>> CreateAsync(
        [FromServices] ICreateGenericEntityCommandHandler<TDto, TEntity, TKey> handler,
        [FromServices] IRequesterUser requesterUser,
        [FromServices] ICommandRepository<CultureTerm, int> cultureTermCommandRepository,
        [FromServices] ICultureTermQueryRepository cultureTermRepository,
        [FromBody] TDto dto, CancellationToken cancellationToken = default)
    {
        TKey? entityKey = await handler.Send(new CreateGenericEntityCommand<TDto, TEntity, TKey> { Dto = dto }, cancellationToken);
        if (entityKey is null)
        {
            return TypedResults.BadRequest();
        }
        dto.Id = entityKey;
        if (CultureFields.Count > 0)
        {
            await SetCultureTerms(true, entityKey.Value.ToInt(), dto, requesterUser,
                cultureTermCommandRepository, cultureTermRepository, cancellationToken);
        }
        return TypedResults.Created(GetResourceLocation(entityKey.Value), dto);
    }

    // Override when the derived controller changes the GET action name or routing convention.
    protected virtual string GetResourceLocation(TKey id) =>
        Url.Action("GetById", new { id })
        ?? throw new InvalidOperationException("Cannot generate a route to the created resource.");

    [HttpPut("{id}")]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status406NotAcceptable)]
    public virtual async Task<Results<NoContent, NotFound, BadRequest>> UpdateAsync(
        [FromServices] IUpdateGenericEntityCommandHandler<TDto, TEntity, TKey> handler,
        [FromServices] IRequesterUser requesterUser,
        [FromServices] ICommandRepository<CultureTerm, int> cultureTermCommandRepository,
        [FromServices] ICultureTermQueryRepository cultureTermRepository,
        TKey? id, [FromBody] TDto dto, CancellationToken cancellationToken = default)
    {
        if (!id.HasValue || !dto.Id.HasValue || !id.Value.Equals(dto.Id.Value))
        {
            return TypedResults.BadRequest();
        }
        _ = await handler.Send(new UpdateGenericEntityCommand<TDto, TEntity, TKey> { Dto = dto }, cancellationToken);
        if (CultureFields.Count > 0)
        {
            await SetCultureTerms(false, id.Value.ToInt(), dto, requesterUser,
                cultureTermCommandRepository, cultureTermRepository, cancellationToken);
        }
        return TypedResults.NoContent();
    }

    [HttpDelete("{id}")]
    [ProducesResponseType(typeof(ValidationProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status406NotAcceptable)]
    public virtual async Task<Results<NoContent, NotFound, BadRequest>> DeleteAsync(
        [FromServices] IDeleteGenericEntityCommandHandler<TEntity, TKey> handler,
        [FromServices] ICultureTermQueryRepository cultureTermRepository,
        [FromServices] ICommandRepository<CultureTerm, int> cultureTermCommandRepository,
        TKey id, CancellationToken cancellationToken = default)
    {
        _ = await handler.Send(new DeleteGenericEntityCommand<TEntity, TKey> { Id = id }, cancellationToken);
        if (CultureFields.Count == 0)
        {
            return TypedResults.NoContent();
        }
        int subjectId = id.ToInt();
        Dictionary<int, List<CultureTerm>> dicTerms = await cultureTermRepository.GetTerms(typeof(TEntity).Name, [subjectId], cancellationToken);
        dicTerms.TryGetValue(subjectId, out List<CultureTerm>? cultureTerms);
        foreach (CultureTerm cultureTerm in cultureTerms ?? [])
        {
            cultureTerm.ExpireDate = DateTime.UtcNow;
            cultureTerm.IsDeleted = true;
            cultureTermCommandRepository.Update(cultureTerm);
        }

        if (cultureTerms is { Count: > 0 })
        {
            await cultureTermCommandRepository.UnitOfWork.SaveChangesAsync(cancellationToken);
        }

        return TypedResults.NoContent();
    }

    private async Task GetCultureTerms(List<TDto> items,
        ICultureTermQueryRepository cultureTermRepository, CancellationToken cancellationToken)
    {
        if (CultureFields == null || CultureFields.Count <= 0 || items == null || items.Count <= 0)
        {
            return;
        }
        Dictionary<int, List<CultureTerm>> subjectIds = await cultureTermRepository.GetTerms(typeof(TEntity).Name,
            items.Select(e => e.Id?.ToInt() ?? 0).ToList(), cancellationToken);

        Dictionary<int, TDto> entitiesItems = items.ToDictionary(e => e.Id?.ToInt() ?? 0);
        Dictionary<string, System.Reflection.MemberInfo> members = ReflectionField.MembersToDictionary<TDto>();
        foreach (KeyValuePair<int, List<CultureTerm>> subject in subjectIds)
        {
            if (!entitiesItems.TryGetValue(subject.Key, out TDto? entity))
            {
                continue;
            }

            foreach (string field in CultureFields)
            {
                CultureTerm? cultureTerm = subject.Value.FirstOrDefault(c => c.SubjectField == field);
                if (cultureTerm != null && members.TryGetValue(field, out System.Reflection.MemberInfo? member) && member != null)
                {
                    ReflectionField.SetValue(entity, member, cultureTerm?.Term);
                }
            }
        }
    }

    private async Task SetCultureTerms(bool isNew,
        int subjectId, TDto dto,
        IRequesterUser requesterUser,
        ICommandRepository<CultureTerm, int> cultureTermCommandRepository,
        ICultureTermQueryRepository cultureTermRepository, CancellationToken cancellationToken)
    {
        List<CultureTerm>? cultureTerms = null;
        if (!isNew)
        {
            Dictionary<int, List<CultureTerm>> dicTerms = await cultureTermRepository.GetTerms(typeof(TEntity).Name, [subjectId], cancellationToken);
            dicTerms.TryGetValue(subjectId, out cultureTerms);
        }
        foreach (string field in CultureFields)
        {
            _ = ReflectionField.GetValue(dto, field, out object? term);
            if (isNew)
            {
                await AddNewCultureTerm(subjectId, field, term, requesterUser, cultureTermCommandRepository, cancellationToken);
            }
            else
            {
                CultureTerm? cultureTerm = cultureTerms?.FirstOrDefault(t => t.SubjectField == field);
                if (cultureTerm != null)
                {
                    cultureTerm.Term = term?.ToString() ?? "";
                    cultureTermCommandRepository.Update(cultureTerm);
                }
                else
                {
                    await AddNewCultureTerm(subjectId, field, term, requesterUser, cultureTermCommandRepository, cancellationToken);
                }
            }
        }
        await cultureTermCommandRepository.UnitOfWork.SaveChangesAsync(cancellationToken);
    }

    private static async Task AddNewCultureTerm(int subjectId, string field, object? term, 
        IRequesterUser requesterUser, 
        ICommandRepository<CultureTerm, int> cultureTermCommandRepository, CancellationToken cancellationToken)
    {
        await cultureTermCommandRepository.AddAsync(new CultureTerm
        {
            LanguageId = await requesterUser.GetLangIdAsync(cancellationToken),
            SubjectTitle = typeof(TEntity).Name,
            SubjectField = field,
            SubjectId = subjectId,
            Term = term?.ToString() ?? ""
        }, cancellationToken);
    }
}
