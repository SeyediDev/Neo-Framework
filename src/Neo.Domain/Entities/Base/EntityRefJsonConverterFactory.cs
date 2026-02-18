using System.Text.Json;
using System.Text.Json.Serialization;

namespace Neo.Domain.Entities.Base;

public sealed class EntityRefJsonConverter<TEntity, TValue>
	: JsonConverter<EntityRef<TEntity, TValue>>
	where TValue : struct
{
	public override EntityRef<TEntity, TValue> Read(
		ref Utf8JsonReader reader,
		Type typeToConvert,
		JsonSerializerOptions options)
	{
		// مقدار primitive خوانده می‌شود
		var value = JsonSerializer.Deserialize<TValue>(ref reader, options);
		return new EntityRef<TEntity, TValue>(value);
	}

	public override void Write(
		Utf8JsonWriter writer,
		EntityRef<TEntity, TValue> value,
		JsonSerializerOptions options)
	{
		JsonSerializer.Serialize(writer, value.Value, options);
	}
}

public sealed class EntityRefJsonConverterFactory : JsonConverterFactory
{
	public override bool CanConvert(Type typeToConvert)
	{
		var actual = Nullable.GetUnderlyingType(typeToConvert) ?? typeToConvert;

		return actual.IsGenericType &&
			   actual.GetGenericTypeDefinition() == typeof(EntityRef<,>);
	}

	public override JsonConverter CreateConverter(
		Type typeToConvert,
		JsonSerializerOptions options)
	{
		var isNullable = Nullable.GetUnderlyingType(typeToConvert) != null;
		var actual = Nullable.GetUnderlyingType(typeToConvert) ?? typeToConvert;

		var args = actual.GetGenericArguments();
		var entityType = args[0];
		var valueType = args[1];

		var converterType = typeof(EntityRefJsonConverter<,>)
			.MakeGenericType(entityType, valueType);

		var converter = (JsonConverter)Activator.CreateInstance(converterType)!;

		if (isNullable)
		{
			return (JsonConverter)Activator.CreateInstance(
				typeof(NullableConverterWrapper<>)
					.MakeGenericType(actual),
				converter
			)!;
		}

		return converter;
	}

	private sealed class NullableConverterWrapper<T>
		: JsonConverter<T?>
		where T : struct
	{
		private readonly JsonConverter<T> _inner;

		public NullableConverterWrapper(JsonConverter inner)
		{
			_inner = (JsonConverter<T>)inner;
		}

		public override T? Read(ref Utf8JsonReader reader,
								Type typeToConvert,
								JsonSerializerOptions options)
		{
			if (reader.TokenType == JsonTokenType.Null)
				return null;

			return _inner.Read(ref reader, typeof(T), options);
		}

		public override void Write(Utf8JsonWriter writer,
								   T? value,
								   JsonSerializerOptions options)
		{
			if (!value.HasValue)
			{
				writer.WriteNullValue();
				return;
			}

			_inner.Write(writer, value.Value, options);
		}
	}
}
