namespace Neo.Infrastructure.Features.DatabaseCache;

public class CachedItem<T>
{
    public T Data { get; set; } = default!;
    public string Version { get; set; } = string.Empty;
    public DateTime CachedAt { get; set; }
}