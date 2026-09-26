namespace Neo.AgentOrchestration.Domain.Work;

// Supplied by the application from authenticated execution context, not treated
// as authentication merely because an HTTP caller knows an agent or chat ID.
public sealed record WorkActor
{
    public string AgentId { get; }
    public string ChatId { get; }

    public WorkActor(string agentId, string chatId)
    {
        AgentId = WorkRules.Required(agentId, 200);
        ChatId = WorkRules.Required(chatId, 200);
    }
}

internal static class WorkRules
{
    public static string Required(string text, int max)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        text = text.Trim();
        if (text.Length > max) throw new ArgumentException($"Text exceeds {max} characters.", nameof(text));
        return text;
    }

    public static string? Optional(string? text, int max)
        => string.IsNullOrWhiteSpace(text) ? null : Required(text, max);
}
