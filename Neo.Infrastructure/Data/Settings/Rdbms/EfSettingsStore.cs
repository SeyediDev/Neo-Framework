using Neo.Common.Extensions;
using Neo.Domain.Entities.Common;
using Neo.Domain.Features;
using Microsoft.EntityFrameworkCore;

namespace Neo.Infrastructure.Data.Settings.Rdbms;

public class EfSettingsStore(SettingDbContext settingDbContext) : ISettingService
{
    public async Task<string?> GetValueAsync(string key, CancellationToken cancellationToken)
    {
        Setting? setting = await settingDbContext.Settings.FirstOrDefaultAsync(x => x.Key == key, cancellationToken);
        return setting?.Value;
    }

    public async Task SetValueAsync(string key, string value, CancellationToken cancellationToken)
    {
        var setting = await settingDbContext.Settings.FirstOrDefaultAsync(x => x.Key == key, cancellationToken);
        if (setting == null)
        {
            setting = new Setting()
            {
                Key = key,
                Value = value
            };
            await settingDbContext.Settings.AddAsync(setting, cancellationToken);
        }
        else
        {
            setting.Value = value;
        }
        await settingDbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<T?> GetValueAsync<T>(string key, CancellationToken cancellationToken = default)
    {
        var d = await GetValueAsync(key, cancellationToken);
        return d is not null ? d.FromJson<T>() : default;
    }

    public async Task SetValueAsync<T>(string key, T value, CancellationToken cancellationToken = default)
    {
        await SetValueAsync(key, value.ToJson(), cancellationToken);
    }
}