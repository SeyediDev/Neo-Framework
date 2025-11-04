using Neo.Application.Features.GenericEntity.GenericService;
using Neo.Domain.Dto;
using Neo.Domain.Entities.Base;
using Neo.Domain.Features.Multilingual;

namespace Neo.Application.Features.GenericEntity.Commands;

public record CreateGenericEntityCommand<TDto, TEntity, TKey>
    : IRequest<TKey?>
    where TDto : class, IDto<TKey>, new()
    where TEntity : class, IEntity<TKey>, new()
    where TKey : struct
{
    public required TDto Dto { get; set; }
    public Func<TEntity, Task>? BeforeCreate { get; set; }
    public Func<TEntity, Task>? AfterCreate { get; set; }
}

public class CreateGenericEntityCommandValidator<TDto, TEntity, TKey>
    : AbstractValidator<CreateGenericEntityCommand<TDto, TEntity, TKey>>
    where TDto : class, IDto<TKey>, new()
    where TEntity : class, IEntity<TKey>, new()
    where TKey : struct
{
    public CreateGenericEntityCommandValidator(IMultiLingualService multiLingual)
    {
        RuleFor(x => x.Dto).NotEmpty().WithMessage(multiLingual.GetMessage("Required"));
    }
}

public interface ICreateGenericEntityCommandHandler<TDto, TEntity, TKey>
    : IGenericServiceCommandHandlerBase<CreateGenericEntityCommand<TDto, TEntity, TKey>, TKey?>
    where TDto : class, IDto<TKey>, new()
    where TEntity : class, IEntity<TKey>, new()
    where TKey : struct
{
}

public class CreateGenericEntityCommandHandler<TDto, TEntity, TKey>(ICommandRepository<TEntity, TKey> repository,
    IGenericServiceHandler genericService)
    : GenericServiceCommandHandlerBase<CreateGenericEntityCommand<TDto, TEntity, TKey>, TKey?>(genericService),
        ICreateGenericEntityCommandHandler<TDto, TEntity, TKey>
    where TDto : class, IDto<TKey>, new()
    where TEntity : class, IEntity<TKey>, new()
    where TKey : struct
{
    protected override async Task<TKey?> Handle(CreateGenericEntityCommand<TDto, TEntity, TKey> request, CancellationToken cancellationToken)
    {
        TEntity entity = new();
        request.Dto.Adapt(entity);
        if (request.BeforeCreate != null)
        {
            await request.BeforeCreate(entity);
        }

        repository.Add(entity);
        await repository.UnitOfWork.SaveChangesAsync(cancellationToken);
        if (request.AfterCreate != null)
        {
            await request.AfterCreate(entity);
            await repository.UnitOfWork.SaveChangesAsync(cancellationToken);
        }
        return entity.Id;
    }
}
