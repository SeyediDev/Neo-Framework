using Asp.Versioning;
using Neo.Endpoint.Controller;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using NSwag;
using NSwag.Generation.Processors.Security;

namespace Neo.Endpoint;

public static class DependencyInjection
{
    public static IServiceCollection AddNeoControllerServices(this IServiceCollection services, string apiName)
    {
        services.AddExceptionHandler<CustomExceptionHandler>();
        // Customize default API behavior
        services.Configure<ApiBehaviorOptions>(options =>
         options.SuppressModelStateInvalidFilter = true);

        services.AddControllers();
        services.Configure<RouteOptions>(options => options.LowercaseUrls = true);


        services.AddApiVersioning(options =>
        {
            options.DefaultApiVersion = new ApiVersion(1);
            options.ReportApiVersions = true;
            options.AssumeDefaultVersionWhenUnspecified = true;
            options.ApiVersionReader = ApiVersionReader.Combine(
                new UrlSegmentApiVersionReader(),
                new HeaderApiVersionReader("X-Api-Version"));
        })
       .AddMvc() // This is needed for controllers
       .AddApiExplorer(options =>
       {
           options.GroupNameFormat = "'v'V";
           options.SubstituteApiVersionInUrl = true;
       })
       ;
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
}
