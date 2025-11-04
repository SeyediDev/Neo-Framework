using Neo.Application.Features.GenericEntity.GenericService;
using Neo.Application.Models;
using Neo.Domain.Dto;
using Neo.Domain.Entities.Base;
using Neo.Domain.Features.Multilingual;
using System.Linq.Expressions;

namespace Neo.Application.Features.GenericEntity.Queries;

public record GetAllGenericEntityCommand<TDto, TEntity, TKey>
    : PaginationQuery, IRequest<GetAllGenericEntityResponse<TDto, TKey>>
    where TDto : class, IDto<TKey>, new()
    where TEntity : class, IEntity<TKey>, new()
    where TKey : struct
{
    public Expression<Func<TEntity, bool>>? Predicate { get; set; }
    public Func<IQueryable<TEntity>, IOrderedQueryable<TEntity>>? OrderBy { get; set; }
}

public record GetAllGenericEntityResponse<TDto, TKey> : PaginationResponse<TDto>
    where TDto : class, IDto<TKey>, new()
    where TKey : struct
{
}

public class GetAllGenericEntityCommandValidator<TDto, TEntity, TKey>
    : AbstractValidator<GetGenericEntityCommand<TDto, TEntity, TKey>>
    where TDto : class, IDto<TKey>, new()
    where TEntity : class, IEntity<TKey>, new()
    where TKey : struct
{
    public GetAllGenericEntityCommandValidator(IMultiLingualService multiLingual)
    {
        RuleFor(x => x.Id).NotEmpty().WithMessage(multiLingual.GetMessage("Required"));
    }
}

public interface IGetAllGenericEntityCommandHandler<TDto, TEntity, TKey>
    : IGenericServiceCommandHandlerBase<GetAllGenericEntityCommand<TDto, TEntity, TKey>, GetAllGenericEntityResponse<TDto, TKey>>
    where TDto : class, IDto<TKey>, new()
    where TEntity : class, IEntity<TKey>, new()
    where TKey : struct
{
}

public class GetAllGenericEntityCommandHandler<TDto, TEntity, TKey>(IQueryRepository<TEntity, TKey> repository,
    IGenericServiceHandler genericService)
    : GenericServiceCommandHandlerBase<GetAllGenericEntityCommand<TDto, TEntity, TKey>, GetAllGenericEntityResponse<TDto, TKey>>(genericService),
        IGetAllGenericEntityCommandHandler<TDto, TEntity, TKey>
    where TDto : class, IDto<TKey>, new()
    where TEntity : class, IEntity<TKey>, new()
    where TKey : struct
{
    protected override async Task<GetAllGenericEntityResponse<TDto, TKey>?> Handle(GetAllGenericEntityCommand<TDto, TEntity, TKey> request, CancellationToken cancellationToken)
    {
        var items = await repository.GetAllAsync<TDto>(cancellationToken, request.Predicate, request.OrderBy);
        GetAllGenericEntityResponse<TDto, TKey> response = new()
        {
            Items = items
        };
        response.SetPagination(request);
        return response;
    }
}
