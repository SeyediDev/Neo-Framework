namespace Neo.AgentOrchestration.Domain.Agents;

internal static class ProfileRules
{
    public static string? Text(string? text, int maximum)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (text.Length > maximum) throw new ArgumentException($"Text exceeds {maximum} characters.", nameof(text));
        return text;
    }

    public static string? SkillPath(string? path)
    {
        path = Text(path, 512)?.Replace('\\', '/');
        if (path is null) return null;
        if (path.StartsWith('/') || path.Contains(':') || path.Any(char.IsControl) ||
            path.Split('/').Any(p => p is "" or "." or ".."))
            throw new ArgumentException("A skill path must be repository-relative without traversal.", nameof(path));
        return path;
    }
}
