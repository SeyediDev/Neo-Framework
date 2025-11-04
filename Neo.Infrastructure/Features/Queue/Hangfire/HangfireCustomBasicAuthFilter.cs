using Hangfire.Dashboard;

namespace Neo.Infrastructure.Features.Queue.Hangfire;

// Basic Auth
internal class HangfireCustomBasicAuthFilter : IDashboardAuthorizationFilter
{
    public string User { get; init; } = default!;
    public string Pass { get; init; } = default!;

    public bool Authorize(DashboardContext context)
    {
        var httpContext = context.GetHttpContext();
        var header = httpContext.Request.Headers["Authorization"].ToString();

        if (string.IsNullOrEmpty(header)) return false;
        if (!header.StartsWith("Basic ")) return false;

        var encoded = header.Substring("Basic ".Length).Trim();
        var decoded = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(encoded));

        var parts = decoded.Split(':');
        if (parts.Length != 2) return false;

        return parts[0] == User && parts[1] == Pass;
    }
}
