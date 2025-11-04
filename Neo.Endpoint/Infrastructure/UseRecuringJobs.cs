using Neo.Application.Features.Queue;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace Neo.Endpoint.Infrastructure;
public static class Extension
{
    public static WebApplication UseRecuringJobs(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var r = scope.ServiceProvider.GetRequiredService<IRegisterRecurringJobs>();
        r?.Register();
        return app;
    }
}