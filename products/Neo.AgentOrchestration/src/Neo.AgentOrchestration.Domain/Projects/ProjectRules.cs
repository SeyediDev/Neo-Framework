namespace Neo.AgentOrchestration.Domain.Projects;

internal static class ProjectRules
{
    public static string Key(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        key = key.Trim();
        if (key.Length > 80 || key.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')))
            throw new ArgumentException("A key must contain 1-80 ASCII letters, digits, dots, hyphens or underscores.", nameof(key));
        return key.ToUpperInvariant();
    }

    public static string Name(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        name = name.Trim();
        if (name.Length > 200) throw new ArgumentException("A name cannot exceed 200 characters.", nameof(name));
        return name;
    }

    public static Guid Id(Guid id)
        => id != Guid.Empty ? id : throw new ArgumentException("A persisted identifier is required.", nameof(id));
}
