namespace Neo.Application.Features.Queue;

public interface ICronJobManager
{
    Func<string> Daily(int hour, int minute);
    Func<string> Hourly(int minute);
    Func<string> Minutely { get; }
    Func<string> MinuteInterval(int interval);
}
