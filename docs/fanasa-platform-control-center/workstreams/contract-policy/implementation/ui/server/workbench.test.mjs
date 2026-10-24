import test, { before, after } from 'node:test';
import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdir } from 'node:fs/promises';
import { join, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const directory = dirname(fileURLToPath(import.meta.url));
const dataDirectory = join(directory, 'tests', '.runs', `${Date.now()}-${process.pid}`);
const base = 'http://127.0.0.1:4197/api/workbench';
let child;
let logs = '';
function start() {
  child = spawn('dotnet', [join(directory, 'bin', 'Debug', 'net10.0', 'Fanasa.ContractPolicy.Workbench.dll')], {
    cwd: directory, windowsHide: true,
    env: { ...process.env, WORKBENCH_PORT: '4197', WORKBENCH_DATA_DIR: dataDirectory,
      Logging__LogLevel__Default: 'Warning' }, stdio: ['ignore', 'pipe', 'pipe']
  });
  child.stdout.on('data', data => { logs += data; });
  child.stderr.on('data', data => { logs += data; });
}
async function ready() {
  for (let i = 0; i < 600; i++) {
    if (child.exitCode !== null) throw new Error(`Engine exited: ${logs}`);
    try { if ((await fetch(base)).ok) return; } catch {}
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  throw new Error(`Engine never became ready: ${logs}`);
}
async function stop() {
  if (!child || child.exitCode !== null || child.signalCode !== null) return;
  const exited = new Promise(resolve => child.once('exit', resolve));
  child.kill();
  await exited;
}
async function request(path = '', body, expected = 200, origin = 'http://127.0.0.1:4186') {
  const response = await fetch(base + path, body === undefined ? {} : {
    method: 'POST', headers: { 'content-type': 'application/json', ...(origin ? { origin } : {}) }, body: JSON.stringify(body)
  });
  const text = await response.text();
  assert.equal(response.status, expected, `${path}: ${text}`);
  return text ? JSON.parse(text) : null;
}
async function reset() { const state = await request(); return request('/reset', { expectedRevision: state.revision }); }
async function evaluate(scenarioId, policyId) { return request('/evaluate', { scenarioId, ...(policyId ? { policyId } : {}) }); }
async function execute(run, failObligation) { return request('/execute', { decisionId: run.decisionId, scenarioId: run.scenarioId, ...(failObligation ? { failObligation } : {}) }); }
async function publish(input = {}) {
  let state = await request();
  state = await request('/draft', { expectedRevision: state.revision, author: 'Author A', reason: 'Test native typed validation', policy: {
    name: 'Native test policy', explicitDeny: false, zoneConstraints: ['ir-private-1'], requestsPerSecond: 5, burstCapacity: 10, monthlyQuota: 1000, ...input
  } });
  const draft = state.drafts.at(-1);
  const published = await request('/publish', { expectedRevision: state.revision, draftId: draft.draftId, reviewer: 'Reviewer B', reason: 'Independent review' });
  return { state: published, policy: published.policies.at(-1), draft };
}
async function bind(policy) {
  const state = await request();
  return request('/bind', { expectedRevision: state.revision, policyId: policy.policyId, reviewer: 'Reviewer B', reason: 'Accept trusted synthetic projection event' });
}

before(async () => { await mkdir(dataDirectory, { recursive: true }); start(); await ready(); });
after(async () => { await stop(); });

test('native profile catalog, four decisions and fail-closed PEP scenarios', async () => {
  await reset();
  const state = await request();
  assert.equal(state.native.engine, 'ContractPolicyModule.Evaluator');
  assert.equal(state.native.pep, 'ContractPolicyModule.Pep');
  assert.equal(state.native.schemas.length, 10);
  assert.equal(state.capabilities.productionConnected, false);
  assert.equal(state.resources.length, 2);
  for (const scenario of state.scenarios) {
    const run = await evaluate(scenario.id);
    assert.equal(run.rawDecision.decision, scenario.expectedDecision, scenario.id);
    assert.equal(run.context.action, 'organization.unit.save');
    assert.equal(run.policySelection, 'accepted');
    if (scenario.id === 'unknown-obligation') assert.equal(run.rawDecision.obligations.length, 2, 'raw native output unchanged');
    const result = await execute(run);
    assert.equal(result.reasonCode, scenario.expectedExecution, scenario.id);
    assert.equal(result.accepted, scenario.id === 'permit');
  }
  const after = await request();
  assert.equal(after.usage.admitted, 1);
  assert.equal(after.operations.length, 1);
  assert.equal(after.resources.find(x => x.resourceId === 'unit-primary').saveCount, 1);
  assert.equal(after.resources.find(x => x.resourceId === 'unit-foreign').saveCount, 0);
});

test('execution is atomic, failure retries, concurrent replay and persisted receipt', async () => {
  await reset();
  const run = await evaluate('permit');
  const failed = await execute(run, 'admit-allocation');
  assert.equal(failed.accepted, false);
  assert.equal(failed.reasonCode, 'admission-adapter-unavailable');
  assert.equal((await request()).usage.admitted, 0);
  assert.equal((await request()).resources[0].saveCount, 0);
  const results = await Promise.all(Array.from({ length: 6 }, () => execute(run)));
  assert.equal(results.filter(x => !x.idempotent).length, 1);
  assert.equal(new Set(results.map(x => x.operation.operationId)).size, 1);
  let state = await request();
  assert.equal(state.usage.admitted, 1);
  assert.equal(state.operations.length, 1);
  assert.equal(state.resources[0].saveCount, 1);
  await stop(); start(); await ready();
  const replay = await execute(run);
  assert.equal(replay.idempotent, true);
  assert.equal((await request()).usage.admitted, 1);
  const resourceFailure = await evaluate('permit');
  assert.equal((await execute(resourceFailure, 'resource-tenant')).reasonCode, 'resource-adapter-unavailable');
  assert.equal((await execute(resourceFailure)).accepted, true);
  state = await request(); assert.equal(state.usage.admitted, 2); assert.equal(state.resources[0].saveCount, 2);
  const file = (await request('/export')).state;
  assert.equal(file.operations.length, file.admitted);
  assert.equal(Object.keys(file.executions).length, 2);
});

test('publication, independent review, conflicts, explicit binding and independent versions', async () => {
  let state = await reset();
  const original = structuredClone(state.projection);
  const staleRevision = state.revision;
  state = await request('/draft', { expectedRevision: staleRevision, author: 'Author A', reason: 'Change policy only', policy: {
    name: 'Deny immutable policy', explicitDeny: true, zoneConstraints: ['ir-private-1'], requestsPerSecond: 5, burstCapacity: 10, monthlyQuota: 1000
  } });
  const draft = state.drafts.at(-1);
  await request('/publish', { expectedRevision: staleRevision, draftId: draft.draftId, reviewer: 'Reviewer B', reason: 'Stale management write' }, 409);
  const denial = await request('/publish', { expectedRevision: state.revision, draftId: draft.draftId, reviewer: 'author a', reason: 'Same author forbidden' }, 409);
  assert.equal(denial.error, 'independent-review-required');
  state = await request('/publish', { expectedRevision: state.revision, draftId: draft.draftId, reviewer: 'Reviewer B', reason: 'Publish independent review' });
  const policy = state.policies.at(-1);
  assert.deepEqual(state.projection, original, 'publish does not accept a contract snapshot');
  const candidate = await evaluate('permit', policy.policyId);
  assert.equal(candidate.rawDecision.decision, 'Deny');
  assert.equal((await execute(candidate)).reasonCode, 'candidate-not-bound');
  const oldAccepted = await evaluate('permit'); assert.equal(oldAccepted.rawDecision.decision, 'Permit');
  state = await bind(policy);
  assert.equal(state.projection.contractVersion, original.contractVersion + 1);
  assert.equal(state.projection.policy.policyVersion, policy.policyVersion);
  assert.equal(state.projection.limits.configRevision, original.limits.configRevision, 'policy-only bind retains independent config revision');
  assert.equal((await execute(oldAccepted)).reasonCode, 'binding-changed');
  assert.equal((await evaluate('permit')).rawDecision.decision, 'Deny');
});

test('local quota, rate, retired policy and no counter reset on new configuration', async () => {
  await reset();
  let published = await publish({ monthlyQuota: 1 }); await bind(published.policy);
  const first = await evaluate('permit'); assert.equal((await execute(first)).accepted, true);
  const second = await evaluate('permit'); assert.equal((await execute(second)).reasonCode, 'quota-exhausted');
  assert.equal((await request()).usage.admitted, 1);
  const previousConfig = (await request()).projection.limits.configRevision;
  published = await publish({ monthlyQuota: 2 });
  let state = await bind(published.policy);
  assert.equal(state.projection.limits.configRevision, previousConfig + 1);
  assert.equal(state.usage.admitted, 1, 'configuration does not reset shared allocation usage');
  const retained = await evaluate('permit');
  state = await request('/retire', { expectedRevision: state.revision, policyId: published.policy.policyId, reviewer: 'Reviewer B', reason: 'Retirement prevents execution' });
  assert.equal((await execute(retained)).reasonCode, 'retired-policy');
  await reset();
  published = await publish({ requestsPerSecond: 0.000001, burstCapacity: 1 }); await bind(published.policy);
  assert.equal((await execute(await evaluate('permit'))).accepted, true);
  assert.equal((await execute(await evaluate('permit'))).reasonCode, 'rate-exhausted');
  assert.equal((await request()).usage.admitted, 1);
});

test('binding a new rate preserves tokens refilled under the previous configuration', async () => {
  await reset();
  let published = await publish({ requestsPerSecond: 1, burstCapacity: 1 }); await bind(published.policy);
  assert.equal((await execute(await evaluate('permit'))).accepted, true);
  await new Promise(resolve => setTimeout(resolve, 1200));
  published = await publish({ requestsPerSecond: 0.000001, burstCapacity: 1 });
  const state = await bind(published.policy);
  assert.ok(state.usage.tokens > 0.99, 'elapsed old-rate refill is preserved at binding transition');
  assert.equal(state.usage.admitted, 1, 'binding does not reset monthly usage');
  assert.equal((await execute(await evaluate('permit'))).accepted, true);
  assert.equal((await execute(await evaluate('permit'))).reasonCode, 'rate-exhausted');
  assert.equal((await request()).usage.admitted, 2);
});

test('server catalog controls attributes and HTTP mutation boundaries', async () => {
  await reset();
  const unknown = await request('/evaluate', { scenarioId: 'caller-invented' }, 400); assert.equal(unknown.error, 'unknown-scenario');
  await request('/evaluate', { scenarioId: 'permit', context: { subject: { authenticationValid: true } } }, 400);
  await request('/evaluate', { scenarioId: 'permit' }, 403, null);
  await request('/evaluate', { scenarioId: 'permit' }, 403, 'https://foreign.invalid');
  const run = await evaluate('permit');
  await request('/execute', { decisionId: run.decisionId, scenarioId: 'no-grant' }, 409);
  await request('/execute', { decisionId: run.decisionId, scenarioId: run.scenarioId, failObligation: 'caller-obligation' }, 400);
  const state = await request();
  await request('/draft', { expectedRevision: state.revision, author: 'A', reason: 'Invalid typed projection', policy: {
    name: 'Invalid', explicitDeny: false, zoneConstraints: ['unsupported-zone'], requestsPerSecond: 0, burstCapacity: 0, monthlyQuota: -1
  } }, 400);
  assert.equal((await request()).revision, state.revision);
  assert.equal((await request('/export')).productionConnected, false);
  assert.ok((await request('/history')).some(x => x.type === 'decision-evaluated'));
});
