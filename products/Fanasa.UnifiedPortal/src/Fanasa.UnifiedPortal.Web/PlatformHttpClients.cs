using Microsoft.Extensions.DependencyInjection;

namespace Fanasa.UnifiedPortal.Web;

public static class PlatformHttpClients
{
    public static IServiceCollection AddFanasaPlatformHttpClients(this IServiceCollection services)
    {
        // The token POST contains a client secret; a 307/308 must never replay it
        // at another endpoint. Catalog requests must likewise remain at their
        // configured authority rather than follow an unexpected login/redirect.
        Add("platform-control-token", TimeSpan.FromSeconds(15));
        Add("platform-control-catalog", TimeSpan.FromSeconds(15));
        Add("platform-control-discovery", TimeSpan.FromSeconds(30));
        return services;

        void Add(string name, TimeSpan timeout) => services.AddHttpClient(name, client => client.Timeout = timeout)
            .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
    }
}
