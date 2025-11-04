using Neo.Application.Features.Queue;
using Hangfire;

namespace Neo.Infrastructure.Features.Queue.Hangfire;

public class HangfireCronJobManager : ICronJobManager
{
    public Func<string> Minutely => Cron.Minutely;
    public Func<string> Hourly(int minute) => () => Cron.Hourly(minute);
    public Func<string> Daily(int hour, int minute) => () => Cron.Daily(hour, minute);

    //[Obsolete]
    [Obsolete]
    public Func<string> MinuteInterval(int interval) => () => Cron.MinuteInterval(interval);
}
