namespace Neo.AgentOrchestration.Domain.Work;

public enum WorkItemStatus : byte { Backlog = 1, Ready = 2, InProgress = 3, Blocked = 4, Review = 5, Done = 6, Cancelled = 7 }
public enum WorkItemPriority : byte { Low = 1, Normal = 2, High = 3, Critical = 4 }
public enum EvidenceKind : byte { Commit = 1, Test = 2, Artifact = 3 }
public enum EvidenceOutcome : byte { NotApplicable = 0, Passed = 1, Failed = 2, Skipped = 3 }
