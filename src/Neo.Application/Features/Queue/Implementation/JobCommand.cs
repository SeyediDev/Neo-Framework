namespace Neo.Application.Features.Queue.Implementation;

public class JobCommand(ISender sender) : IJobCommand
{
    public async Task Run<TCommandRequest>(TCommandRequest command, CancellationToken cancellationToken)
        where TCommandRequest : IRequest
    {
        await sender.Send(command, cancellationToken);
    }
}