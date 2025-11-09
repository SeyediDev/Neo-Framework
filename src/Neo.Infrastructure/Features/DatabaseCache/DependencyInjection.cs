using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Neo.Common.Extensions;
using Neo.Domain.Features.DatabaseCache;

namespace Neo.Infrastructure.Features.DatabaseCache;

public static class DependencyInjection
{
    /// <summary>
    /// Extension Methods برای ثبت Database Caching در DI Container
    /// </summary>
    public static IServiceCollection AddDatabaseCaching(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var enableDatabaseCache = configuration["DatabaseCache:Enabled"]?.ToBooleanOrDefault(false) ?? false;

        if (!enableDatabaseCache)
        {
            // کش غیرفعال است - استفاده از No-Op implementation
            services.AddSingleton<IDatabaseCache, NoOpDatabaseCache>();
            return services;
        }

        // کش فعال است
        services.AddMemoryCache();
        services.AddSingleton<IDatabaseCache, DatabaseCache>();

        // Interceptor برای Auto Invalidation
        services.AddScoped<CacheInvalidationInterceptor>();

        return services;
    }
}
