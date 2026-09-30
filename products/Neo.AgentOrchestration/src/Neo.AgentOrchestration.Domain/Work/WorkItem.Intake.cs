using Neo.AgentOrchestration.Domain.Projects;

namespace Neo.AgentOrchestration.Domain.Work;

public sealed partial class WorkItem
{
    // Stored in the append-only aggregate log, in the same workspace-serialized
    // transaction as creation. Ordinary notes cannot forge this reserved kind.
    public void RecordIntake(WorkspaceScope scope, WorkActor actor, Guid requestId, string fingerprint, DateTimeOffset now)
    {
        RequireEditable(scope, actor, now);
        if (requestId == Guid.Empty || fingerprint.Length != 64 || !fingerprint.All(Uri.IsHexDigit))
            throw new ArgumentException("Invalid intake identity or fingerprint.");
        if (Status != WorkItemStatus.Backlog || OwnerAgentId is not null || _logs.Any(x => x.Kind == "ChatIntake"))
            throw new InvalidOperationException("Intake can only be recorded once during creation.");
        Record(actor, "ChatIntake", $"{requestId:D}:{fingerprint}", now);
    }
}
