namespace Neo.Domain.Features;

public interface ISettingService
{
    Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default);
    Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default);
    Task<T?> GetValueAsync<T>(string key, CancellationToken cancellationToken = default);
    Task SetValueAsync<T>(string key, T value, CancellationToken cancellationToken = default);
}
