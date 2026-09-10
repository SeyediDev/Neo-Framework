using System.Security.Claims;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Neo.Domain.Entities.Common;
using Neo.Domain.Features.Client;
using Neo.Domain.Features.Telementry;

namespace Neo.Companion.TelemetryDemo;

public static class DemoServices
{
    public const string SourceName = "Neo.Companion.TelemetryDemo";

    public static ServiceProvider Create(string mode)
    {
        if (mode is not ("manual" or "attribute")) throw new ArgumentException("Use manual or attribute.", nameof(mode));
        var services = new ServiceCollection();
        services.AddSingleton<IOptions<TelemetryOptions>>(Options.Create(new TelemetryOptions
            { ApplicationName = SourceName, ApplicationVersion = "0.2.0" }));
        services.AddSingleton<ILogger<TelementryObject>>(NullLogger<TelementryObject>.Instance);
        services.AddScoped<IRequesterUser, DemoRequester>();
        services.AddScoped<ITelementryObject, TelementryObject>();
        services.AddScoped<ITelementryBehaviour, TelementryBehaviour>();
        services.AddScoped<ProductLookup>();
        if (mode == "manual") services.AddScoped<IProductLookup, ManualProductLookup>();
        else services.AddScopedWithTelemetry<IProductLookup, ProductLookup>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }
}

public sealed class DemoRequester : IRequesterUser
{
    public UserId? Id { get; set; }
    public string Platform => "demo";
    public string? AppName => DemoServices.SourceName;
    public string? Lang => "en";
    public string? Mobile => null;
    public string? CorrelationId => "local-demo";
    public Dictionary<string, object> Properties { get; set; } = [];
    public Task<LanguageId> GetLangIdAsync(CancellationToken cancellationToken = default) => Task.FromResult(default(LanguageId)!);
    public List<Claim> Claims() => [];
    public bool? IsInRole(string role) => false;
}
