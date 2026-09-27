# Durable simulation runs

NAO-009 adds the persisted FakeHarness scenario. It is **not an external agent**:
no shell, repository change, model request, fabricated commit, test result or
artifact is produced. External authenticated callbacks/HTTP harness are NAO-011.
Optional GitHub/GitLab/Azure work providers are separate future adapters; see
[WORK-PROVIDERS.md](WORK-PROVIDERS.md). Native work management remains active.

## Prerequisites and opt-in

1. Build the product solution and explicitly apply its migrations using the
   [provisioner](DATABASE.md). `DurableAgentRuns` adds `nao.AgentRuns` and nullable
   delivery links, preserving existing receipts and their fingerprints.
2. Configure an organization/workspace/project, two enabled role profiles and
   one enabled `fake` agent per role through the scoped API. Bootstrap of the
   initial organization/workspace still uses the forthcoming installer or a
   controlled seed; startup does not invent an admin or membership.
3. Create a workflow with developer Review -> reviewer Ready and reviewer
   Review -> Done. For a pure simulation demonstration do not require nonexistent
   commits/tests/artifacts. Real gates are never bypassed by the fake harness.
4. The API caller needs `read` and `execute` grants plus its chat header.
   Task creation/status also needs `write`, configuration `configure` and
   independent approval `approve`. Enable `Orchestration__SimulationEnabled=true`
   separately on API and worker. Both default to disabled; API never hosts a
   dispatcher. Starting an unconfigured/disabled worker exits without dispatch.

The worker uses Neo's Hangfire integration and EF Outbox claims/retries. It is a
generic host with no HTTP listener or Hangfire dashboard. It needs an **existing,
dedicated jobs catalog**, separate from the product database, with permission
for Hangfire to maintain its own schema. For the default installation an operator
can explicitly create `NeoAgentOrchestration_Jobs`; the product migration runner
must not be pointed at a catalog containing Hangfire or legacy tables.

Supply credentials privately through environment/secret storage, not committed
settings, terminal transcripts, task notes or exported bundles:

| Setting | Value/purpose |
| --- | --- |
| `NEO_ORCHESTRATION_SQL` or `ConnectionStrings__Orchestration` | Product SQL connection |
| `Orchestration__DatabaseName` | `NeoAgentOrchestration` by default; must match connection |
| `Orchestration__SimulationEnabled` | Explicit `true` to enable this host |
| `Orchestration__JobsDatabaseName` | `NeoAgentOrchestration_Jobs` by default |
| `Hangfire__Storage` | `SqlServer` for this worker |
| `Hangfire__ConnectionString` | Independent jobs connection |
| `Hangfire__WorkerCount` | Positive worker count, e.g. `2` |
| `Hangfire__Queues__0` | `outbox` (required queue) |

```powershell
dotnet run --project products/Neo.AgentOrchestration/src/Neo.AgentOrchestration.Worker --no-build
```

Startup checks product connectivity/pending migrations, registers the minutely
dispatcher and enqueues an initial scan. It does not migrate the product, import
legacy runs or install an external provider. The dispatcher processes up to 15
eligible messages per scan; a six-message chain may need several minutes.
Each installation must have its own jobs catalog; never share this recurring
schedule/queue between different product databases. Stop only its own worker
process to pause dispatch. Queue messages and product state survive a restart.

## HTTP scenario

All routes use the organization/workspace base in [API.md](API.md). Create a task
then move it to Ready. POST `items/{id}/runs` with:

```json
{
  "requestId": "<new-guid>",
  "expectedWorkItemVersion": "<current-item-version>",
  "workflowId": "<workflow-id>",
  "expectedWorkflowVersion": "<current-workflow-version>",
  "roleId": "<developer-role-id>",
  "agentProfileId": "<developer-agent-id>",
  "branch": "simulation-only",
  "simulationOutcome": "Succeeded"
}
```

