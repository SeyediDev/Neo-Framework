namespace Neo.Application.Exceptions;

public class DuplicateKeyException : Exception
{
    public DuplicateKeyException()
    {
    }

    public DuplicateKeyException(string message) : base(message)
    {
    }

    public DuplicateKeyException(string? message, Exception? innerException) : base(message, innerException)
    {
    }
}
