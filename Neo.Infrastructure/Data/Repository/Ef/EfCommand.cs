using Neo.Common.Extensions;
using Neo.Domain.Repository;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;

namespace Neo.Infrastructure.Data.Repository.Ef;

public class EfCommand<TEntity, TConfig, TUnitOfWork>(TConfig config, TUnitOfWork uow,
    ILogger<EfCommand<TEntity, TConfig, TUnitOfWork>> logger) : ICommand<TEntity, TConfig>
    where TEntity : class
    where TConfig : ICommandConfig
    where TUnitOfWork : IUnitOfWork
{
    public async Task Run(TEntity entity)
    {
        var members = typeof(TEntity).GetMembers();
        string names = "";
        foreach (var member in members)
            names += $"{(!string.IsNullOrEmpty(names) ? "," : "")}@{member.Name}";
        var values = members.Select(member =>
            {
                SqlParameter? p = null;
                object? value = null;
                try
                {
                    value = member.GetValue(entity);
                    p = new SqlParameter($"@{member.Name}", value);
                }
                catch (Exception ex)
                {
                    logger?.LogError(ex, "Execute {configName} {name} {value}", config.Name, member.Name, value?.ToString());
                }
                return p;
            }).ToArray();

        logger?.LogInformation("Execute {configName} {names} {values}", config.Name, names, values.Select(v => v?.SqlValue?.ToString()));

        await uow.ExecuteSqlRawCommandAsync($"Execute {config.Name} {names} ", values!);
        logger?.LogInformation("Executed {configName} {names} {values}", config.Name, names, values.Select(v => v?.SqlValue?.ToString()));
    }
}
