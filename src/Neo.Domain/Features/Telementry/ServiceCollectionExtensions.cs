using Castle.DynamicProxy;
using Microsoft.Extensions.DependencyInjection;

namespace Neo.Domain.Features.Telementry;

public static class ServiceCollectionExtensions
{
    private static readonly ProxyGenerator _proxyGenerator = new();

    public static IServiceCollection AddScopedWithTelemetry<TService, TImpl>(this IServiceCollection services)
        where TService : class
        where TImpl : class, TService
    {
		services.AddScoped<TService, TImpl>();
		//services.AddScoped<TImpl>();
  //TODO Disable temperory
  //    services.AddScoped(sp =>
  //      {
  //          var impl = sp.GetRequiredService<TImpl>();
  //          var interceptor = sp.GetRequiredService<TelemetryInterceptor>();

  //          return _proxyGenerator.CreateInterfaceProxyWithTarget<TService>(impl, interceptor);
  //      });

        return services;
    }
}
