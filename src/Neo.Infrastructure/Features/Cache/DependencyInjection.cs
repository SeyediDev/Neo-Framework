using Neo.Domain.Features.Cache;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Neo.Infrastructure.Features.Cache;

public static class DependencyInjection
{
    public static IServiceCollection AddNeoMemoryCacheServices(this IServiceCollection services)
    {
        services.AddMemoryCache();
        services.AddScoped<ICacheService, MemoryCacheService>();
        return services;
    }
    
    public static IServiceCollection AddNeoMemoryCacheServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddMemoryCache();
        services.AddScoped<ICacheService, MemoryCacheService>();
        return services;
    }
    
    public static IServiceCollection AddNeoRedisCacheServices(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<ICacheService, CacheService>();
        services.AddStackExchangeRedisCache(options => options.Configuration = configuration.GetConnectionString("Redis"));
        return services;
    }
}