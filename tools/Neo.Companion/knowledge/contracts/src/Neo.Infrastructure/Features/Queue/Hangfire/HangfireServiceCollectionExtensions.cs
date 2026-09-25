using Neo.Application.Features.Queue;
using Neo.Domain.Features.Telementry;
using Hangfire;
using Hangfire.Redis.StackExchange;
using Hangfire.SqlServer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Neo.Infrastructure.Features.Queue.Hangfire;

public static class HangfireServiceCollectionExtensions
{
    public static IServiceCollection AddNeoHangfire(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection("Hangfire").Get<HangfireOptions>()
                      ?? throw new InvalidOperationException("Hangfire configuration section is missing");

        if (string.IsNullOrWhiteSpace(options.ConnectionString) || options.WorkerCount < 1
            || options.Queues is null || options.Queues.Length == 0
            || options.Queues.Any(q => string.IsNullOrWhiteSpace(q) || q.Any(c => !(c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-')))
            || !(string.Equals(options.Storage, "SqlServer", StringComparison.OrdinalIgnoreCase) || string.Equals(options.Storage, "Redis", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Hangfire requires SqlServer or Redis storage, a connection string, a positive worker count and valid queue names.");

        services.AddHangfire(config =>
        {
            if (options.Storage.Equals("SqlServer", StringComparison.OrdinalIgnoreCase))
            {
                config.UseSqlServerStorage(options.ConnectionString, new SqlServerStorageOptions
                {
                    QueuePollInterval = TimeSpan.FromSeconds(15),
                    SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
                });
            }
            else if (options.Storage.Equals("Redis", StringComparison.OrdinalIgnoreCase))
            {
                config.UseRedisStorage(options.ConnectionString, new RedisStorageOptions
                {
                    Prefix = options.RedisPrefix
                });
            }

            config.UseSimpleAssemblyNameTypeSerializer()
                  .UseRecommendedSerializerSettings();
        });

        services.AddHangfireServer(serverOptions =>
        {
            serverOptions.WorkerCount = options.WorkerCount;
            serverOptions.Queues = options.Queues;
        });


        services.AddScopedWithTelemetry<IJobExecuter, HangfireJobExecuter>();
        services.AddScopedWithTelemetry<IRecurringJobsManager, HangfireRecurringJobManager>();
        services.AddScoped<ICronJobManager, HangfireCronJobManager>();

        return services;
    }
}
