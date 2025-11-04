namespace Neo.Domain.Dto;

public interface IDto<TKey>
    where TKey : struct
{
    public TKey? Id { get; set; }
}

public abstract class BaseDtoClass<TKey> : IDto<TKey>
    where TKey : struct

{
    public TKey? Id { get; set; }
}

public abstract record BaseDto<TKey> : IDto<TKey>
    where TKey : struct

{
    public TKey? Id { get; set; }
}
