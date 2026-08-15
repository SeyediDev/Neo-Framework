using Asp.Versioning;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Razor;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Neo.Endpoint.Controller;
using Neo.Endpoint.Controller.Api;
using Neo.Endpoint.Features.Monitoring.Hubs;
using Neo.Endpoint.Features.Monitoring.Models;
using Neo.Endpoint.Features.Monitoring.Services;
using Neo.Endpoint.Infrastructure;
using NSwag;
using NSwag.Generation.Processors.Security;

namespace Neo.Endpoint;

public static class DependencyInjection
{
    public static IServiceCollection AddNeoControllerServices(
        this IServiceCollection services, IConfiguration configuration, 
        string apiName, bool includeViews = false, IMvcBuilder? existingMvcBuilder = null)
    {
        services.AddExceptionHandler<CustomExceptionHandler>();
        
        // اضافه کردن MetricsCollectorService برای جمع‌آوری metrics
        services.AddHostedService<MetricsCollectorService>();
        // Customize default API behavior
        services.Configure<ApiBehaviorOptions>(options =>
         options.SuppressModelStateInvalidFilter = true);

		// Configure storage options from configuration
		services.Configure<MonitoringStorageOptions>(
			configuration.GetSection("Monitoring"));
		// Register SignalR
		services.AddSignalR();
		
		// Register monitoring services (stores, collectors, etc.)
		services.AddNeoMonitoringServices(configuration);
		
        // بررسی می‌کنیم که آیا قبلاً Controllers اضافه شده‌اند یا نه
		// اگر AddControllersWithViews قبلاً فراخوانی شده (که ITempDataDictionaryFactory را اضافه می‌کند)، نیازی به اضافه کردن دوباره نیست
		var tempDataFactoryRegistered = services.Any(s => 
            s.ServiceType == typeof(Microsoft.AspNetCore.Mvc.ViewFeatures.ITempDataDictionaryFactory));
        
        IMvcBuilder? mvcBuilder = existingMvcBuilder;
        
        if (mvcBuilder == null && !tempDataFactoryRegistered)
        {
            // اگر builder موجود نیست و services هم ثبت نشده‌اند، باید اضافه کنیم
            if (includeViews)
            {
                mvcBuilder = services.AddControllersWithViews();
            }
            else
            {
                mvcBuilder = services.AddControllers();
            }
        }
        else if (mvcBuilder == null && tempDataFactoryRegistered)
        {
            // اگر services ثبت شده اما builder نداریم، باید یک builder ایجاد کنیم
            // اما چون services قبلاً ثبت شده، این فقط یک reference برمی‌گرداند
            mvcBuilder = services.AddControllersWithViews();
        }
        services.Configure<RouteOptions>(options =>
        {
            options.LowercaseUrls = true;
        });


        // استفاده از API Versioning - AddMvc() فقط MVC را configure می‌کند و services را override نمی‌کند
        services.AddApiVersioning(options =>
        {
            options.DefaultApiVersion = new ApiVersion(1);
            options.ReportApiVersions = true;
            options.AssumeDefaultVersionWhenUnspecified = true;
            options.ApiVersionReader = ApiVersionReader.Combine(
                new UrlSegmentApiVersionReader(),
                new HeaderApiVersionReader("X-Api-Version"));
        })
       .AddMvc() // This is needed for controllers - doesn't override existing services
       .AddApiExplorer(options =>
       {
           options.GroupNameFormat = "'v'V";
           options.SubstituteApiVersionInUrl = true;
       });

        // Register apiVersion constraint for route templates
        services.Configure<RouteOptions>(options =>
        {
            if (!options.ConstraintMap.ContainsKey("apiVersion"))
            {
                options.ConstraintMap.Add("apiVersion", typeof(Asp.Versioning.Routing.ApiVersionRouteConstraint));
            }
        });
        services.AddEndpointsApiExplorer();

        services.AddOpenApiDocument((config, sp) =>
        {
            config.Title = apiName;
            //config.DocumentProcessors.Add(new GenericCrudDocumentProcessor());
            //config.OperationProcessors.Add(new GenericCrudDocumentFilter());

            // برای پشتیبانی از جنریک‌ها
            config.SchemaSettings.GenerateKnownTypes = true;
            config.SchemaSettings.SchemaType = NJsonSchema.SchemaType.OpenApi3;

            // تنظیمات خاص NSwag برای جنریک‌ها
            config.SchemaSettings.AllowReferencesWithProperties = true;
            config.SchemaSettings.FlattenInheritanceHierarchy = true;

            // Add JWT
            config.AddSecurity("JWT", [], new OpenApiSecurityScheme
            {
                Type = OpenApiSecuritySchemeType.ApiKey,
                Name = "Authorization",
                In = OpenApiSecurityApiKeyLocation.Header,
                Description = "Type into the textbox: Bearer {your JWT token}."
            });

            config.OperationProcessors.Add(new AspNetCoreOperationSecurityScopeProcessor("JWT"));
        });

        //services.AddRazorPages(); 
        return services;
    }

