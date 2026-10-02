using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediatR;
using Neo.AgentOrchestration.Application.Agents;
using Neo.AgentOrchestration.Application.Delivery;
using Neo.AgentOrchestration.Application.Work;
using Neo.AgentOrchestration.Contracts;
using Neo.AgentOrchestration.Domain.Projects;
using Neo.AgentOrchestration.Domain.Runs;
using Neo.AgentOrchestration.Domain.Work;
using Neo.AgentOrchestration.Domain.Workflows;

namespace Neo.AgentOrchestration.Application.Runs;

public sealed record StartAgentRun(WorkspaceScope Scope, Guid WorkItemId, WorkActor Requestor, StartAgentRunRequest Body) : IRequest<AgentRunDetails>;
public sealed record EvaluateAgentRun(WorkspaceScope Scope, Guid RunId, WorkActor Requestor, EvaluateAgentRunRequest Body) : IRequest<AgentRunDetails>;
public sealed record GetAgentRun(WorkspaceScope Scope, Guid RunId) : IRequest<AgentRunDetails>;
public sealed record ReturnRunAssignment(WorkspaceScope Scope, Guid RunId, WorkActor Requestor, ReturnRunAssignmentRequest Body) : IRequest<AgentRunDetails>;
public sealed record GetAgentRuns(WorkspaceScope Scope, Guid WorkItemId) : IRequest<IReadOnlyList<AgentRunDetails>>;
public sealed record RecordAgentRunUsage(WorkspaceScope Scope, Guid RunId, RecordTokenUsageRequest Body) : IRequest<AgentRunDetails>;

