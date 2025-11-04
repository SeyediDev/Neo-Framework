using Neo.Application.Features.GenericEntity.GenericService;
using Neo.Domain.Entities.Base;
using Neo.Domain.Features.Multilingual;

namespace Neo.Application.Features.GenericEntity.Commands;

public record DeleteGenericEntityCommand<TEntity, TKey>
    : IRequest<Unit>
    where TEntity : class, IEntity<TKey>, new()
    where TKey : struct
{
    public required TKey Id { get; set; }
}

public class DeleteGenericEntityCommandValidator<TEntity, TKey>
    : AbstractValidator<DeleteGenericEntityCommand<TEntity, TKey>>
    where TEntity : class, IEntity<TKey>, new()
    where TKey : struct
{
    public DeleteGenericEntityCommandValidator(IMultiLingualService multiLingual)
    {
        RuleFor(x => x.Id).NotEmpty().WithMessage(multiLingual.GetMessage("Required"));
    }
}


public interface IDeleteGenericEntityCommandHandler<TEntity, TKey>
    : IGenericServiceCommandHandlerBase<DeleteGenericEntityCommand<TEntity, TKey>, Unit>
    where TEntity : class, IEntity<TKey>, new()
    where TKey : struct
{
}

public class DeleteGenericEntityCommandHandler<TEntity, TKey>(ICommandRepository<TEntity, TKey> repository,
    IGenericServiceHandler genericService)
    : GenericServiceCommandHandlerBase<DeleteGenericEntityCommand<TEntity, TKey>, Unit>(genericService),
        IDeleteGenericEntityCommandHandler<TEntity, TKey>
    where TEntity : class, IEntity<TKey>, new()
    where TKey : struct
{
    protected override async Task<Unit> Handle(DeleteGenericEntityCommand<TEntity, TKey> request, CancellationToken cancellationToken)
    {
        var response = await repository.RemoveAsync(request.Id);
        if (response is true)
        {
            await repository.UnitOfWork.SaveChangesAsync(cancellationToken);
        }
        return Unit.Value;
    }
}
