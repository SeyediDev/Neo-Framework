using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Neo.Endpoint.Infrastructure;

public static class ClientProfileSettingExtension
{
    public static IHostBuilder ConfigureAppClientProfileSetting(this IHostBuilder host)
    {
        host.ConfigureAppConfiguration((h, c) =>
        {
            c.AddJsonFile("appsettings.clientprofile.json", true, true);
        });
        return host;
    }
}