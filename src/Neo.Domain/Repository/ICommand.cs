namespace Neo.Domain.Repository;

public interface ICommand<TCommand, TConfig>
    where TCommand : class
    where TConfig : ICommandConfig
{
    Task Run(TCommand entity);
}

public interface ICommandConfig
{
    string Name { get; }
}

public interface ICommandWithResult<TResult, TCommand, TConfig>
    where TResult : class
    where TCommand : class
    where TConfig : ICommandConfig
{
    IQueryable<TResult> Run(TCommand entity);
}
