namespace Neo.Application.Features.Queue;

public interface IRegisterRecurringJobs
{
    [Telemetry]
    void Register();
}
