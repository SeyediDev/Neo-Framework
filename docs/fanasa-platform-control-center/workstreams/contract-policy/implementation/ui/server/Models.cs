using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Fanasa.ContractPolicy;

namespace Fanasa.ContractPolicy.Workbench;

public sealed record PolicyInput(string Name, bool ExplicitDeny, string[] ZoneConstraints,
    double RequestsPerSecond, long BurstCapacity, long MonthlyQuota);
public sealed record ValidateConfigurationCommand(PolicyInput Policy, string Author, string Reviewer);
public sealed record DraftCommand(long ExpectedRevision, string Author, string Reason, PolicyInput Policy);
public sealed record PublishCommand(long ExpectedRevision, string DraftId, string Reviewer, string Reason);
public sealed record PolicyCommand(long ExpectedRevision, string PolicyId, string Reviewer, string Reason);
public sealed record EvaluateCommand(string ScenarioId, string? PolicyId = null, string? ProposedUnitName = null);
public sealed record ExecuteCommand(string DecisionId, string ScenarioId, string? FailObligation = null);
public sealed record RevisionCommand(long ExpectedRevision);
public sealed record PolicyDocument(string PolicyId, long PolicyVersion, string Name, bool ExplicitDeny,
    string[] ZoneConstraints, double RequestsPerSecond, long BurstCapacity, long MonthlyQuota,
    string Author, string? Reviewer, string Status, string CreatedAt, string? PublishedAt, string Reason,
    string? DraftId = null);
public sealed record Scenario(string Id, string Title, string Description, string ExpectedDecision,
    string ExpectedExecution, string[] Changes);
public sealed record Inspection(string Id, string Label, string Status, object Input, string Explanation);
public sealed record Provenance(string Component, string Source, string Trust, string Detail);
public sealed record WorkbenchResource(string ResourceId, string TenantId, string ProductId, string Environment);
public sealed record UnitRecord(string ResourceId, string TenantId, string ProductId, string Environment,
    string Name, long SaveCount, string? LastSavedAt);
public sealed record DecisionRun(string DecisionId, string ScenarioId, PolicyDecision RawDecision,
    ContractAccessProjection Projection, AccessEvaluationContext Context, Provenance[] Provenance,
    Inspection[] Inspection, string ExecutionClock, string PolicySelection,
    WorkbenchResource Resource, string CreatedAt, string BindingFingerprint, string? ProposedUnitName = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? TrustedSourcesFingerprint = null);
public sealed record ObligationResult(string Id, string Status, string Detail);
public sealed record Operation(string OperationId, string DecisionId, string ScenarioId, string ResourceId,
    string TenantId, string Action, string ExecutedAt, int MeterUnits, string? SavedName = null);
public sealed record Usage(string AllocationId, string PeriodId, long Admitted, long Remaining,
    double Tokens, long ConfigRevision, string? LastAdmittedAt);
public sealed record ExecutionResult(bool Accepted, bool Idempotent, string ReasonCode,
    ObligationResult[] Obligations, Operation? Operation, Usage Usage, PolicyDecision RawDecision);
public sealed record AuditEntry(string Id, string Type, string At, string Actor, string Reason,
    long Revision, JsonNode? Details);
public sealed record WorkbenchState
{
    public long Revision { get; set; } = 1;
    public required ProjectionState ProjectionState { get; set; }
    public AccessEvaluationContext? TrustedContext { get; set; }
    public List<PolicyDocument> Policies { get; set; } = [];
    public List<PolicyDocument> Drafts { get; set; } = [];
    public List<DecisionRun> Decisions { get; set; } = [];
    public List<Operation> Operations { get; set; } = [];
    public List<UnitRecord> Resources { get; set; } = [];
    public Dictionary<string, ExecutionResult> Executions { get; set; } = [];
    public List<AuditEntry> History { get; set; } = [];
    public long Admitted { get; set; }
    public string PeriodId { get; set; } = "";
    public double Tokens { get; set; }
    public string LastRefillAt { get; set; } = "";
    public string? LastAdmittedAt { get; set; }
}
public sealed class WorkbenchError(int status, string code, string message, long? revision = null)
    : Exception(message)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public long? Revision { get; } = revision;
}
