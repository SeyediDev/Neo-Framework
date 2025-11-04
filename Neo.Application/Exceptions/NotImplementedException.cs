namespace Neo.Application.Exceptions;

public class NotImplementedException<TInterface> : NotImplementedException
{
    public NotImplementedException()
        : base($"Not Implemented Exception {typeof(TInterface).Name}")
    {
    }
}