public sealed class RunHandlers(IWorkspaceWorkStore store, IDurableWorkStore durable, TimeProvider clock, IRunProviders? providers = null) :
    IRequestHandler<StartAgentRun, AgentRunDetails>, IRequestHandler<EvaluateAgentRun, AgentRunDetails>,
    IRequestHandler<GetAgentRun, AgentRunDetails>, IRequestHandler<GetAgentRuns, IReadOnlyList<AgentRunDetails>>,
    IRequestHandler<ReturnRunAssignment, AgentRunDetails>, IRequestHandler<RecordAgentRunUsage, AgentRunDetails>
{
    private readonly IRunProviders providers = providers ?? new SimulationRunProviders();
    public async Task<AgentRunDetails> Handle(StartAgentRun r, CancellationToken ct)
    {
        if (r.Body.RequestId == Guid.Empty) throw new ArgumentException("Request ID is required.");
        var outcome = Outcome(r.Body.SimulationOutcome);
        await durable.EnqueueAsync(r.Scope, Request(r.WorkItemId, r.Body.RequestId, "run.start", r.Requestor, StartPayload(r.Body), r.Body.RequestId),
            WorkDeliveryKind.DispatchAgent, async (session, token) =>
            {
                var s = Session(session);
                var item = await Item(s, r.WorkItemId, token); Version(item.Version, r.Body.ExpectedWorkItemVersion);
                var workflow = await Workflow(s, r.Body.WorkflowId, token); Version(workflow.Version, r.Body.ExpectedWorkflowVersion);
                var role = await s.GetRoleAsync(r.Body.RoleId, token) ?? throw new KeyNotFoundException("Role not found.");
                if (await s.IsRoleBusyAsync(role.Id, role.MaxConcurrentWorkItems, item.Id, token) || (await s.GetRunsAsync(item.Id, token))
                    .Any(x => x.Status is AgentRunStatus.Queued or AgentRunStatus.AwaitingResult))
                    throw new WorkItemConflictException("Role or task already has an active execution.");
                item.RequireCompletedDependencies(await s.GetProjectItemsAsync(item.ProjectId, token));
                var profile = AgentSelector.Select(r.Scope, role, await s.GetAgentsAsync(token), r.Body.AgentProfileId);
                providers.RequireAvailable(profile.Provider, r.Scope);
                if (profile.Provider != "fake" && !r.Body.AllowExternalExecution)
                    throw new ArgumentException("External execution requires explicit consent.");
                var now = clock.GetUtcNow();
                var run = AgentRun.Create(r.Scope, r.Body.RequestId, item, workflow, role, profile, r.Requestor, r.Body.Branch, outcome, now,
                    allowExternalExecution: r.Body.AllowExternalExecution);
                item.Claim(r.Scope, role, run.Actor, run.Branch, now);
                item.AddLog(r.Scope, run.Actor, $"Execution requested by {r.Requestor.AgentId}, chat {r.Requestor.ChatId}; provider {profile.Provider}.", now);
                run.BindClaim(item); s.Add(run);
            }, ct);
        return await Handle(new GetAgentRun(r.Scope, r.Body.RequestId), ct);
    }

    public async Task<AgentRunDetails> Handle(EvaluateAgentRun r, CancellationToken ct)
    {
        if (r.Body.RequestId == Guid.Empty) throw new ArgumentException("Request ID is required.");
        var snapshot = await Handle(new GetAgentRun(r.Scope, r.RunId), ct);
        providers.RequireAvailable(snapshot.Run.Provider, r.Scope);
        await durable.EnqueueAsync(r.Scope, Request(snapshot.Run.WorkItemId, r.Body.RequestId, "run.eval", r.Requestor, r.Body, r.RunId),
            WorkDeliveryKind.EvaluateWorkflow, async (session, token) =>
            {
                var s = Session(session); var run = await Run(s, r.RunId, token); var item = await Item(s, run.WorkItemId, token);
                var flow = await Workflow(s, run.WorkflowId, token);
                Version(item.Version, r.Body.ExpectedWorkItemVersion); Version(flow.Version, r.Body.ExpectedWorkflowVersion);
                run.RequestEvaluation(item, flow, r.Requestor, clock.GetUtcNow());
            }, ct);
        return await Handle(new GetAgentRun(r.Scope, r.RunId), ct);
    }
    public Task<AgentRunDetails> Handle(GetAgentRun r, CancellationToken ct)
        => store.ExecuteAsync(r.Scope, async (session, token) =>
        {
            var s = Session(session); var run = await Run(s, r.RunId, token);
            return new AgentRunDetails(View(run), await s.GetRunDeliveriesAsync(run.Id, token), Usage(await s.GetTokenUsageAsync(run.Id, token)));
        }, ct);
    public async Task<AgentRunDetails> Handle(ReturnRunAssignment r, CancellationToken ct)
    {
        await store.ExecuteAsync<bool>(r.Scope, async (session, token) =>
        {
            var s = Session(session); var run = await Run(s, r.RunId, token); var item = await Item(s, run.WorkItemId, token);
            Version(item.Version, r.Body.ExpectedWorkItemVersion);
            run.ReturnAssignment(item, r.Requestor, clock.GetUtcNow()); return true;
        }, ct);
        return await Handle(new GetAgentRun(r.Scope, r.RunId), ct);
    }
    public Task<IReadOnlyList<AgentRunDetails>> Handle(GetAgentRuns r, CancellationToken ct)
        => store.ExecuteAsync<IReadOnlyList<AgentRunDetails>>(r.Scope, async (session, token) =>
        {
            var s = Session(session); _ = await Item(s, r.WorkItemId, token); var result = new List<AgentRunDetails>();
            foreach (var run in await s.GetRunsAsync(r.WorkItemId, token))
                result.Add(new(View(run), await s.GetRunDeliveriesAsync(run.Id, token), Usage(await s.GetTokenUsageAsync(run.Id, token))));
            return result;
        }, ct);

    public async Task<AgentRunDetails> Handle(RecordAgentRunUsage r, CancellationToken ct)
    {
        if (r.Body.RequestId == Guid.Empty) throw new ArgumentException("Request ID is required.");
        var key = string.IsNullOrWhiteSpace(r.Body.IdempotencyKey) ? r.Body.RequestId.ToString("N") : r.Body.IdempotencyKey.Trim();
        await store.ExecuteAsync<bool>(r.Scope, async (session, token) =>
        {
            var s = Session(session); var run = await Run(s, r.RunId, token);
            var existing = await s.FindTokenUsageAsync(key, token);
            if (existing is not null)
            {
                if (existing.AgentRunId != run.Id) throw new WorkItemConflictException("Usage idempotency key belongs to another run.");
                return true;
            }
            var provider = string.IsNullOrWhiteSpace(r.Body.Provider) ? run.Provider : r.Body.Provider.Trim();
            if (!string.Equals(provider, run.Provider, StringComparison.OrdinalIgnoreCase))
                throw new UnauthorizedAccessException("Usage provider does not belong to this run.");
            var source = r.Body.Source.Trim().ToLowerInvariant();
            if (source is not ("reported" or "estimated" or "imported")) throw new ArgumentException("Unknown usage source.");
            var usage = TokenUsageReport.Create(r.Scope, r.Body.RequestId, run.Id, run.WorkItemId, provider, r.Body.Model ?? run.Model,
                r.Body.InputTokens, r.Body.OutputTokens, r.Body.CachedInputTokens, r.Body.ReasoningTokens, source, key,
                r.Body.RecordedAtUtc ?? clock.GetUtcNow());
            s.Add(usage);
            return true;
        }, ct);
        return await Handle(new GetAgentRun(r.Scope, r.RunId), ct);
    }

    internal static IRunSession Session(IWorkItemSession session) => session as IRunSession
        ?? throw new InvalidOperationException("A durable run session is required.");
    internal static async Task<WorkItem> Item(IRunSession s, Guid id, CancellationToken ct)
        => await s.GetItemAsync(id, ct) ?? throw new KeyNotFoundException("Work item not found.");
    internal static async Task<AgentRun> Run(IRunSession s, Guid id, CancellationToken ct)
        => await s.GetRunAsync(id, ct) ?? throw new KeyNotFoundException("Run not found.");
    internal static async Task<WorkflowDefinition> Workflow(IRunSession s, Guid id, CancellationToken ct)
        => (await s.GetWorkflowsAsync(ct)).SingleOrDefault(x => x.Id == id) ?? throw new KeyNotFoundException("Workflow not found.");
    internal static void Version(Guid current, Guid expected)
    { if (expected == Guid.Empty || expected != current) throw new WorkItemConflictException("Resource changed; reload before retrying."); }
    internal static void RequireSimulation(string provider)
    { if (provider != "fake") throw new InvalidOperationException("Only a fake run may receive an internal simulation result."); }
    private static SimulationOutcome Outcome(string value) => Enum.GetNames<SimulationOutcome>()
        .Any(x => string.Equals(x, value, StringComparison.OrdinalIgnoreCase)) && Enum.TryParse<SimulationOutcome>(value, true, out var parsed)
        ? parsed : throw new ArgumentException("Unknown simulation outcome.");
    private static WorkDeliveryRequest Request(Guid item, Guid requestId, string operation, WorkActor actor, object body, Guid run)
        => new(item, operation + "." + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(actor.AgentId))).ToLowerInvariant(),
            requestId.ToString("N"), JsonSerializer.Serialize(new { actor.AgentId, actor.ChatId, Body = body }), run);
    private static object StartPayload(StartAgentRunRequest body)
    {
        // Preserve the old simulation idempotency fingerprint across upgrade.
        if (body.AllowExternalExecution) return body;
        return new { body.RequestId, body.ExpectedWorkItemVersion, body.WorkflowId, body.ExpectedWorkflowVersion,
            body.RoleId, body.AgentProfileId, body.Branch, body.SimulationOutcome };
    }
    private static AgentRunView View(AgentRun x) => new(x.Id, x.WorkItemId, x.RoleId, x.AgentProfileId, x.WorkflowId,
        x.InitialWorkflowVersion, x.WorkflowVersion, x.WorkItemVersion, x.PreviousRunId, x.NextRunId, x.Hop, x.Provider,
        x.Model, x.Instructions, x.SkillPath, x.Branch, x.RequestedByAgentId, x.RequestedByChatId, x.Status.ToString(), x.Decision.ToString(),
        x.DecisionReason, x.SimulationOutcome.ToString(), x.ResultSummary, x.CreatedAtUtc, x.DispatchedAtUtc, x.CompletedAtUtc, x.UpdatedAtUtc);
    private static IReadOnlyList<TokenUsageView> Usage(IReadOnlyList<TokenUsageReport> rows) => rows.Select(x => new TokenUsageView(x.Id,
        x.AgentRunId, x.WorkItemId, x.Provider, x.Model, x.InputTokens, x.OutputTokens, x.CachedInputTokens,
        x.ReasoningTokens, x.Source, x.IdempotencyKey, x.RecordedAtUtc)).ToArray();
}
