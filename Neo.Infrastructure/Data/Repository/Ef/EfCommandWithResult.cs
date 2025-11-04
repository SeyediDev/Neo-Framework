using Neo.Common.Extensions;
using Neo.Domain.Entities.Base;
using Neo.Domain.Repository;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Neo.Infrastructure.Data.Repository.Ef;

public class EfCommandWithResult<TResult, TCommand, TConfig, TUnitOfWork>(
    TConfig config, TUnitOfWork uow,
    ILogger<EfCommandWithResult<TResult, TCommand, TConfig, TUnitOfWork>> logger)
    : EfRepositoryBase<TResult, int>(uow), ICommandWithResult<TResult, TCommand, TConfig>
    where TCommand : class
    where TResult : class, IEntity<int>, new()
    where TConfig : ICommandConfig
    where TUnitOfWork : IUnitOfWork
{
    public IQueryable<TResult> Run(TCommand entity)
    {
        var members = typeof(TCommand).GetMembers();
        string names = "";
        string values = "";
        foreach (var member in members)
        {
            names += $"{(!string.IsNullOrEmpty(names) ? "," : "")}@{member.Name}";
            values += $"{(!string.IsNullOrEmpty(values) ? "," : "")}@{member.GetValue(entity)}";
        }

        logger?.LogInformation("Execute {configName} {names} {values}", config.Name, names, values);
        if (_dbSet == null)
        {
            return null!;
        }

        IQueryable<TResult> result = _dbSet.FromSql($"{config.Name} {names} {values}");
        logger?.LogInformation("Executed {configName} {names} {values} {@Result}",
            config.Name, names, values, result);
        return result;
    }
}
