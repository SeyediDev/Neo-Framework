using Neo.Domain.Features.Telementry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using Serilog;

namespace Neo.Infrastructure.Features.Telementry;

public static class DependencyInjection
{
    public static IServiceCollection AddNeoOpenTelementry(this IServiceCollection services, IConfiguration configuration)
    {
        TelemetryOptions openTelemetryOptions = configuration.Get<TelemetryOptions>() ?? new();
        _ = services.AddOpenTelemetry()
            .ConfigureResource(resource => resource
                .AddService(serviceName: openTelemetryOptions.ApplicationName, openTelemetryOptions.ApplicationVersion))
            .WithTracing(tracing =>
            {
                _ = tracing
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    //dotnet add package OpenTelemetry.Instrumentation.EntityFrameworkCore --prerelease
                    //prerelease است عملکردش را نمیدونم
                    .AddEntityFrameworkCoreInstrumentation(options =>
                    {
                        options.SetDbStatementForText = true; // متن کامل SQL در span قرار بگیره
                        options.SetDbStatementForStoredProcedure = true;
                    })
                    .AddSqlClientInstrumentation(options =>
                    {
                        options.SetDbStatementForText = true; // کوئری SQL رو هم نشون میده
                    })
                    .AddConsoleExporter()   // خروجی در کنسول
                    .AddOtlpExporter();
                if (string.IsNullOrEmpty(openTelemetryOptions.JaegerExporterHost))
                {
                    _ = tracing.AddJaegerExporter(o =>
                    {
                        o.AgentHost = openTelemetryOptions.JaegerExporterHost;// "localhost"; // Jaeger
                        o.AgentPort = openTelemetryOptions.JaegerExporterPort;// 6831;
                    });
                }
                if (string.IsNullOrEmpty(openTelemetryOptions.ZipkinExporterUri))
                {
                    _ = tracing.AddZipkinExporter(o =>
                    {
                        o.Endpoint = new Uri(openTelemetryOptions.ZipkinExporterUri); // Zipkin
                    });
                }
            })
            .WithMetrics(metrics =>
            {
                _ = metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddConsoleExporter();
            })
            //.WithLogging() با توجه به استفاده از سریلاگ نیازی به این نیست
            ;
        return services;
    }
    
    public static void AddNeoSerilog(this IHostBuilder builder)
    {
        builder.UseSerilog((context, loggerConfig) => loggerConfig.ReadFrom.Configuration(context.Configuration));
    }
}
