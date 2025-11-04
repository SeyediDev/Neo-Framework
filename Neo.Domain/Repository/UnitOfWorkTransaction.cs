namespace Neo.Domain.Repository;

public interface IUnitOfWorkTransaction
{
    Task TransactionBody(Func<CancellationToken, Task> business, CancellationToken cancellationToken, params IUnitOfWork[] uows);
}
public class UnitOfWorkTransaction : IUnitOfWorkTransaction
{
    public async Task TransactionBody(Func<CancellationToken, Task> business,
        CancellationToken cancellationToken, params IUnitOfWork[] uows)
    {
        if (uows == null)
        {
            throw new ArgumentNullException(nameof(uows));
        }
        if ((uows?.Length ?? 0) > 1)
        {
            // using TransactionScope scope = new();
            await Body(business, cancellationToken, uows!);
            //scope.Complete();
        }
        else
        {
            await Body(business, cancellationToken, uows!);
        }
    }

    private static async Task Body(Func<CancellationToken, Task> business,
        CancellationToken cancellationToken, IUnitOfWork[] uows)
    {
        foreach (IUnitOfWork uow in uows)
        {
            await uow.BeginTransactionAsync();
        }
        await business(cancellationToken);

        foreach (IUnitOfWork uow in uows)
        {
            try
            {
                _ = await uow.SaveChangesAsync(cancellationToken);
            }
            catch
            {
                await uow.RollbackTransactionAsync();
                throw;
            }
        }
        foreach (IUnitOfWork uow in uows)
        {
            await uow.CommitTransactionAsync();
        }
    }
}
