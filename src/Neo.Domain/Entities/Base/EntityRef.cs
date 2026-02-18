namespace Neo.Domain.Entities.Base;

public readonly record struct EntityRef<TEntity, TValue>
	where TValue : struct
{
	public TValue Value { get; }

	public EntityRef(TValue value)
	{
		Value = value;
	}

	public override string ToString() => Value.ToString()!;

	public static explicit operator TValue(EntityRef<TEntity, TValue> id)
		=> id.Value;

	public static implicit operator EntityRef<TEntity, TValue>(TValue value)
		=> new(value);
}
