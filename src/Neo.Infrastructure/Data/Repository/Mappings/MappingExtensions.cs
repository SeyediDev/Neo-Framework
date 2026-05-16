using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Neo.Infrastructure.Data.Repository.Mappings;

public static class MappingExtensions
{
    public static Task<PaginatedList<TDestination>> PaginatedListAsync<TDestination>(this IQueryable<TDestination> queryable, int pageNumber, int pageSize) where TDestination : class
        => PaginatedList<TDestination>.CreateAsync(queryable.AsNoTracking(), pageNumber, pageSize);

    public static Task<List<TDestination>> ProjectToListAsync<TDestination>(this IQueryable<TDestination> queryable, IConfigurationProvider configuration) where TDestination : class
        => queryable.AsNoTracking().ProjectToListAsync<TDestination>(configuration);
}
