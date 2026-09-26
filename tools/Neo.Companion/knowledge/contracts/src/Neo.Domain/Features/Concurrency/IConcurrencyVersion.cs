namespace Neo.Domain.Features.Concurrency;

/// <summary>Application-managed optimistic token. Regenerate on every persisted change, including non-CRUD writes.</summary>
public interface IConcurrencyVersion
{
    Guid Version { get; set; }
}

public enum EntityConcurrencyMode { Optimistic, Pessimistic }

public sealed class EntityConcurrencyException(string code, string message, Exception? inner = null) : Exception(message, inner)
{
    public string Code { get; } = code;
}
