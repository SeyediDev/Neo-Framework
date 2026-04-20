using Microsoft.Extensions.Caching.Distributed;
using Neo.Common.Extensions;
using Neo.Domain.Features.Cache;
using Neo.Domain.Repository;

namespace Neo.Infrastructure.Features.Cache;

public class DbCacheService(
	IQueryRepositoryL<Domain.Entities.Common.Cache> repo, 
	ICommandRepositoryL<Domain.Entities.Common.Cache> command) 
	: ICacheService
{
    public T? Get<T>(string key)
    {
        var item = repo.FirstOrDefault(x => x.Key == key);
        if(item!=null )
        {
            return item.Value.FromJson<T>();
        }
        return default;
    }

    public void Set<T>(string key, T? value)
    {
		var item = new Domain.Entities.Common.Cache() { Key= key, Value = value?.ToJson()??"" };
		command.Add( item);
		command.UnitOfWork.SaveChanges();
	}

	public void Set<T>(string key, T? value, TimeSpan lifeTime)
    {
		var item = new Domain.Entities.Common.Cache() { Key = key, Value = value?.ToJson() ?? "" };
		command.Add( item);
		command.UnitOfWork.SaveChanges();
	}

	public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default)
    {
		var item = await repo.FirstOrDefaultAsync(x => x.Key == key, cancellationToken);
		if (item != null)
		{
			return item.Value.FromJson<T>();
		}
		return default;
	}

	public async Task SetAsync<T>(string key, T value, DistributedCacheEntryOptions option, CancellationToken cancellationToken = default)
    {
		var item = new Domain.Entities.Common.Cache() { Key = key, Value = value?.ToJson() ?? "" };
		await command.AddAsync(item, cancellationToken);
        await command.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
		var item = await repo.FirstOrDefaultAsync(x => x.Key == key, cancellationToken);
        if (item != null) 
        {
			await command.RemoveAsync(item.Id, cancellationToken);
			await command.SaveChangesAsync(cancellationToken);
		}
	}

    public async Task<string?> GetStringAsync(string key, CancellationToken cancellationToken = default)
    {
		var item = await repo.FirstOrDefaultAsync(x => x.Key == key, cancellationToken);
		if (item != null)
		{
			return item.Value;
		}
        return null;
	}

	public async Task SetStringAsync(string key, string value, TimeSpan lifeTime, CancellationToken cancellationToken = default)
    {
		var item = new Domain.Entities.Common.Cache() { Key = key, Value = value };
		await command.AddAsync(item, cancellationToken);
		await command.SaveChangesAsync(cancellationToken);
	}
}
