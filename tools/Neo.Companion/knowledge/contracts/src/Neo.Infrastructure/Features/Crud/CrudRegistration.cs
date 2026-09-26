using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Neo.Application.Features.Crud;
using Neo.Domain.Entities.Base;
using Neo.Domain.Features.Concurrency;

namespace Neo.Infrastructure.Features.Crud;

public sealed record CrudResourceRegistration(Type DefinitionType, Type ContextType,
    Func<IServiceProvider,IReadOnlyList<string>> Validate, Func<IServiceProvider,ICrudDefinition> Definition,
    Func<IServiceProvider,CancellationToken,Task<bool>> CanConnect);

public static class CrudRegistration
{
    public static IServiceCollection AddNeoCrudResource<TContext,TDefinition,TCreate,TUpdate,TRead,TEntity,TKey>(this IServiceCollection services)
        where TContext : DbContext
        where TDefinition : CrudDefinition<TCreate,TUpdate,TRead,TEntity,TKey>
        where TCreate : class where TUpdate : class where TRead : class
        where TEntity : class,IEntity<TKey>,IConcurrencyVersion where TKey : struct
    {
        if (services.Any(x=>x.ServiceType==typeof(ICrudService<TDefinition,TCreate,TUpdate,TRead,TKey>)))
            throw new InvalidOperationException("This resource definition was already registered.");
        services.TryAddScoped<TDefinition>();
        services.AddScoped<EfCrudService<TContext,TDefinition,TCreate,TUpdate,TRead,TEntity,TKey>>();
        services.AddScoped<ICrudService<TDefinition,TCreate,TUpdate,TRead,TKey>>(sp=>sp.GetRequiredService<EfCrudService<TContext,TDefinition,TCreate,TUpdate,TRead,TEntity,TKey>>());
        services.AddSingleton(new CrudResourceRegistration(typeof(TDefinition),typeof(TContext),
            sp=>sp.GetRequiredService<EfCrudService<TContext,TDefinition,TCreate,TUpdate,TRead,TEntity,TKey>>().ValidateConfiguration(),
            sp=>sp.GetRequiredService<TDefinition>(),
            (sp,ct)=>sp.GetRequiredService<TContext>().Database.CanConnectAsync(ct)));
        return services;
    }
}
