using System.Text.Json;
using System.Text.Json.Serialization;

namespace Neo.Domain.Entities.Base;

/// <summary>
/// Factory to create JsonConverter for any StronglyTypedId<TValue>
/// </summary>
public class StronglyTypedIdJsonConverterFactory : JsonConverterFactory
{
	public override bool CanConvert(Type typeToConvert)
	{
		// Check if type implements IStronglyTypedId<T>
		return typeToConvert.GetInterfaces()
			.Any(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IStronglyTypedId<>));
	}

	public override JsonConverter? CreateConverter(Type typeToConvert, JsonSerializerOptions options)
	{
		var interfaceType = typeToConvert.GetInterfaces()
			.First(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IStronglyTypedId<>));

		var valueType = interfaceType.GetGenericArguments()[0];

		var converterType = typeof(StronglyTypedIdJsonConverter<,>)
			.MakeGenericType(typeToConvert, valueType);

		return (JsonConverter?)Activator.CreateInstance(converterType)!;
	}
}

/// <summary>
/// Generic converter for a specific StronglyTypedId
/// </summary>
public class StronglyTypedIdJsonConverter<TId, TValue> : JsonConverter<TId>
	where TId : struct, IStronglyTypedId<TValue>
	where TValue : struct
{
	public override TId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
	{
		if (typeof(TValue) == typeof(Guid))
		{
			var guid = reader.GetGuid();
			return (TId)Activator.CreateInstance(typeof(TId), guid)!;
		}

		if (typeof(TValue) == typeof(int))
		{
			var val = reader.GetInt32();
			return (TId)Activator.CreateInstance(typeof(TId), val)!;
		}

		if (typeof(TValue) == typeof(long))
		{
			var val = reader.GetInt64();
			return (TId)Activator.CreateInstance(typeof(TId), val)!;
		}

		throw new NotSupportedException($"Unsupported TValue type: {typeof(TValue)}");
	}

	public override void Write(Utf8JsonWriter writer, TId value, JsonSerializerOptions options)
	{
		switch (value.Value)
		{
			case int i: writer.WriteNumberValue(i); break;
			case long l: writer.WriteNumberValue(l); break;
			case Guid g: writer.WriteStringValue(g); break;
			default:
				throw new NotSupportedException($"Unsupported TValue type: {typeof(TValue)}");
		}
	}
}
