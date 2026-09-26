using Neo.Domain.Entities.Base;

namespace Neo.AgentOrchestration.Domain.Work;

public sealed class WorkItemLog : BaseEntity<Guid>
{
    public Guid WorkItemId { get; private set; }
    public string AgentId { get; private set; } = "";
    public string ChatId { get; private set; } = "";
    public string Kind { get; private set; } = "";
    public string Message { get; private set; } = "";
    public DateTimeOffset CreatedAtUtc { get; private set; }

    public WorkItemLog() { }
    internal static WorkItemLog Create(Guid itemId, WorkActor actor, string kind, string message, DateTimeOffset now)
        => new() { Id = Guid.NewGuid(), WorkItemId = itemId, AgentId = actor.AgentId, ChatId = actor.ChatId,
            Kind = kind, Message = message, CreatedAtUtc = now.ToUniversalTime() };
}

public sealed class WorkItemTimeEntry : BaseEntity<Guid>
{
    public Guid WorkItemId { get; private set; }
    public DateTimeOffset StartedAtUtc { get; private set; }
    public DateTimeOffset? EndedAtUtc { get; private set; }
    public long DurationSeconds { get; private set; }
    public WorkItemTimeEntry() { }
    internal static WorkItemTimeEntry Start(Guid itemId, DateTimeOffset now)
        => new() { Id = Guid.NewGuid(), WorkItemId = itemId, StartedAtUtc = now.ToUniversalTime() };
    internal void Stop(DateTimeOffset now)
    {
        if (EndedAtUtc.HasValue) return;
        if (now < StartedAtUtc) throw new InvalidOperationException("Interval cannot end before it starts.");
        EndedAtUtc = now.ToUniversalTime();
        DurationSeconds = (long)(now - StartedAtUtc).TotalSeconds;
    }
}

public sealed class WorkItemDependency : BaseEntity<Guid>
{
    public Guid WorkItemId { get; private set; }
    public Guid DependsOnWorkItemId { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public WorkItemDependency() { }
    internal static WorkItemDependency Create(Guid itemId, Guid dependsOn, DateTimeOffset now)
        => new() { Id = Guid.NewGuid(), WorkItemId = itemId, DependsOnWorkItemId = dependsOn, CreatedAtUtc = now.ToUniversalTime() };
}

public sealed class WorkItemEvidence : BaseEntity<Guid>
{
    public Guid WorkItemId { get; private set; }
    public int Sequence { get; private set; }
    public string? CommitSha { get; private set; }
    public EvidenceKind Kind { get; private set; }
    public string Reference { get; private set; } = "";
    public EvidenceOutcome Outcome { get; private set; }
    public string? Details { get; private set; }
    public DateTimeOffset CreatedAtUtc { get; private set; }
    public WorkItemEvidence() { }

    internal static WorkItemEvidence Create(Guid itemId, EvidenceKind kind, string reference,
        EvidenceOutcome outcome, string? details, DateTimeOffset now, int sequence, string? commitSha)
    {
        if (!Enum.IsDefined(kind) || !Enum.IsDefined(outcome)) throw new ArgumentException("Unknown evidence kind or outcome.");
        reference = WorkRules.Required(reference, 2000);
        if (kind == EvidenceKind.Commit && (reference.Length is not (40 or 64) || reference.Any(c => !char.IsAsciiHexDigit(c))))
            throw new ArgumentException("Commit evidence requires a full hexadecimal SHA.");
        if ((kind == EvidenceKind.Test) != (outcome != EvidenceOutcome.NotApplicable))
            throw new ArgumentException("Only test evidence has a test outcome; a test requires an outcome.");
        if (sequence <= 0) throw new ArgumentOutOfRangeException(nameof(sequence));
        commitSha = WorkRules.Optional(commitSha, 64)?.ToLowerInvariant();
        if (commitSha is not null && (kind == EvidenceKind.Commit || commitSha.Length is not (40 or 64) ||
            commitSha.Any(c => !char.IsAsciiHexDigit(c))))
            throw new ArgumentException("Only test/artifact evidence may reference a full commit SHA.");
        return new WorkItemEvidence { Id = Guid.NewGuid(), WorkItemId = itemId, Kind = kind,
            Sequence = sequence, CommitSha = commitSha,
            Reference = reference, Outcome = outcome, Details = WorkRules.Optional(details, 8000),
            CreatedAtUtc = now.ToUniversalTime() };
    }
}
