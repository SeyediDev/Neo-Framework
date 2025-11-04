using Neo.Application.Features.Outbox;
using Neo.Application.Features.Queue;
using Microsoft.Extensions.Logging;

namespace Neo.Infrastructure.Features.Queue;

public class RegisterRecurringJobs(IRecurringJobsManager recurringJobsManager,
    ILogger<RegisterRecurringJobs> logger) : IRegisterRecurringJobs
{
    public void Register()
    {
        RegisterJob<IProcessOutboxRecurringJob>();
    }


    private void RegisterJob<TRecurringJob>()
        where TRecurringJob : IRecurringJob
    {
        logger.LogInformation("Register {jobName} {DateTime.Now}", typeof(TRecurringJob).Name, DateTime.Now);
        recurringJobsManager.AddOrUpdate<TRecurringJob>();
    }
}