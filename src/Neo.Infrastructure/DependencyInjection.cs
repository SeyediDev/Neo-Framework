using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Neo.Application.Features.Queue;
using Neo.Domain.Features.Captchas;
using Neo.Domain.Features.Client;
using Neo.Domain.Features.Integrations;
using Neo.Domain.Features.Sms;
using Neo.Domain.Features.Telementry;
using Neo.Infrastructure.Data.Interceptors;
using Neo.Infrastructure.Features.Captchas;
using Neo.Infrastructure.Features.Client;
using Neo.Infrastructure.Features.Integrations;
using Neo.Infrastructure.Features.Queue;
using Neo.Infrastructure.Features.ServiceCaller;
using Neo.Infrastructure.Features.Sms;

namespace Neo.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddNeoInfrastructureServices(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddScoped<ISaveChangesInterceptor, AuditableEntityInterceptor>();
        services.AddScoped<ISaveChangesInterceptor, DispatchDomainEventsInterceptor>();
        services.AddHttpClient();
        
        AddFeatureServices(services, environment);
        services.AddNeoAuthorization(configuration);
        
        services.AddSingleton(TimeProvider.System);
        
        return services;
    }
    
    private static void AddFeatureServices(IServiceCollection services, IHostEnvironment environment)
    {
        services.AddScoped<IOtpService, OtpService>();
        services.AddScoped<ISmsService, SmsService>();
        services.AddScoped<IExternalApiService, ExternalApiService>();
        
        //services.AddScoped<IUserService<int>, DummyUserService>();
        services.AddScoped<IClientProfileReader, ClientProfileReaderFromConfig>();
        
        services.AddScoped<ICaptchaProvider, CaptchaService>();

        services.AddScopedWithTelemetry<IRegisterRecurringJobs, RegisterRecurringJobs>();

        services.AddTransient<LoggingHandler>();
    }
}
