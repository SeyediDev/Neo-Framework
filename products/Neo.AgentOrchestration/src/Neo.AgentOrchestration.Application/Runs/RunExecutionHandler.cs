using System.Text.Json;
using Neo.AgentOrchestration.Application.Delivery;
using Neo.AgentOrchestration.Application.Work;
using Neo.AgentOrchestration.Application.Workflows;
using Neo.AgentOrchestration.Domain.Runs;
using Neo.AgentOrchestration.Domain.Work;
using Neo.AgentOrchestration.Contracts;

namespace Neo.AgentOrchestration.Application.Runs;

// This handler is invoked by SqlWorkDeliveryExecutor with its SAME session.
// It never opens another store transaction or invokes an external provider.
public sealed class RunExecutionHandler(ISimulationHarness harness, TimeProvider clock, IRunProviders? providers = null)
{
    private readonly IRunProviders providers = providers ?? new SimulationRunProviders();
    public async Task ExecuteAsync(WorkDeliveryExecution operation, IWorkItemSession session, CancellationToken ct)
    {
        var s = RunHandlers.Session(session);
        var run = await RunHandlers.Run(s, operation.AgentRunId ?? throw new InvalidOperationException("Run delivery required."), ct);
        var item = await RunHandlers.Item(s, operation.WorkItemId, ct);
        if (run.WorkItemId != item.Id || run.ProjectId != operation.ProjectId) throw new InvalidOperationException("Run scope mismatch.");
        switch (operation.Kind)
        {
            case WorkDeliveryKind.DispatchAgent:
                if (run.Status != AgentRunStatus.Queued) return;
                providers.RequireAvailable(run.Provider, operation.Scope);
                RunHandlers.Version(item.Version, operation.RequestedWorkItemVersion);
                var flow = await RunHandlers.Workflow(s, run.WorkflowId, ct);
                var profile = (await s.GetAgentsAsync(ct)).SingleOrDefault(x => x.Id == run.AgentProfileId);
                var role = await s.GetRoleAsync(run.RoleId, ct);
                if (profile is not { IsEnabled: true } || role is not { IsEnabled: true } || !flow.IsEnabled || flow.Version != run.WorkflowVersion)
                {
                    run.DeclineDispatch(item, "Execution not dispatched: profile, role or pinned workflow is unavailable.", clock.GetUtcNow());
                    return;
                }
                if (run.Provider != "fake")
                {
                    var binding = providers.Bind(run.Provider, operation.Scope, run.Id);
                    var context = WorkItemProjection.Details(item, await s.GetProjectItemsAsync(item.ProjectId, ct), clock.GetUtcNow());
                    run.PrepareHarness(binding.Fingerprint, JsonSerializer.Serialize(new HarnessRequest("neo-harness/v1", run.Id,
                        run.OrganizationId, run.WorkspaceId, run.RoleId, run.AgentProfileId, run.Model, run.Instructions,
                        run.SkillPath, run.Branch, binding.CallbackUrl, context), new JsonSerializerOptions(JsonSerializerDefaults.Web)));
                    run.MarkDispatched(item, clock.GetUtcNow());
                    await s.StageMessageAsync(Message(run, "http-dispatch", new { run.Id }), WorkDeliveryKind.SendHarnessRequest, clock, ct);
                    break;
                }
                run.MarkDispatched(item, clock.GetUtcNow());
                // The queued callback is a separate durable execution. A crash
                // after dispatch cannot lose it or silently mark the run complete.
                await s.StageMessageAsync(Message(run, "fake-dispatch-result", harness.Result(run)),
                    WorkDeliveryKind.ReceiveSimulationResult, clock, ct);
                break;
            case WorkDeliveryKind.ReceiveSimulationResult:
                providers.RequireAvailable("fake", operation.Scope);
                RunHandlers.RequireSimulation(run.Provider);
                var result = harness.Result(run);
                await s.StageInboxAsync(Message(run, "fake-callback", result), (_, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    RunHandlers.Version(item.Version, operation.RequestedWorkItemVersion);
                    run.RecordResult(item, result.Outcome, result.Summary, clock.GetUtcNow());
                    return Task.CompletedTask;
                }, result.Outcome == SimulationOutcome.Succeeded ? WorkDeliveryKind.EvaluateWorkflow : null, clock, ct);
                break;
            case WorkDeliveryKind.EvaluateWorkflow:
                await Evaluate(operation, s, run, item, ct); break;
            case WorkDeliveryKind.SendHarnessRequest:
                if (run.Provider == "fake" || run.HarnessPayload is null) throw new InvalidOperationException("External snapshot required.");
                // HTTP happened before entering this database transaction. A
                // callback may already have completed/handed off this run.
                break;
            default: throw new ArgumentException("Unsupported run delivery kind.");
        }
    }