    /// <summary>
    /// اضافه کردن پشتیبانی از Views برای Monitoring
    /// این متد باید در API هایی که می‌خواهند Views را نمایش دهند فراخوانی شود
    /// </summary>
    public static IMvcBuilder AddNeoMonitoringViews(this IServiceCollection services, IWebHostEnvironment? environment = null)
    {
        // بررسی می‌کنیم که آیا قبلاً AddControllersWithViews فراخوانی شده یا نه
        // اگر AddControllers فراخوانی شده، باید آن را به AddControllersWithViews تبدیل کنیم
        var mvcBuilder = services.AddControllersWithViews();

        // اضافه کردن View Location Expander برای پیدا کردن Views در Neo.Endpoint
        services.Configure<RazorViewEngineOptions>(options =>
        {
            options.ViewLocationExpanders.Add(new NeoMonitoringViewLocationExpander());
        });

        // اضافه کردن مسیر Views از Neo.Endpoint برای Runtime Compilation
        if (environment != null && environment.IsDevelopment())
        {
            mvcBuilder.AddRazorRuntimeCompilation(options =>
            {
                // پاک کردن FileProvider های پیش‌فرض برای جلوگیری از کامپایل View های دیگر پروژه‌ها
                // فقط مسیرهای Neo.Endpoint را اضافه می‌کنیم
                options.FileProviders.Clear();
                
                // اضافه کردن مسیر Views از Neo.Endpoint
                var neoEndpointAssembly = typeof(MonitoringController).Assembly;
                var assemblyLocation = neoEndpointAssembly.Location;
                
                // اول بررسی مسیر output directory
                if (!string.IsNullOrEmpty(assemblyLocation))
                {
                    var outputViewsPath = Path.Combine(Path.GetDirectoryName(assemblyLocation) ?? "", "Views");
                    
                    if (Directory.Exists(outputViewsPath))
                    {
                        options.FileProviders.Add(new PhysicalFileProvider(outputViewsPath));
                    }
                }
                
                // سپس بررسی مسیر source directory (برای Development)
                // پیدا کردن مسیر source از assembly location
                if (!string.IsNullOrEmpty(assemblyLocation))
                {
                    // مسیر assembly: D:\Projects\Neo\src\Neo.Endpoint\bin\Debug\net10.0\Neo.Endpoint.dll
                    // مسیر project: D:\Projects\Neo\src\Neo.Endpoint
                    var assemblyDir = Path.GetDirectoryName(assemblyLocation) ?? ""; // bin\Debug\net10.0
                    
                    // استفاده از مسیر نسبی برای پیدا کردن Views (قابل اعتمادتر)
                    var relativeViewsPath = Path.GetFullPath(Path.Combine(assemblyDir, "..", "..", "..", "Views"));
                    
                    if (Directory.Exists(relativeViewsPath))
                    {
                        options.FileProviders.Add(new PhysicalFileProvider(relativeViewsPath));
                    }
                }
            });
        }

        // غیرفعال کردن BrowserLink برای Views Monitoring
        // BrowserLink فقط در Development فعال است و باعث خطاهای CSP می‌شود
        services.Configure<Microsoft.AspNetCore.Mvc.MvcOptions>(options =>
        {
            // BrowserLink به صورت خودکار در Development inject می‌شود
            // برای غیرفعال کردن باید از UseBrowserLink استفاده نکنیم
        });

		return mvcBuilder;
    }

