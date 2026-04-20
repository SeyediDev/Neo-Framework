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
                    .AddConsoleExporter();   // خروجی در کنسول
                
                // OTLP Exporter - only enable if endpoint is configured
                if (!string.IsNullOrWhiteSpace(openTelemetryOptions.OtlpExporterEndpoint))
                {
                    _ = tracing.AddOtlpExporter(options =>
                    {
                        options.Endpoint = new Uri(openTelemetryOptions.OtlpExporterEndpoint);
                        // Protocol is set via environment variable or defaults to grpc
                        // For http/protobuf, set OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
                    });
                }
                
                if (!string.IsNullOrEmpty(openTelemetryOptions.JaegerExporterHost))
                {
					//               _ = tracing.AddJaegerExporter(o =>//AddJaegerExporter
					//{
					//                   o.AgentHost = openTelemetryOptions.JaegerExporterHost;// "localhost"; // Jaeger
					//                   o.AgentPort = openTelemetryOptions.JaegerExporterPort;// 6831;
					//               });
					_ = tracing.AddOtlpExporter(options =>
					{
						options.Endpoint = new Uri(openTelemetryOptions.JaegerExporterHost+":"+ openTelemetryOptions.JaegerExporterPort);
					});
				}
				if (!string.IsNullOrEmpty(openTelemetryOptions.ZipkinExporterUri))
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
                
                // OTLP Exporter for Metrics - only enable if endpoint is configured
                if (!string.IsNullOrWhiteSpace(openTelemetryOptions.OtlpExporterEndpoint))
                {
                    _ = metrics.AddOtlpExporter(options =>
                    {
                        options.Endpoint = new Uri(openTelemetryOptions.OtlpExporterEndpoint);
                    });
                }
            })
            //.WithLogging() با توجه به استفاده از سریلاگ نیازی به این نیست
            ;
        return services;
    }
    
    public static void AddNeoSerilog(this IHostBuilder builder)
    {
        builder.UseSerilog((context, loggerConfig) =>
        {
            loggerConfig.ReadFrom.Configuration(context.Configuration);
            
            // Self-monitoring: هر API لاگ‌های خودش را به خودش ارسال می‌کند
            // Get the current API URL for self-monitoring
            string monitoringApiUrl = string.Empty;
            
            // If not configured, use self-monitoring (send logs to the same API)
            var urls = context.Configuration["Urls"] ?? context.Configuration["Kestrel:Endpoints:Http:Url"];
            if (!string.IsNullOrWhiteSpace(urls))
            {
                var firstUrl = urls.Split(';')[0].Trim();
                if (firstUrl.StartsWith("http://") || firstUrl.StartsWith("https://"))
                {
                    monitoringApiUrl = firstUrl;
                }
                else if (int.TryParse(firstUrl, out var port))
                {
                    monitoringApiUrl = $"http://localhost:{port}";
                }
            }
                
            // Fallback: use ASPNETCORE_URLS environment variable
            if (string.IsNullOrWhiteSpace(monitoringApiUrl))
            {
                var aspnetcoreUrls = Environment.GetEnvironmentVariable("ASPNETCORE_URLS");
                if (!string.IsNullOrWhiteSpace(aspnetcoreUrls))
                {
                    monitoringApiUrl = aspnetcoreUrls.Split(';')[0].Trim();
                }
            }
            
            // Add sink to send logs to monitoring API (self-monitoring)
            if (!string.IsNullOrWhiteSpace(monitoringApiUrl))
            {
                var minLevel = context.Configuration.GetValue<Serilog.Events.LogEventLevel>(
                    "TelemetryOptions:MonitoringLogLevel", 
                    Serilog.Events.LogEventLevel.Information);
                
                loggerConfig.WriteTo.Sink(
                    new NeoMonitoringSerilogSink(monitoringApiUrl, null, minLevel),
                    minLevel);
            }
        });
    }
}
