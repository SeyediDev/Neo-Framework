using Neo.Application.Features.GenericEntity.GenericService;
using Neo.Domain.Dto;
using Neo.Domain.Entities.Base;
using Neo.Domain.Features.Multilingual;

namespace Neo.Application.Features.GenericEntity.Commands;

public record UpdateGenericEntityCommand<TDto, TEntity, TKey>
    : IRequest<Unit>
    where TDto : class, IDto<TKey>, new()
    where TEntity : class, IEntity<TKey>, IDomainEventEntity, new()
    where TKey : struct
{
    public required TDto Dto { get; set; }
    public Func<TEntity, Task>? BeforeUpdate { get; set; }
    public Func<TEntity, Task>? AfterUpdate { get; set; }
    public List<BaseEvent>? DomainEvents { get; }
}

public class UpdateGenericEntityCommandValidator<TDto, TEntity, TKey>
    : AbstractValidator<UpdateGenericEntityCommand<TDto, TEntity, TKey>>
    where TDto : class, IDto<TKey>, new()
    where TEntity : class, IEntity<TKey>, IDomainEventEntity, new()
    where TKey : struct
{
    public UpdateGenericEntityCommandValidator(IMultiLingualService multiLingual)
    {
        RuleFor(x => x.Dto).NotEmpty().WithMessage(multiLingual.GetMessage("Required"));
        RuleFor(x => x.Dto.Id).NotEmpty().WithMessage(multiLingual.GetMessage("Required"));
    }
}

public interface IUpdateGenericEntityCommandHandler<TDto, TEntity, TKey>
    : IGenericServiceCommandHandlerBase<UpdateGenericEntityCommand<TDto, TEntity, TKey>, Unit>
    where TDto : class, IDto<TKey>, new()
    where TEntity : class, IEntity<TKey>, IDomainEventEntity, new()
    where TKey : struct
{
}

public class UpdateGenericEntityCommandHandler<TDto, TEntity, TKey>(ICommandRepository<TEntity, TKey> repository,
    IGenericServiceHandler genericService)
    : GenericServiceCommandHandlerBase<UpdateGenericEntityCommand<TDto, TEntity, TKey>, Unit>(genericService),
        IUpdateGenericEntityCommandHandler<TDto, TEntity, TKey>
    where TDto : class, IDto<TKey>, new()
    where TEntity : class, IEntity<TKey>, IDomainEventEntity, new()
    where TKey : struct
{
    protected override async Task<Unit> Handle(UpdateGenericEntityCommand<TDto, TEntity, TKey> request, CancellationToken cancellationToken)
    {
        TEntity? entity = await repository.GetAsync(request.Dto.Id is not null ? request.Dto.Id.Value : default, cancellationToken)
            ?? throw new NotFoundException(request.Dto.Id?.ToString() ?? "", typeof(TEntity).Name);
        request.Dto.Adapt(entity);
        if (request.DomainEvents is not null)
        {
            entity.AddDomainEvents(request.DomainEvents);
        }
        if (request.BeforeUpdate != null)
        {
            await request.BeforeUpdate(entity);
        }
        repository.Update(entity);
        _ = await repository.UnitOfWork.SaveChangesAsync(cancellationToken);
        if (request.AfterUpdate != null)
        {
            await request.AfterUpdate(entity);
            _ = await repository.UnitOfWork.SaveChangesAsync(cancellationToken);
        }
        return Unit.Value;
    }
}
