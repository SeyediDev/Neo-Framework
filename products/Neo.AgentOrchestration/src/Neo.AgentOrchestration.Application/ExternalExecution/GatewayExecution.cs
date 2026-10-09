using System.Text;
using System.Text.Json;
using Neo.AgentOrchestration.Application.ExternalAgents;
using Neo.AgentOrchestration.Contracts;
using Neo.AgentOrchestration.Domain.ExternalExecution;

namespace Neo.AgentOrchestration.Application.ExternalExecution;

// Called only by an explicitly composed, existing durable worker job. No hosted
// dispatcher, loop, retry scheduler or repository shell is started here.
public sealed class GatewayExecution(IGatewayJournal journal, IGatewayBindings bindings,
    IGatewaySandbox sandbox, IGatewayResultDelivery delivery, TimeProvider clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<GatewayRun> ReserveAsync(string bindingKey, string body, CancellationToken ct)
        => ReserveAsync(bindingKey, body, null, ct);

    public async Task<GatewayRun> ReserveAsync(string bindingKey, string body, Guid? idempotencyRunId, CancellationToken ct)
    {
        var request = Parse(body);
        if (idempotencyRunId.HasValue && idempotencyRunId != request.Input.Scope.RunId)
            throw new ExternalAgentException("gateway-idempotency-invalid");
        var binding = bindings.Resolve(bindingKey, request.Input.Scope, request.ProfileId, request.CallbackUrl);
        var candidate = GatewayRun.Reserve(request.Input.Scope.RunId, request.Input.Scope.OrganizationId,
            request.Input.Scope.WorkspaceId, request.Input.Scope.ProjectId, binding.Key,
            binding.Fingerprint, binding.SandboxId, body, clock.GetUtcNow());
        // A dispatch host may return 202 ONLY after this durable operation.
        return await journal.ReserveAsync(candidate, ct);
    }

    public async Task<GatewayRun> AdvanceAsync(ExternalAgentScope scope, CancellationToken ct)
    {
        await AdvanceCoreAsync(scope, ct);
        // Return the committed post-release version, not a pre-finally snapshot.
        return await journal.ReadAsync(scope, ct);
    }
    private async Task<GatewayRun> AdvanceCoreAsync(ExternalAgentScope scope, CancellationToken ct)
    {
        var prior = await journal.ReadAsync(scope, ct);
        if (prior.Phase == GatewayPhase.Delivered) return prior;
        var initialRequest = Parse(prior.Payload);
        var initialBinding = bindings.Resolve(prior.BindingKey, scope, initialRequest.ProfileId, initialRequest.CallbackUrl);
        if (initialBinding.Fingerprint != prior.BindingFingerprint || initialBinding.SandboxId != prior.SandboxId)
            throw new ExternalAgentException("gateway-binding-changed");
        // Read-only isolation readiness must pass BEFORE acquiring capacity or
        // writing any native intent. A disabled installation keeps Reserved.
        if (prior.Phase != GatewayPhase.CallbackReady) await sandbox.VerifyReadyAsync(initialBinding, ct);
        var lease = Guid.NewGuid();
        var run = await journal.AcquireAsync(scope, lease, clock.GetUtcNow(), ct);
        var request = Parse(run.Payload);
        try
        {
            var binding = bindings.Resolve(run.BindingKey, scope, request.ProfileId, request.CallbackUrl);
            if (binding.Fingerprint != run.BindingFingerprint || binding.SandboxId != run.SandboxId)
                throw new ExternalAgentException("gateway-binding-changed");
            if (run.Phase == GatewayPhase.CallbackReady)
            {
                var receipt = await delivery.DeliverAsync(binding, run.FrozenResult!, ct);
                run = await Change(x => x.AcknowledgeCallback(lease, receipt, Now()), ct);
                return run;
            }
            var adapter = bindings.Adapter(binding);
            if (run.Phase == GatewayPhase.Reserved)
            {
                run = await Change(x => x.BeginPrepare(lease, Now()), ct);
                var handle = await adapter.PrepareAsync(request.Input, ct);
                RequireHandle(handle, scope, adapter);
                run = await Change(x => x.SavePreparation(lease, JsonSerializer.Serialize(handle, Json), Now()), ct);
            }
            if (run.Phase == GatewayPhase.Prepared)
            {
                var handle = Handle(run);
                run = await Change(x => x.BeginSubmit(lease, Now()), ct);
                var submitted = await adapter.SubmitAsync(request.Input, handle, ct);
                RequireHandle(submitted, scope, adapter);
                run = await Change(x => x.SaveSubmission(lease, JsonSerializer.Serialize(submitted, Json), Now()), ct);
                return run;
            }
            // Lost session creation / Hermes submission without a native ID is
            // an operator reconciliation hold, never a blind new session/run.
            if (run.PreparedHandle is null || Handle(run).NativeId is null) return run;
            var observation = await adapter.ObserveAsync(Handle(run), ct);
            await journal.SaveUsageAsync(scope, lease, Now(), observation.Usage, ct);
            if (observation.State == ExternalAgentState.AwaitingApproval)
                run = await Change(x => x.HoldApproval(lease, observation.NativeApprovalId, Now()), ct);
            else
                run = await Change(x => x.Observe(lease, Terminal(observation.State), Now()), ct);
            if (run.Phase == GatewayPhase.AwaitingEvidence && Terminal(observation.State))
            {
                var evidence = await sandbox.CollectAsync(binding, observation.State, ct);
                if (evidence.ExecutorExited && evidence.Result is not null)
                {
                    ValidateResult(evidence.Result, observation.State, run.StopPending);
                    run = await Change(x => x.FreezeResult(lease,
                        JsonSerializer.Serialize(evidence.Result, Json), true, Now()), ct);
                }
            }
            return run;
        }
        catch (ExternalAgentException ex)
        {
            // Leave a callback-ready result untouched; its exact delivery can be
            // retried using the API's existing inbox deduplication contract.
            if (run.Phase != GatewayPhase.CallbackReady)
                run = await Change(x => x.Reconcile(lease, ex.Code, Now()), CancellationToken.None);
            return run;
        }
        finally
        {
            // If the lease expired, stale operations cannot mutate or release it.
            try { run = await Change(x => x.ReleaseLease(lease, Now()), CancellationToken.None); }
            catch (GatewayConflictException) { }
        }
        DateTimeOffset Now() => clock.GetUtcNow();
        Task<GatewayRun> Change(Action<GatewayRun> action, CancellationToken token) => journal.ChangeAsync(scope, run.Version, action, token);
    }

    public async Task<GatewayRun> StopAsync(ExternalAgentScope scope, CancellationToken ct)
    {
        await StopCoreAsync(scope, ct);
        return await journal.ReadAsync(scope, ct);
    }
    private async Task<GatewayRun> StopCoreAsync(ExternalAgentScope scope, CancellationToken ct)
    {
        var prior = await journal.ReadAsync(scope, ct);
        if (prior.PreparedHandle is null || Handle(prior).NativeId is null || prior.Phase is
            GatewayPhase.Reserved or GatewayPhase.Preparing or GatewayPhase.Prepared or GatewayPhase.CallbackReady or GatewayPhase.Delivered)
            throw new ExternalAgentException("gateway-stop-requires-reconciliation");
        var lease = Guid.NewGuid();
        var run = await journal.AcquireAsync(scope, lease, clock.GetUtcNow(), ct);
        try
        {
            var request = Parse(run.Payload);
            var binding = bindings.Resolve(run.BindingKey, scope, request.ProfileId, request.CallbackUrl);
            if (binding.Fingerprint != run.BindingFingerprint || binding.SandboxId != run.SandboxId)
                throw new ExternalAgentException("gateway-binding-changed");
            run = await journal.ChangeAsync(scope, run.Version, x => x.RequestStop(lease, clock.GetUtcNow()), ct);
            await bindings.Adapter(binding).RequestStopAsync(Handle(run), ct);
            return run; // Acknowledgement NEVER releases capacity or completes.
        }
        catch (ExternalAgentException ex)
        { return await journal.ChangeAsync(scope, run.Version, x => x.Reconcile(lease, ex.Code, clock.GetUtcNow()), CancellationToken.None); }
        finally
        {
            var current = await journal.ReadAsync(scope, CancellationToken.None);
            try { await journal.ChangeAsync(scope, current.Version, x => x.ReleaseLease(lease, clock.GetUtcNow()), CancellationToken.None); }
            catch (GatewayConflictException) { }
        }
    }

    private static ExternalAgentHandle Handle(GatewayRun run) => JsonSerializer.Deserialize<ExternalAgentHandle>(run.PreparedHandle!, Json)
        ?? throw new ExternalAgentException("gateway-handle-invalid");
    private static void RequireHandle(ExternalAgentHandle handle, ExternalAgentScope scope, IExternalAgentAdapter adapter)
    {
        if (handle.Scope != scope || handle.Engine != adapter.Engine || handle.BindingFingerprint != adapter.BindingFingerprint ||
            handle.PromptId != "msg_" + scope.RunId.ToString("N"))
            throw new ExternalAgentException("gateway-handle-invalid", true);
    }
    private static bool Terminal(ExternalAgentState state) => state is ExternalAgentState.Completed or
        ExternalAgentState.Cancelled or ExternalAgentState.Failed or ExternalAgentState.Interrupted;
    private static void ValidateResult(HarnessResult result, ExternalAgentState state, bool stopped)
    {
        if (result.Outcome is not ("Succeeded" or "Failed" or "NeedsInput") ||
            string.IsNullOrWhiteSpace(result.Summary) || Encoding.UTF8.GetByteCount(result.Summary) > 16 * 1024 ||
            result.Evidence?.Count > 100 || result.Evidence?.Any(x => x is null ||
                x.Kind is not ("Commit" or "Test" or "Artifact") ||
                x.Outcome is not ("NotApplicable" or "Passed" or "Failed" or "Skipped") ||
                string.IsNullOrWhiteSpace(x.Reference) || x.Reference.Length > 2000 ||
                x.CommitSha is not null && !System.Text.RegularExpressions.Regex.IsMatch(x.CommitSha, @"\A[0-9a-fA-F]{40,64}\z")) == true ||
            result.Outcome == "Succeeded" &&
            (state != ExternalAgentState.Completed || stopped || result.Evidence?.Count is not > 0))
            throw new ExternalAgentException("gateway-evidence-invalid");
    }

    private sealed record Parsed(ExternalAgentInput Input, Guid ProfileId, string CallbackUrl);
    private static Parsed Parse(string body)
    {
        try
        {
            if (Encoding.UTF8.GetByteCount(body) > 256 * 1024) throw new JsonException();
            using var doc = JsonDocument.Parse(body);
            var protocol = doc.RootElement.GetProperty("protocol").GetString();
            Guid run; Guid org; Guid workspace; Guid profile; string callback; string? instructions; WorkContextView context;
            if (protocol == "neo-harness/v1")
            {
                var r = JsonSerializer.Deserialize<HarnessRequest>(body, Json)!;
                run = r.RunId; org = r.OrganizationId; workspace = r.WorkspaceId;
                profile = r.AgentProfileId; callback = r.CallbackUrl; instructions = r.Instructions;
                context = WorkContextProjection.Create(r.Work);
            }
            else if (protocol == "neo-harness/v2")
            {
                var r = JsonSerializer.Deserialize<HarnessCompactRequest>(body, Json)!;
                run = r.RunId; org = r.OrganizationId; workspace = r.WorkspaceId;
                profile = r.AgentProfileId; callback = r.CallbackUrl; instructions = r.Instructions; context = r.Context;
            }
            else throw new JsonException();
            if (context.Unchanged || profile == Guid.Empty || context.Item.Id == Guid.Empty) throw new JsonException();
            var prompt = JsonSerializer.Serialize(context, Json);
            if (Encoding.UTF8.GetByteCount(prompt) > 64 * 1024 || Encoding.UTF8.GetByteCount(instructions ?? "") > 16 * 1024)
                throw new JsonException();
            // Snapshot model, branch, skillPath and task prose do not select a
            // route, workspace, executable or instruction-file authority.
            return new(new(new(org, workspace, context.Item.ProjectId, run), prompt, instructions), profile, callback);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or NullReferenceException or ArgumentException)
        { throw new ExternalAgentException("gateway-request-invalid"); }
    }
}
