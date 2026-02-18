using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Neo.Domain.Entities.Base;

namespace Neo.Infrastructure.Data.Repository.Ef;

public class StronglyTypedIdValueConverter<TId, TValue>
	: ValueConverter<TId, TValue>
	where TId : struct, IStronglyTypedId<TValue>
{
	public StronglyTypedIdValueConverter()
		: base(
			id => id.Value,
			value => (TId)Activator.CreateInstance(typeof(TId), value)!)
	{
	}
}

public class NullableStronglyTypedIdValueConverter<TId, TValue>
	: ValueConverter<TId?, TValue?>
	where TId : struct, IStronglyTypedId<TValue>
{
	public NullableStronglyTypedIdValueConverter()
		: base(
			id => id.HasValue ? id.Value.Value : default,
			value => value != null
				? (TId?)Activator.CreateInstance(typeof(TId), value)!
				: null)
	{
	}
}

public static class StronglyTypedIdConverters
{
	public static ValueConverter CreateConverter(Type idType)
	{
		// If idType is Nullable<SomeStronglyTypedId>, unwrap it first
		var underlyingNullable = Nullable.GetUnderlyingType(idType);
		var typeToInspect = underlyingNullable ?? idType;

		var iface = typeToInspect.GetInterfaces()
			.First(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IStronglyTypedId<>));

		var valueType = iface.GetGenericArguments()[0];

		if (underlyingNullable != null)
		{
			var converterType = typeof(NullableStronglyTypedIdValueConverter<,>)
				.MakeGenericType(typeToInspect, valueType);
			return (ValueConverter)Activator.CreateInstance(converterType)!;
		}
		else
		{
			var converterType = typeof(StronglyTypedIdValueConverter<,>)
				.MakeGenericType(idType, valueType);
			return (ValueConverter)Activator.CreateInstance(converterType)!;
		}
	}
}
