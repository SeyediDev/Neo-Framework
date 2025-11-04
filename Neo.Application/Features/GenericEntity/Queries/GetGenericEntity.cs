using Neo.Application.Features.GenericEntity.GenericService;
using Neo.Domain.Dto;
using Neo.Domain.Entities.Base;
using Neo.Domain.Features.Multilingual;

namespace Neo.Application.Features.GenericEntity.Queries;

public record GetGenericEntityCommand<TDto, TEntity, TKey>
    : IRequest<TDto>
    where TDto : class, IDto<TKey>, new()
    where TEntity : class, IEntity<TKey>, new()
    where TKey : struct
{
    public required TKey Id { get; set; }
}

public class GetGenericEntityCommandValidator<TDto, TEntity, TKey>
    : AbstractValidator<GetGenericEntityCommand<TDto, TEntity, TKey>>
    where TDto : class, IDto<TKey>, new()
    where TEntity : class, IEntity<TKey>, new()
    where TKey : struct
{
    public GetGenericEntityCommandValidator(IMultiLingualService multiLingual)
    {
        RuleFor(x => x.Id).NotEmpty().WithMessage(multiLingual.GetMessage("Required"));
    }
}

public interface IGetGenericEntityCommandHandler<TDto, TEntity, TKey>
    : IGenericServiceCommandHandlerBase<GetGenericEntityCommand<TDto, TEntity, TKey>, TDto>
    where TDto : class, IDto<TKey>, new()
    where TEntity : class, IEntity<TKey>, new()
    where TKey : struct
{
}

public class GetGenericEntityCommandHandler<TDto, TEntity, TKey>(IQueryRepository<TEntity, TKey> repository,
    IGenericServiceHandler genericService)
    : GenericServiceCommandHandlerBase<GetGenericEntityCommand<TDto, TEntity, TKey>, TDto>(genericService),
        IGetGenericEntityCommandHandler<TDto, TEntity, TKey>
    where TDto : class, IDto<TKey>, new()
    where TEntity : class, IEntity<TKey>, new()
    where TKey : struct
{
    protected override async Task<TDto?> Handle(GetGenericEntityCommand<TDto, TEntity, TKey> request, CancellationToken cancellationToken)
    {
        TEntity? entity = await repository.GetByIdAsync(request.Id, cancellationToken);
        return entity.Adapt<TDto>();
    }
}
