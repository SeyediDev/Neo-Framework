using Hangfire;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Neo.Infrastructure.Features.Queue.Hangfire;

public static class HangfireApplicationBuilderExtensions
{
    public static IApplicationBuilder UseNeoHangfireDashboard(
        this IApplicationBuilder app, IConfiguration configuration, IHostEnvironment env,
        string path = "/hangfire", bool readOnly = false, 
        string? username = null, string? password = null)
    {
        var options = new DashboardOptions
        {
            AppPath = "/",
            IsReadOnlyFunc = _ => readOnly
        };

        if (!string.IsNullOrWhiteSpace(username) && !string.IsNullOrWhiteSpace(password))
        {
            options.Authorization =
            [
                new HangfireCustomBasicAuthFilter
                {
                    User = username,
                    Pass = password
                }
            ];
        }

        // در Development دسترسی آزاد است
        //if (env.IsDevelopment())
        {
            options.Authorization = [new AllowAllDashboardAuthorizationFilter()];
        }

        app.UseHangfireDashboard(path, options);
        return app;
    }
}
