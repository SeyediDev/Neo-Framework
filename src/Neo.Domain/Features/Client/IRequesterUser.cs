using System.Security.Claims;

namespace Neo.Domain.Features.Client;

public interface IRequesterUser
{
    int? Id { get; set; }
    string Platform { get; }
    string? AppName { get; }
    string? Lang { get; }
    Task<int> GetLangIdAsync(CancellationToken cancellationToken=default);
    string? Mobile { get; }
    string? CorrelationId { get; }
    string? TenantId { get; }
    List<Claim> Claims();
    Dictionary<string, object> Properties { get; set; }
    object? GetProperty(string key, Func<object>? generateValueMethod = null)
    {
        if (Properties.TryGetValue(key, out object? value))
        {
            return value;
        }
        else if (generateValueMethod is not null)
        {
            value = generateValueMethod();
            _ = Properties.TryAdd(key, value);
            return value;
        }
        return null;
    }

    void AddProperty(string key, object value)
    {
        _ = Properties.TryAdd(key, value);
    }

    void SetProperty(string key, object value)
    {
        if (Properties.ContainsKey(key))
        {
            Properties[key] = value;
        }
        else
        {
            _ = Properties.TryAdd(key, value);
        }
    }

    bool? IsInRole(string role);
}
