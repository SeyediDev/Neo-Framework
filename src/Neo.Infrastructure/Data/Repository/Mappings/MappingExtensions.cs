using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Neo.Domain.Repository;

namespace Neo.Infrastructure.Data.Repository.Mappings;

public static class MappingExtensions
{
	public static async Task<PaginatedList<TDestination>> PaginatedListAsync<TDestination>(
		this IQueryable<TDestination> queryable, int pageNumber, int pageSize)
		where TDestination : class
	{
		var count = await queryable.CountAsync();
		var items = await queryable.Skip((pageNumber - 1) * pageSize).Take(pageSize).ToListAsync();

		return new PaginatedList<TDestination>(items, count, pageNumber, pageSize);
	}

	public static Task<List<TDestination>> ProjectToListAsync<TDestination>(this IQueryable<TDestination> queryable, IConfigurationProvider configuration) where TDestination : class
        => queryable.AsNoTracking().ProjectToListAsync<TDestination>(configuration);
}
