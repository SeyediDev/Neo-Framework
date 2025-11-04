namespace Neo.Application.Features.Queue;

public interface IEventContainer
{
    public List<INotification> Events { get; set; }
    public void AddEvent(INotification notification);
    public void ClearEvents();
}
