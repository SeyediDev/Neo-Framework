using Hangfire.Dashboard;

namespace Neo.Infrastructure.Features.Queue.Hangfire;

// برای محیط develop
internal class AllowAllDashboardAuthorizationFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context) => true;
}
