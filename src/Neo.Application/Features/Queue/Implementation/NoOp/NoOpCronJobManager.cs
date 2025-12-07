namespace Neo.Application.Features.Queue.Implementation.NoOp;

public sealed class NoOpCronJobManager : ICronJobManager
{
    public Func<string> Daily(int hour, int minute) => () => $"0 {minute} {hour} * * *";

    public Func<string> Hourly(int minute) => () => $"0 {minute} * * * *";

    public Func<string> Minutely => () => "* * * * *";

    public Func<string> MinuteInterval(int interval) => () => $"*/{Math.Max(1, interval)} * * * *";
}

