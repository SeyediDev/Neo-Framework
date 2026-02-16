using System.Text.Json.Serialization;

namespace Neo.Domain.Entities.Base;

public interface IStronglyTypedId<T>
{
	T Value { get; }
}

[JsonConverter(typeof(StronglyTypedIdJsonConverterFactory))]
public record struct StronglyTypedId<TValue> : IStronglyTypedId<TValue>
	where TValue : struct
{
	public TValue Value { get; init; }

	public StronglyTypedId(TValue value)
	{
		Value = value;
	}

	public override string ToString() => Value.ToString()!;

	public static explicit operator TValue(StronglyTypedId<TValue> id) => id.Value;
}

public readonly record struct OrderId(int Value)
	: IStronglyTypedId<int>;