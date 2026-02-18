using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Neo.Domain.Entities.Base;

namespace Neo.Infrastructure.Data.Repository.Ef;

public class EntityRefValueConverter<TEntity, TValue>
	: ValueConverter<EntityRef<TEntity, TValue>, TValue>
	where TValue : struct
{
	public EntityRefValueConverter()
		: base(
			v => v.Value,
			v => new EntityRef<TEntity, TValue>(v))
	{ }
}
public class NullableEntityRefValueConverter<TEntity, TValue>
	: ValueConverter<EntityRef<TEntity, TValue>?, TValue?>
	where TValue : struct
{
	public NullableEntityRefValueConverter()
		: base(
			v => v.HasValue ? v.Value.Value : null,
			v => v.HasValue
				? new EntityRef<TEntity, TValue>(v.Value)
				: null)
	{ }
}
