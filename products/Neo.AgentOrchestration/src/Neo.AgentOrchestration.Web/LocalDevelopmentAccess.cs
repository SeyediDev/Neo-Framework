using System.Net;

namespace Neo.AgentOrchestration.Web;

public static class LocalDevelopmentAccess
{
    public static bool IsAllowed(HttpContext context)
    {
        var environment = context.RequestServices.GetRequiredService<IHostEnvironment>();
        var configuration = context.RequestServices.GetRequiredService<IConfiguration>();
        return environment.IsDevelopment()
            && string.Equals(configuration["NEO_LOCAL_DEVELOPMENT"], "true", StringComparison.OrdinalIgnoreCase)
            && context.Connection.RemoteIpAddress is { } address && IPAddress.IsLoopback(address)
            && !context.Request.Headers.ContainsKey("Forwarded")
            && !context.Request.Headers.ContainsKey("X-Forwarded-For");
    }
}