The response is **202 Accepted**, with a resolvable run Location, not completed
work. `requestId` is the stable run ID/idempotency key. Exact retries from the
same authenticated actor/chat return the existing run even after it advances;
changing the target/body under that key conflicts. Stale versions, a busy role,
incomplete dependencies, disabled/ambiguous profiles or a non-fake provider are
rejected before an execution claim is committed.

GET `items/{id}/runs` or `runs/{id}` to observe:

```text
Queued -> AwaitingResult -> Succeeded / Failed / NeedsInput
                              |
                         EvaluateWorkflow
                              |
                 Waiting / HandedOff -> next run / Completed
```

The dispatch, internal callback and workflow evaluation are separate durable
messages. The worker receives its business session from `SqlWorkDeliveryExecutor`;
receipt, task effects, follow-up message and Processed state share that SQL
transaction. No second retry engine or independent transaction is introduced.
Run details expose operation/outbox IDs, delivery state, attempts and sanitized
errors. Result and workflow decision are separate: Succeeded may still be Waiting.
Profiles/instructions are snapshotted; workflow versions are pinned. Role/profile
disablement and workflow edits are checked again before dispatch. Configuration
change while waiting does not silently grant a different transition.

A successful result moves the task to Review and closes its interval. A failed
or needs-input result moves it to Blocked without automatic handoff. The fake
callback never creates evidence. A satisfied transition atomically claims the
next role/run and queues its dispatch, or completes the task after checking
dependencies and children. At 20 hops, cyclic handoff waits for human review.

Missing approval/evidence, busy next role, changed versions or unavailable target
provider leaves an explicit Waiting reason. After resolving the cause, POST
`runs/{id}/evaluate` with a new stable `requestId`, current
`expectedWorkItemVersion` and `expectedWorkflowVersion`. This also returns 202.
Do not retry a failed gate blindly or invent versions. An independent approval
is bound to both versions; neither the managed owner nor its initiating user may
self-approve. Run-assignment subjects beginning with `neo-run:` are reserved and
cannot authenticate to user-facing workspace operations.

## Return control and limits

POST `runs/{id}/return-assignment` with `expectedWorkItemVersion` to return a
stopped Review/Blocked/Done item to the initiating user and current chat. Requires
read/execute and that same authenticated initiating subject; active runs and runs
that already handed off cannot be returned. This local recovery remains available
when simulation is disabled. It preserves result/history, records the return,
ends that run's automatic transition authority, and permits normal task editing
or archival by its new owner. Repeating with a stale version conflicts. If work
must run again, move it through Ready and start a new run explicitly.

The ordinary work timer starts at the claim: it includes queue/wait-for-result
time, **not measured CPU or active coding time**. Dispatch/result timestamps are
available separately. Elapsed budget is not completion percentage. Raw exception
details/provider responses are not included in delivery diagnostics.

Transient delivery errors follow Neo's 30-second retry/three-attempt contract.
Failed delivery is retained for inspection and is not automatically reset or
converted into a successful/negative agent result. A queued/awaiting run with
exhausted delivery may need operator reconciliation; this stage has no unsafe
force-release/reset endpoint. A timeout or missing callback is not proof of a
failed external operation. Real external adapters remain unimplemented.

## Verification

With a private `NEO_ORCHESTRATION_TEST_SQL` connection targeting only
`NeoAgentOrchestration_Verification`, run the product test project. Tests retain
their records. SQL tests execute the HTTP/scoped JWT scenario, fresh-context
dispatch/callback/handoff, duplicate requests/jobs, negative outcomes, gate
waiting, independent approval, changed configuration, return/archival and claims.

`SqlRunWorkerTests` additionally creates/uses only the isolated
`NeoAgentOrchestration_WorkerVerification` and
`NeoAgentOrchestration_JobsVerification` catalogs, runs a real Neo/Hangfire SQL
server host and checks the six committed deliveries. It accelerates dispatch
scans, not delivery execution, and stops only its own host. It does not run
against the shared kernel-test catalog or delete databases. Lease-crash recovery
is covered by timestamp-based SQL tests, not a claim of a killed-process drill.
No test constitutes real external agent execution, browser acceptance or migration
acceptance. Full standalone Web and external harness remain separate milestones.