    private async Task Evaluate(WorkDeliveryExecution operation, IRunSession s, AgentRun run, WorkItem item, CancellationToken ct)
    {
        if (run.Decision is RunDecision.HandedOff or RunDecision.Completed) return;
        var now = clock.GetUtcNow();
        if (run.Status is AgentRunStatus.Queued or AgentRunStatus.AwaitingResult) { run.Wait("result-not-received", now); return; }
        if (operation.RequestedWorkItemVersion != item.Version || run.WorkItemVersion != item.Version)
        { run.Wait("work-version-changed", now); return; }
        run.RequireCurrent(item);
        var workflow = await RunHandlers.Workflow(s, run.WorkflowId, ct);
        if (workflow.Version != run.WorkflowVersion) { run.Wait("workflow-version-changed", now); return; }
        var transition = workflow.Transitions.SingleOrDefault(x => x.IsEnabled && x.FromRoleId == run.RoleId && x.FromStatus == item.Status);
        if (transition is null) { run.Wait("no-matching-transition", now); return; }
        var roles = await s.GetRolesAsync(ct); var profiles = await s.GetAgentsAsync(ct);
        Neo.AgentOrchestration.Contracts.WorkflowPlan plan;
        try { plan = WorkflowPlanner.Preview(operation.Scope, workflow, transition.Id, item, roles, profiles,
            await s.GetApprovalsAsync(workflow.Id, item.Id, ct), now); }
        catch (InvalidOperationException) { run.Wait("agent-selection-required", now); return; }
        if (!plan.GatesSatisfied) { run.Wait(string.Join(",", plan.UnmetConditions), now); return; }
        var projectItems = await s.GetProjectItemsAsync(item.ProjectId, ct);
        try { item.RequireCompletedDependencies(projectItems); }
        catch (InvalidOperationException) { run.Wait("dependencies-incomplete", now); return; }
        if (transition.ToStatus == WorkItemStatus.Done)
        {
            if (projectItems.Any(x => x.ParentWorkItemId == item.Id && x.Status is not (WorkItemStatus.Done or WorkItemStatus.Cancelled)))
            { run.Wait("children-incomplete", now); return; }
            item.ChangeStatus(operation.Scope, run.Actor, WorkItemStatus.Done, now, "Workflow completed after execution.");
            run.Advance(null, now); return;
        }
        if (run.Hop >= 20) { run.Wait("handoff-limit-reached", now); return; }
        var nextRole = roles.Single(x => x.Id == plan.TargetRoleId);
        if (await s.IsRoleBusyAsync(nextRole.Id, nextRole.MaxConcurrentWorkItems, item.Id, ct)) { run.Wait("target-role-busy", now); return; }
        var nextProfile = profiles.Single(x => x.Id == plan.AgentProfileId);
        try { providers.RequireAvailable(nextProfile.Provider, operation.Scope); }
        catch (RunProviderUnavailableException) { run.Wait("target-provider-unavailable", now); return; }
        if (nextProfile.Provider != "fake" && !run.AllowExternalExecution) { run.Wait("external-execution-not-authorized", now); return; }
        item.ChangeStatus(operation.Scope, run.Actor, WorkItemStatus.Ready, now, "Workflow handoff after execution.");
        var next = AgentRun.Create(operation.Scope, Guid.NewGuid(), item, workflow, nextRole, nextProfile,
            new(run.RequestedByAgentId, run.RequestedByChatId), run.Branch, SimulationOutcome.Succeeded, now, run);
        item.Claim(operation.Scope, nextRole, next.Actor, next.Branch, now);
        next.BindClaim(item); s.Add(next); run.Advance(next.Id, now);
        await s.StageMessageAsync(Message(next, "workflow-handoff", new { PreviousRunId = run.Id }), WorkDeliveryKind.DispatchAgent, clock, ct);
    }
    private static WorkDeliveryRequest Message(AgentRun run, string source, object payload)
        => new(run.WorkItemId, source, run.Id.ToString("N"), JsonSerializer.Serialize(payload), run.Id);
}
