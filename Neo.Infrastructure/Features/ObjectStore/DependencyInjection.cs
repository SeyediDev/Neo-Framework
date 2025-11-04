using Neo.Domain.Features.ObjectStore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Minio;

namespace Neo.Infrastructure.Features.ObjectStore;

public static class DependencyInjection
{
    public static IServiceCollection AddCandoMinIo(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddMinio(configureClient =>
                    configureClient.WithEndpoint(configuration["Minio:Url"]!, int.Parse(configuration["Minio:Port"]!))
                   .WithCredentials(configuration["Minio:AccessKey"]!, configuration["Minio:SecretKey"]!)
                   .WithSSL(false)
                   .Build());
        services.AddScoped<IObjectStoreService, ObjectStoreService>();
        return services;
    }
}
