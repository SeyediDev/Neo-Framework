using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Neo.AgentOrchestration.Application.ExternalAgents;

namespace Neo.AgentOrchestration.Infrastructure.ExternalAgents;

// Pure signal translation, not an approval grant. Gateway must collect events
// on its authenticated connection and preserve pending approval across restart.
// Unknown event schemas fail closed; never render upstream command/HTML here.
public static class NativeAgentPermissionSignals
{
    // Snapshot complements transient SSE signals. No patterns/commands/tool
    // arguments leave this projection, and it never authorizes a native reply.
    public static ExternalAgentObservation? ReadPendingOpenCode(ExternalAgentHandle handle, JsonElement requests)
    {
        if (handle.Engine != ExternalAgentEngine.OpenCode || handle.NativeId is null ||
            requests.ValueKind != JsonValueKind.Array || requests.GetArrayLength() > 1000)
            throw new ExternalAgentException("native-agent-permission-snapshot-invalid");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var request in requests.EnumerateArray())
        {
            var session = Text(request, "sessionID");
            if (string.IsNullOrEmpty(session)) throw new ExternalAgentException("native-agent-permission-snapshot-invalid");
            if (session != handle.NativeId) continue;
            var id = Text(request, "id");
            if (id is null || !Regex.IsMatch(id, @"\Aper[A-Za-z0-9_-]{0,97}\z", RegexOptions.CultureInvariant) || !ids.Add(id))
                throw new ExternalAgentException("native-agent-permission-snapshot-invalid");
        }
        // Multiple requests mean pending approval, NOT a guessed request ID.
        return ids.Count == 0 ? null : new(ExternalAgentState.AwaitingApproval, [], ids.Count == 1 ? ids.Single() : null);
    }

    public static ExternalAgentObservation? Read(ExternalAgentHandle handle, string eventName, string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > 32 * 1024) throw new ExternalAgentException("native-agent-event-limit");
        try
        {
            using var document = JsonDocument.Parse(json); var root = document.RootElement;
            if (handle.Engine == ExternalAgentEngine.Hermes)
            {
                if (eventName != "approval.request" || Text(root, "run_id") != handle.NativeId || handle.NativeId is null) return null;
                // Hermes resolves by run route. Approval body/choices require an
                // installed-version capability/schema check before any reply.
                return new(ExternalAgentState.AwaitingApproval, []);
            }
            if (Text(root, "type") is not ("permission.updated" or "permission.asked")) return null;
            if (!root.TryGetProperty("properties", out var properties) || Text(properties, "sessionID") != handle.NativeId ||
                handle.NativeId is null) return null;
            var id = Text(properties, "id");
            if (id is null || !Regex.IsMatch(id, @"\A[A-Za-z0-9_-]{1,100}\z", RegexOptions.CultureInvariant))
                throw new ExternalAgentException("native-agent-permission-identifier");
            return new(ExternalAgentState.AwaitingApproval, [], id);
        }
        catch (JsonException) { throw new ExternalAgentException("native-agent-event-protocol"); }
    }
    private static string? Text(JsonElement parent, string name) => parent.ValueKind == JsonValueKind.Object &&
        parent.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