    /// <summary>
    /// اضافه کردن Swagger UI با عنوان API در عنوان صفحه
    /// </summary>
    public static IApplicationBuilder UseNeoSwaggerUi(this IApplicationBuilder app, string apiName, string? path = null)
    {
        path ??= "swagger";
        
        app.UseSwaggerUi(options =>
        {
            options.DocumentTitle = apiName;
            options.Path = path;
            // تنظیم عنوان صفحه HTML
            var existingHeadContent = options.CustomHeadContent ?? "";
            // اگر title tag وجود ندارد، اضافه می‌کنیم
            if (!existingHeadContent.Contains("<title>", StringComparison.OrdinalIgnoreCase))
            {
                options.CustomHeadContent = $"<title>{apiName}</title>" + existingHeadContent;
            }
            else
            {
                // اگر title tag وجود دارد، با JavaScript آن را تغییر می‌دهیم
                options.CustomHeadContent = existingHeadContent + $@"
                    <script>
                        (function() {{
                            document.title = '{apiName}';
                            var titleElement = document.querySelector('title');
                            if (titleElement) {{
                                titleElement.textContent = '{apiName}';
                            }}
                        }})();
                    </script>
                ";
            }
        });
        
        return app;
    }

    /// <summary>
    /// اضافه کردن Swagger UI با عنوان API در عنوان صفحه (برای WebApplication)
    /// </summary>
    public static WebApplication UseNeoSwaggerUi(this WebApplication app, string apiName, string? path = null)
    {
        path ??= "swagger";
        
        app.UseSwaggerUi(options =>
        {
            options.DocumentTitle = apiName;
            options.Path = path;
            // تنظیم عنوان صفحه HTML
            var existingHeadContent = options.CustomHeadContent ?? "";
            // اگر title tag وجود ندارد، اضافه می‌کنیم
            if (!existingHeadContent.Contains("<title>", StringComparison.OrdinalIgnoreCase))
            {
                options.CustomHeadContent = $"<title>{apiName}</title>" + existingHeadContent;
            }
            else
            {
                // اگر title tag وجود دارد، با JavaScript آن را تغییر می‌دهیم
                options.CustomHeadContent = existingHeadContent + $@"
                    <script>
                        (function() {{
                            document.title = '{apiName}';
                            var titleElement = document.querySelector('title');
                            if (titleElement) {{
                                titleElement.textContent = '{apiName}';
                            }}
                        }})();
                    </script>
                ";
            }
        });
        
        return app;
    }

	private static bool _monitoringHubRegistered = false;
	private static readonly object _monitoringHubLock = new object();

	public static IEndpointRouteBuilder MapNeoEndpoints(this IEndpointRouteBuilder endpoints)
	{
		// Map API controllers from this assembly
		endpoints.MapControllers();

		// Prevent duplicate hub registration
		lock (_monitoringHubLock)
		{
			if (!_monitoringHubRegistered)
			{
				endpoints.MapHub<MonitoringHub>("/hubs/monitoring");
				_monitoringHubRegistered = true;
			}
		}

		return endpoints;
	}
	/// <summary>
	/// Add Neo monitoring services to the service collection
	/// </summary>
	public static IServiceCollection AddNeoMonitoringServices(
		this IServiceCollection services, 
		IConfiguration? configuration = null)
	{
		// Configure storage options from configuration if provided
		if (configuration != null)
		{
			services.Configure<MonitoringStorageOptions>(
				configuration.GetSection("Monitoring"));
		}
		else
		{
			// Use default options if configuration is not provided
			services.Configure<MonitoringStorageOptions>(options =>
			{
				// Default values are already set in MonitoringStorageOptions class
			});
		}

		// Register stores as singletons (shared state)
		services.AddSingleton<ILogStore, LogStore>();
		services.AddSingleton<IMetricsStore>(sp => 
		{
			var options = sp.GetRequiredService<IOptions<MonitoringStorageOptions>>();
			var logStore = sp.GetRequiredService<ILogStore>();
			return new MetricsStore(options, logStore);
		});
		services.AddSingleton<ITraceStore, TraceStore>();

		// Register collectors as hosted services
		services.AddHostedService<MetricsCollector>();
		services.AddHostedService<TraceCollector>();
		services.AddHostedService<MonitoringCleanupService>();
		services.AddHostedService<MonitoringBroadcaster>();

		// Register system metrics publisher (built-in metrics)
		services.AddHostedService<SystemMetricsPublisher>();

		// Register Serilog monitoring service
		// This adds MonitoringSerilogSink to capture logs
		services.AddHostedService<SerilogMonitoringService>();

		// OTLP endpoints are available via TracesController and MetricsController
		// POST /api/monitoring/traces/otlp
		// POST /api/monitoring/metrics/otlp
		// These endpoints receive telemetry data from external APIs

		return services;
	}
}
