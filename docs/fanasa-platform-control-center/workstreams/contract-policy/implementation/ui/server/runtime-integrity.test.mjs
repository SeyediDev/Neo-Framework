import test, { after } from 'node:test';
import assert from 'node:assert/strict';
import { spawn, spawnSync } from 'node:child_process';
import { mkdtemp, readFile, writeFile, readdir } from 'node:fs/promises';
import { join, dirname, resolve } from 'node:path';
import { tmpdir } from 'node:os';
import { createServer } from 'node:net';
import { fileURLToPath } from 'node:url';

const directory = dirname(fileURLToPath(import.meta.url));
const assembly = process.env.WORKBENCH_TEST_ASSEMBLY
  ? resolve(process.env.WORKBENCH_TEST_ASSEMBLY)
  : join(directory, 'bin', 'Debug', 'net10.0', 'Fanasa.ContractPolicy.Workbench.dll');
const hosts = new Set();
const origin = 'http://127.0.0.1:4186';
const validPolicy = Object.freeze({ name: 'Reviewed unit save policy', explicitDeny: false,
  zoneConstraints: ['ir-private-1'], requestsPerSecond: 5, burstCapacity: 10, monthlyQuota: 1000 });

async function freePort() {
  const server = createServer();
  await new Promise((accept, reject) => { server.once('error', reject); server.listen(0, '127.0.0.1', accept); });
  const port = server.address().port;
  await new Promise((accept, reject) => server.close(error => error ? reject(error) : accept()));
  return port;
}

async function start(dataDirectory, { expectFailure = false } = {}) {
  const port = await freePort();
  const child = spawn('dotnet', [assembly], { cwd: directory, windowsHide: true,
    env: { ...process.env, WORKBENCH_PORT: String(port), WORKBENCH_DATA_DIR: dataDirectory,
      Logging__LogLevel__Default: 'Warning' }, stdio: ['ignore', 'pipe', 'pipe'] });
  const host = { child, dataDirectory, base: `http://127.0.0.1:${port}/api/workbench`, logs: '', error: null };
  hosts.add(host);
  child.stdout.on('data', data => { host.logs += data; });
  child.stderr.on('data', data => { host.logs += data; });
  child.on('error', error => { host.error = error; });
  for (let i = 0; i < 600; i++) {
    if (host.error) throw host.error;
    if (child.exitCode !== null || child.signalCode !== null) {
      if (expectFailure) { assert.notEqual(child.exitCode, 0, 'invalid state must fail startup'); return host; }
      throw new Error(`Engine exited (${child.exitCode ?? child.signalCode}): ${host.logs}`);
    }
    try {
      const response = await fetch(host.base, { signal: AbortSignal.timeout(1000) });
      if (response.ok) {
        assert.equal(expectFailure, false, 'invalid database was accepted as a ready engine');
        return host;
      }
    } catch (error) { if (error instanceof assert.AssertionError) throw error; }
    await new Promise(accept => setTimeout(accept, 100));
  }
  throw new Error(`Engine neither became ready nor failed startup: ${host.logs}`);
}

async function stop(host) {
  if (!host || !hosts.has(host) || host.child.exitCode !== null || host.child.signalCode !== null || host.error) {
    hosts.delete(host); return;
  }
  const ended = new Promise(accept => host.child.once('exit', accept));
  host.child.kill();
  await ended;
  hosts.delete(host);
}

async function isolated() { return start(await mkdtemp(join(tmpdir(), 'fanasa-workbench-integrity-'))); }

async function request(host, path = '', body, expected = 200) {
  const response = await fetch(host.base + path, body === undefined ? {} : {
    method: 'POST', headers: { 'content-type': 'application/json', origin }, body: JSON.stringify(body)
  });
  const text = await response.text();
  assert.equal(response.status, expected, `${path}: ${text}`);
  return text ? JSON.parse(text) : null;
}

async function sqlFile(host) {
  const files = (await readdir(host.dataDirectory)).filter(file => /\.(?:db|sqlite|sqlite3)$/.test(file));
  assert.equal(files.length, 1, 'one isolated SQLite database exists');
  return join(host.dataDirectory, files[0]);
}

function python(script, args) {
  const result = spawnSync(process.env.WORKBENCH_TEST_PYTHON || 'python', ['-c', script, ...args],
    { windowsHide: true, encoding: 'utf8', timeout: 15000 });
  if (result.error) throw result.error;
  assert.equal(result.status, 0, `Independent SQLite probe failed: ${result.stderr}`);
  return JSON.parse(result.stdout.trim());
}

async function inspectSql(host) {
  const file = await sqlFile(host);
  assert.equal((await readFile(file)).subarray(0, 16).toString(), 'SQLite format 3\0', 'storage is an actual SQLite database');
  return python(`import sqlite3,sys,json
c=sqlite3.connect('file:'+sys.argv[1]+'?mode=ro',uri=True)
names=[r[0] for r in c.execute("SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'")]
counts={n:c.execute('SELECT COUNT(*) FROM "'+n+'"').fetchone()[0] for n in names}
print(json.dumps({'integrity':c.execute('PRAGMA integrity_check').fetchone()[0],'foreignKeys':c.execute('PRAGMA foreign_key_check').fetchall(),'counts':counts}))
c.close()`, [file]);
}

async function evaluate(host, scenarioId = 'permit', policyId, proposedUnitName) {
  return request(host, '/evaluate', { scenarioId, ...(policyId ? { policyId } : {}),
    ...(proposedUnitName === undefined ? {} : { proposedUnitName }) });
}
async function execute(host, run, failObligation) {
  return request(host, '/execute', { decisionId: run.decisionId, scenarioId: run.scenarioId,
    ...(failObligation ? { failObligation } : {}) });
}
async function publish(host, changes = {}) {
  let state = await request(host);
  state = await request(host, '/draft', { expectedRevision: state.revision, author: 'Author A',
    reason: 'Reviewed real native configuration', policy: { ...validPolicy, ...changes } });
  const draft = state.drafts.at(-1);
  state = await request(host, '/publish', { expectedRevision: state.revision, draftId: draft.draftId,
    reviewer: 'Reviewer B', reason: 'Independent review' });
  return { policy: state.policies.at(-1), state };
}
async function bind(host, policy) {
  const state = await request(host);
  return request(host, '/bind', { expectedRevision: state.revision, policyId: policy.policyId,
    reviewer: 'Reviewer B', reason: 'Accept reviewed native definition' });
}

after(async () => { await Promise.all([...hosts].map(stop)); });

test('guided validation is native, read-only and distinguishes policy validity from independent review', async () => {
  const host = await isolated();
  try {
    const before = (await request(host, '/export')).state;
    const assessment = await request(host, '/validate', { policy: { ...validPolicy, name: '  Reviewed policy  ' },
      author: 'Author A', reviewer: 'Reviewer B' });
    assert.equal(assessment.valid, true);
    assert.equal(assessment.canPublish, true);
    assert.equal(assessment.normalizedPolicy.name, 'Reviewed policy');
    assert.deepEqual(assessment.errors, []);
    assert.ok(assessment.checks.length > 0, 'guided checks explain assessment');
    assert.ok(assessment.example, 'worked example is returned by the backend');
    assert.equal(assessment.example.expected.scenarioId, 'permit');
    assert.equal(assessment.example.expected.decision, 'Permit');
    assert.equal(assessment.example.expected.obligations.length, 2);
    assert.equal(assessment.example.steps.length, 6, 'guidance covers definition through durable effect');
    assert.ok(assessment.example.sizing.length > 0, 'the example explains why capacity values were chosen');
    assert.ok(Number.isFinite(Date.parse(assessment.checkedAt)));
    const sameReviewer = await request(host, '/validate', { policy: validPolicy, author: 'Author A', reviewer: 'author a' });
    assert.equal(sameReviewer.valid, true, 'policy is valid and may be drafted');
    assert.equal(sameReviewer.canPublish, false, 'same label is not independent review');
    const stoppingConfiguration = await request(host, '/validate', { policy: { ...validPolicy,
      explicitDeny: true, zoneConstraints: ['ir-private-2'], monthlyQuota: 0 }, author: 'Author A', reviewer: 'Reviewer B' });
    assert.equal(stoppingConfiguration.valid, true, 'blocking access may be an intentional valid definition');
    assert.equal(stoppingConfiguration.canPublish, true);
    for (const code of ['explicit-deny', 'zone-mismatch', 'zero-quota'])
      assert.ok(stoppingConfiguration.warnings.some(warning => warning.code === code), `${code} explains runtime impact`);
    const recommended = await request(host, '/validate', { policy: assessment.example.policy,
      author: assessment.example.author, reviewer: assessment.example.reviewer });
    assert.equal(recommended.valid, true, 'the backend example itself is valid');
    assert.equal(recommended.canPublish, true);
    assert.deepEqual((await request(host, '/export')).state, before, 'validation writes no revision, audit, decision or draft');
    const storage = await request(host, '/readiness');
    assert.equal(storage.storage.provider, 'SQLite');
    assert.equal(storage.storage.healthy, true);
    assert.equal(storage.ready, true);
    assert.equal(storage.canEvaluate, true);
  } finally { await stop(host); }
});

test('invalid configuration produces field guidance and cannot persist a draft', async () => {
  const host = await isolated();
  try {
    const before = (await request(host, '/export')).state;
    for (const [field, value] of [['name', ' '], ['zoneConstraints', ['unregistered-zone']],
      ['zoneConstraints', []], ['requestsPerSecond', 0], ['requestsPerSecond', 1000001],
      ['burstCapacity', 0], ['burstCapacity', 1000001], ['monthlyQuota', -1], ['monthlyQuota', 1000000001]]) {
      const policy = { ...validPolicy, [field]: value };
      const assessment = await request(host, '/validate', { policy, author: 'Author A', reviewer: 'Reviewer B' });
      assert.equal(assessment.valid, false, field);
      assert.equal(assessment.canPublish, false, field);
      assert.equal(assessment.normalizedPolicy, null, field);
      assert.ok(assessment.errors.some(error => error.field === field || error.field === `policy.${field}`), `${field} has actionable field guidance`);
      await request(host, '/draft', { expectedRevision: before.revision, policy,
        author: 'Author A', reason: 'Rejected invalid field' }, 400);
    }
    await request(host, '/validate', { policy: validPolicy, author: 'Author A', reviewer: 'Reviewer B',
      context: { subject: { authenticationValid: true } } }, 400);
    for (const proposedUnitName of ['', 'x', 'x'.repeat(121)])
      await request(host, '/evaluate', { scenarioId: 'permit', proposedUnitName }, 400);
    assert.deepEqual((await request(host, '/export')).state, before, 'all invalid validations/drafts leave persisted state unchanged');
    const probe = await inspectSql(host);
    assert.equal(probe.integrity, 'ok');
    assert.deepEqual(probe.foreignKeys, []);
  } finally { await stop(host); }
});

test('persisted trusted data and SQL effect records remain coherent through failed, concurrent and replayed execution', async () => {
  const host = await isolated();
  let restarted;
  try {
    const initial = (await request(host, '/export')).state;
    const context = initial.trustedContext;
    assert.ok(context, 'trusted attribute sources are persisted in the database');
    const projection = initial.projectionState.projection;
    assert.equal(context.membership.subjectId, context.subject.subjectId);
    assert.equal(context.grant.subjectId, context.subject.subjectId);
    assert.equal(context.membership.tenantId, projection.consumerTenantId);
    assert.equal(context.grant.consumerTenantId, projection.consumerTenantId);
    assert.equal(context.resource.productId, projection.productId);
    assert.ok(context.grant.actions.includes(context.action));
    const proposedUnitName = 'واحد فروش آزمون';
    const run = await evaluate(host, 'permit', undefined, proposedUnitName);
    assert.equal(run.rawDecision.decision, 'Permit');
    assert.equal(run.proposedUnitName, proposedUnitName);
    await request(host, '/execute', { decisionId: run.decisionId, scenarioId: run.scenarioId,
      proposedUnitName: 'واحد دست‌کاری‌شده' }, 400);
    assert.equal((await execute(host, run, 'admit-allocation')).accepted, false);
    let state = (await request(host, '/export')).state;
    assert.equal(state.admitted, 0);
    assert.equal(state.resources[0].saveCount, 0);
    assert.equal(state.resources[0].name, initial.resources[0].name, 'failed obligation does not save requested content');
    assert.equal(state.operations.length, 0);
    assert.equal(Object.keys(state.executions).length, 0);
    const results = await Promise.all(Array.from({ length: 8 }, () => execute(host, run)));
    assert.equal(results.filter(result => !result.idempotent).length, 1);
    assert.equal(new Set(results.map(result => result.operation.operationId)).size, 1);
    state = (await request(host, '/export')).state;
    assert.equal(state.admitted, 1);
    assert.equal(state.resources[0].saveCount, 1);
    assert.equal(state.resources[0].name, proposedUnitName, 'successful execution saves the actual proposed record content');
    assert.equal(state.operations.length, 1);
    assert.equal(state.operations[0].savedName, proposedUnitName);
    assert.equal(Object.keys(state.executions).length, 1);
    assert.equal((await request(host, '/readiness')).storage.healthy, true);
    const sql = await inspectSql(host);
    assert.equal(sql.integrity, 'ok'); assert.deepEqual(sql.foreignKeys, []);
    assert.ok(Object.keys(sql.counts).length >= 6, 'SQL has separately persisted operational entities');
    await stop(host);
    restarted = await start(host.dataDirectory);
    const replay = await execute(restarted, run);
    assert.equal(replay.idempotent, true);
    assert.equal(replay.operation.operationId, results[0].operation.operationId);
    assert.equal(replay.operation.savedName, proposedUnitName, 'replay retains the original saved payload');
    const afterRestart = (await request(restarted, '/export')).state;
    assert.equal(afterRestart.admitted, 1);
    assert.equal(afterRestart.resources[0].saveCount, 1);
    assert.equal(afterRestart.resources[0].name, proposedUnitName, 'actual record content survives process restart');
    assert.deepEqual(afterRestart.operations, state.operations);
    assert.deepEqual(afterRestart.trustedContext, context);
    assert.equal((await request(restarted, '/readiness')).storage.healthy, true);
  } finally { await stop(host); await stop(restarted); }
});

test('candidate publication, accepted binding and retirement enforce the current runtime definition', async () => {
  const host = await isolated();
  try {
    const initial = await request(host);
    const oldDecision = await evaluate(host);
    const { policy, state: published } = await publish(host, { explicitDeny: true });
    assert.deepEqual(published.projection, initial.projection, 'publishing a policy preserves accepted contract binding');
    const candidate = await evaluate(host, 'permit', policy.policyId);
    assert.equal(candidate.rawDecision.decision, 'Deny');
    assert.equal((await execute(host, candidate)).reasonCode, 'candidate-not-bound');
    const bound = await bind(host, policy);
    assert.equal(bound.projection.contractVersion, initial.projection.contractVersion + 1);
    assert.equal(bound.projection.limits.configRevision, initial.projection.limits.configRevision);
    assert.equal((await execute(host, oldDecision)).reasonCode, 'binding-changed');
    assert.equal((await evaluate(host)).rawDecision.decision, 'Deny');
    const { policy: permitPolicy } = await publish(host, { monthlyQuota: 1 });
    await bind(host, permitPolicy);
    const accepted = await evaluate(host);
    const result = await execute(host, accepted);
    assert.equal(result.accepted, true);
    assert.equal((await execute(host, await evaluate(host))).reasonCode, 'quota-exhausted');
    const beforeRetire = await request(host);
    await request(host, '/retire', { expectedRevision: beforeRetire.revision, policyId: permitPolicy.policyId,
      reviewer: 'Reviewer B', reason: 'Stop current accepted definition' });
    assert.equal((await execute(host, accepted)).reasonCode, 'retired-policy', 'retirement applies before receipt replay');
    const readiness = await request(host, '/readiness');
    assert.equal(readiness.storage.healthy, true, 'retirement is a valid administrative state');
    assert.equal(readiness.ready, false, 'retired bound policy is not ready to execute');
    assert.equal(readiness.canEvaluate, true);
    const after = await request(host);
    assert.equal(after.usage.admitted, 1);
    assert.equal(after.operations.length, 1);
    assert.equal(after.resources[0].saveCount, 1);
  } finally { await stop(host); }
});

test('valid legacy state migrates once without losing effects; invalid legacy state cannot be silently seeded', async () => {
  const source = await isolated();
  let imported, invalid;
  try {
    const successful = await execute(source, await evaluate(source));
    assert.equal(successful.accepted, true);
    const legacyState = (await request(source, '/export')).state;
    const legacyDirectory = await mkdtemp(join(tmpdir(), 'fanasa-workbench-legacy-'));
    const legacyFile = join(legacyDirectory, 'workbench-state.json');
    const original = JSON.stringify(legacyState);
    await writeFile(legacyFile, original);
    imported = await start(legacyDirectory);
    const readiness = await request(imported, '/readiness');
    assert.equal(readiness.storage.legacyImported, true);
    const importedState = (await request(imported, '/export')).state;
    assert.deepEqual(importedState.operations, legacyState.operations);
    assert.equal(importedState.resources[0].saveCount, 1);
    assert.equal(importedState.admitted, 1);
    assert.equal(await readFile(legacyFile, 'utf8'), original, 'original import input is preserved');
    for (const variant of ['missing-operation', 'binding-definition-mismatch', 'trusted-subject-mismatch']) {
      const invalidDirectory = await mkdtemp(join(tmpdir(), `fanasa-workbench-${variant}-`));
      const corrupt = structuredClone(legacyState);
      if (variant === 'missing-operation') corrupt.operations = [];
      else if (variant === 'binding-definition-mismatch') {
        const bound = corrupt.policies.find(policy => policy.policyId === corrupt.projectionState.projection.policy.policyId);
        bound.explicitDeny = !bound.explicitDeny;
      } else corrupt.trustedContext.grant.subjectId = '55555555-5555-4555-8555-555555555555';
      const invalidFile = join(invalidDirectory, 'workbench-state.json');
      const invalidContent = JSON.stringify(corrupt);
      await writeFile(invalidFile, invalidContent);
      invalid = await start(invalidDirectory, { expectFailure: true });
      assert.equal(await readFile(invalidFile, 'utf8'), invalidContent, 'corrupt import is preserved for repair');
      assert.match(invalid.logs, /invalid|integrity|consisten|receipt|operation/i, 'startup explains failed integrity');
      await stop(invalid);
    }
  } finally { await stop(source); await stop(imported); await stop(invalid); }
});

test('expired persisted membership and revoked grants deny access without classifying valid definitions as corrupt', async () => {
  const source = await isolated();
  try {
    const initial = (await request(source, '/export')).state;
    for (const variant of ['expired-membership', 'revoked-grant']) {
      const persisted = structuredClone(initial);
      assert.ok(persisted.trustedContext, 'a persisted source context exists');
      if (variant === 'expired-membership') {
        persisted.trustedContext.membership.validFrom = new Date(Date.now() - 20 * 86400000).toISOString();
        persisted.trustedContext.membership.validTo = new Date(Date.now() - 10 * 86400000).toISOString();
      } else persisted.trustedContext.grant.active = false;
      const dataDirectory = await mkdtemp(join(tmpdir(), `fanasa-workbench-${variant}-`));
      await writeFile(join(dataDirectory, 'workbench-state.json'), JSON.stringify(persisted));
      const host = await start(dataDirectory);
      try {
        const readiness = await request(host, '/readiness');
        assert.equal(readiness.storage.healthy, true, `${variant}: source data is coherent`);
        assert.equal(readiness.ready, false, `${variant}: source data does not currently authorize access`);
        assert.equal(readiness.canEvaluate, true, 'a valid denial configuration can still be evaluated');
        const run = await evaluate(host);
        assert.equal(run.rawDecision.decision, 'Deny', variant);
        assert.equal((await execute(host, run)).reasonCode, 'decision-denied');
        const after = (await request(host, '/export')).state;
        assert.equal(after.admitted, 0);
        assert.equal(after.resources[0].saveCount, 0);
        assert.equal(after.operations.length, 0);
      } finally { await stop(host); }
    }
  } finally { await stop(source); }
});

test('external SQLite corruption is detected during execution and remains failed closed after restart', async () => {
  for (const variant of ['normalized-counter', 'missing-resource']) {
    const host = await isolated();
    let rejected;
    try {
      const run = await evaluate(host);
      const file = await sqlFile(host);
      const corruption = python(`import sqlite3,sys,json
c=sqlite3.connect(sys.argv[1]);c.execute('PRAGMA foreign_keys=OFF');blocked=None
if sys.argv[2]=='normalized-counter':
 try:c.execute("UPDATE resources SET save_count=save_count+1 WHERE id='unit-primary'")
 except sqlite3.IntegrityError:blocked=True;c.rollback()
 else:blocked=False;c.rollback()
 c.execute('PRAGMA ignore_check_constraints=ON')
 c.execute("UPDATE resources SET save_count=save_count+1 WHERE id='unit-primary'")
else:c.execute("DELETE FROM resources WHERE id='unit-primary'")
c.commit();print(json.dumps({'changed':c.total_changes,'constraintBlocked':blocked}));c.close()`, [file, variant]);
      if (variant === 'normalized-counter') assert.equal(corruption.constraintBlocked, true, 'SQLite itself rejects inconsistent normalized counters');
      const before = await inspectSql(host);
      assert.equal(before.counts.operations, 0);
      assert.equal(before.counts.executions, 0);
      const readiness = await request(host, '/readiness');
      assert.equal(readiness.ready, false, variant);
      assert.equal(readiness.storage.healthy, false, variant);
      assert.equal(readiness.canEvaluate, false, 'corrupt storage blocks assessment as well as execution');
      assert.ok(readiness.storage.errors.length > 0, 'operator receives integrity repair evidence');
      const health = await fetch(host.base.replace('/api/workbench', '/health'));
      assert.equal(health.status, 503);
      for (const [path, body] of [['/evaluate', { scenarioId: 'permit' }],
        ['/execute', { decisionId: run.decisionId, scenarioId: run.scenarioId }],
        ['/draft', { expectedRevision: 1, author: 'Author A', reason: 'Cannot overwrite corrupt data', policy: validPolicy }],
        ['/reset', { expectedRevision: 1 }]]) {
        const refusal = await request(host, path, body, 503);
        assert.equal(refusal.error, 'storage-invalid', path);
      }
      assert.deepEqual((await inspectSql(host)).counts, before.counts, 'failed closed requests create no effects or audit writes');
      await stop(host);
      rejected = await start(host.dataDirectory, { expectFailure: true });
      assert.match(rejected.logs, /invalid|integrity|consisten/i);
      assert.deepEqual((await inspectSql(host)).counts, before.counts, 'restart never replaces corrupted data with a fresh seed');
    } finally { await stop(host); await stop(rejected); }
  }
});

test('two engine processes share one atomic effect and reject competing administrative revisions', async () => {
  const first = await isolated();
  let second;
  try {
    second = await start(first.dataDirectory);
    const run = await evaluate(first);
    async function concurrent(host, path, body) {
      const response = await fetch(host.base + path, { method: 'POST',
        headers: { 'content-type': 'application/json', origin }, body: JSON.stringify(body) });
      return { status: response.status, body: await response.json() };
    }
    const command = { decisionId: run.decisionId, scenarioId: run.scenarioId };
    const results = await Promise.all(Array.from({ length: 6 }, (_, index) =>
      concurrent(index % 2 ? first : second, '/execute', command)));
    for (const result of results) {
      assert.ok(result.status === 200 || result.status === 409, JSON.stringify(result));
      if (result.status === 409) assert.equal(result.body.error, 'storage-conflict');
      else assert.equal(result.body.accepted, true);
    }
    assert.equal(results.filter(result => result.status === 200 && !result.body.idempotent).length, 1,
      'one durable transaction wins across engine processes');
    for (const host of [first, second]) {
      const replay = await execute(host, run);
      assert.equal(replay.idempotent, true);
      const state = await request(host);
      assert.equal(state.usage.admitted, 1);
      assert.equal(state.resources[0].saveCount, 1);
      assert.equal(state.operations.length, 1);
      assert.equal((await request(host, '/readiness')).storage.healthy, true);
    }
    const before = await request(first);
    const edits = await Promise.all([first, second].map((host, index) => concurrent(host, '/draft', {
      expectedRevision: before.revision, author: 'Author A', reason: 'Competing configuration',
      policy: { ...validPolicy, name: `Competing draft ${index}` }
    })));
    assert.equal(edits.filter(result => result.status === 200).length, 1);
    assert.equal(edits.filter(result => result.status === 409).length, 1);
    assert.ok(['revision-conflict', 'storage-conflict'].includes(edits.find(result => result.status === 409).body.error));
    const state = await request(first);
    assert.equal(state.revision, before.revision + 1);
    assert.equal(state.drafts.length, 1);
    assert.equal(state.usage.admitted, 1, 'administrative conflict does not reset or duplicate usage');
    const database = await inspectSql(first);
    assert.equal(database.counts.operations, 1);
    assert.equal(database.counts.executions, 1);
    assert.equal(database.integrity, 'ok');
    assert.deepEqual(database.foreignKeys, []);
  } finally { await stop(first); await stop(second); }
});

test('a decision expiring while SQLite waits for the write lock never commits a record or consumption', { timeout: 120000 }, async () => {
  const host = await isolated();
  let locker;
  try {
    const before = await request(host);
    const run = await evaluate(host, 'permit', undefined, 'واحد نباید ذخیره شود');
    const delay = Date.parse(run.rawDecision.validUntil) - Date.now() - 3000;
    assert.ok(delay > 1000 && delay <= 30000, 'native decision has a bounded future deadline');
    await new Promise(accept => setTimeout(accept, delay));
    locker = spawn(process.env.WORKBENCH_TEST_PYTHON || 'python', ['-u', '-c',
      `import sqlite3,sys,time
c=sqlite3.connect(sys.argv[1]);c.execute('BEGIN IMMEDIATE');print('locked',flush=True)
time.sleep(4);c.rollback();c.close()`, await sqlFile(host)],
    { windowsHide: true, stdio: ['ignore', 'pipe', 'pipe'] });
    let output = '';
    await new Promise((accept, reject) => {
      locker.on('error', reject);
      locker.stdout.on('data', chunk => { output += chunk; if (output.includes('locked')) accept(); });
      locker.stderr.on('data', chunk => { output += chunk; });
      locker.on('exit', code => { if (!output.includes('locked')) reject(new Error(`SQLite locker failed ${code}: ${output}`)); });
    });
    assert.ok(Date.parse(run.rawDecision.validUntil) - Date.now() > 500,
      'execution begins before expiry, with the write lock already held');
    const result = await execute(host, run);
    assert.equal(result.accepted, false);
    assert.equal(result.reasonCode, 'expired-decision');
    const after = await request(host);
    assert.equal(after.resources[0].name, before.resources[0].name);
    assert.equal(after.resources[0].saveCount, 0);
    assert.equal(after.usage.admitted, 0);
    assert.equal(after.operations.length, 0);
    const durable = await inspectSql(host);
    assert.equal(durable.counts.operations, 0);
    assert.equal(durable.counts.executions, 0);
    assert.equal(durable.integrity, 'ok');
    assert.equal((await request(host, '/readiness')).storage.healthy, true);
  } finally {
    if (locker && locker.exitCode === null && locker.signalCode === null) locker.kill();
    await stop(host);
  }
});

test('coherent maintenance without a version bump rejects the stale full-state write and preserves trusted data', async () => {
  const host = await isolated();
  try {
    const state = (await request(host, '/export')).state;
    const probeDirectory = await mkdtemp(join(tmpdir(), 'fanasa-workbench-freshness-'));
    const exported = join(probeDirectory, 'exported-state.json');
    await writeFile(exported, JSON.stringify(state));
    const probe = spawnSync('dotnet', [join(directory, 'tests', 'DatabaseFreshness.Probe', 'bin', 'Debug', 'net10.0',
      'Fanasa.Workbench.DatabaseFreshness.Probe.dll'), join(probeDirectory, 'database'), exported,
    join(dirname(assembly), 'Schemas')], { windowsHide: true, encoding: 'utf8', timeout: 60000 });
    if (probe.error) throw probe.error;
    assert.equal(probe.status, 0, probe.stderr || probe.stdout);
    const evidence = JSON.parse(probe.stdout.trim());
    assert.equal(evidence.status, 'passed');
    assert.equal(evidence.checks, 4);
    assert.equal(evidence.staleWriteRejected, true);
    assert.equal(evidence.maintenancePreserved, true);
    assert.equal(evidence.freshWriteSucceeded, true);
    assert.equal((await request(host, '/readiness')).storage.healthy, true, 'the live host was never modified by the isolated regression');
  } finally { await stop(host); }
});

test('coherent source revocation invalidates retained Permit immediately and fresh evaluation follows restored database grants', async () => {
  const host = await isolated();
  try {
    async function maintain(mode) {
      const state = (await request(host, '/export')).state;
      const probeDirectory = await mkdtemp(join(tmpdir(), 'fanasa-workbench-sources-'));
      const exported = join(probeDirectory, 'exported-state.json');
      await writeFile(exported, JSON.stringify(state));
      const probe = spawnSync('dotnet', [join(directory, 'tests', 'DatabaseFreshness.Probe', 'bin', 'Debug', 'net10.0',
        'Fanasa.Workbench.DatabaseFreshness.Probe.dll'), host.dataDirectory, exported, join(dirname(assembly), 'Schemas'), mode],
      { windowsHide: true, encoding: 'utf8', timeout: 60000 });
      if (probe.error) throw probe.error;
      assert.equal(probe.status, 0, probe.stderr || probe.stdout);
      const evidence = JSON.parse(probe.stdout.trim());
      assert.equal(evidence.status, 'passed');
      assert.equal(evidence.mutationVersionRetained, true);
      assert.equal(evidence.active, mode === 'restore');
    }
    const before = await request(host);
    const retained = await evaluate(host, 'permit', undefined, 'واحد با نقش لغوشده');
    assert.equal(retained.rawDecision.decision, 'Permit');
    assert.match(retained.trustedSourcesFingerprint, /^[a-f0-9]{64}$/);
    await maintain('revoke');
    let readiness = await request(host, '/readiness');
    assert.equal(readiness.storage.healthy, true, 'coherent revocation is valid data');
    assert.equal(readiness.ready, false);
    assert.equal(readiness.canEvaluate, true);
    const refusal = await execute(host, retained);
    assert.equal(refusal.accepted, false);
    assert.equal(refusal.reasonCode, 'sources-changed');
    let state = (await request(host, '/export')).state;
    assert.equal(state.resources[0].name, before.resources[0].name);
    assert.equal(state.resources[0].saveCount, 0);
    assert.equal(state.admitted, 0);
    assert.equal(state.operations.length, 0);
    assert.equal(Object.keys(state.executions).length, 0);
    const current = await evaluate(host);
    assert.equal(current.rawDecision.decision, 'Deny');
    assert.equal((await execute(host, current)).reasonCode, 'decision-denied');
    await maintain('restore');
    readiness = await request(host, '/readiness');
    assert.equal(readiness.storage.healthy, true);
    assert.equal(readiness.ready, true);
    const restored = await evaluate(host, 'permit', undefined, 'واحد پس از بازگردانی');
    assert.equal(restored.rawDecision.decision, 'Permit');
    assert.equal((await execute(host, restored)).accepted, true);
    state = (await request(host, '/export')).state;
    assert.equal(state.resources[0].name, 'واحد پس از بازگردانی');
    assert.equal(state.resources[0].saveCount, 1);
    assert.equal(state.admitted, 1);
    assert.equal(state.operations.length, 1);
    assert.equal(Object.keys(state.executions).length, 1);
    assert.equal((await inspectSql(host)).integrity, 'ok');
  } finally { await stop(host); }
});

test('legacy decisions without source fingerprints remain valid history and require fresh evaluation before execution', async () => {
  const source = await isolated();
  let legacy;
  try {
    const oldRun = await evaluate(source, 'permit', undefined, 'واحد تصمیم قدیمی');
    const state = (await request(source, '/export')).state;
    delete state.decisions.find(run => run.decisionId === oldRun.decisionId).trustedSourcesFingerprint;
    const dataDirectory = await mkdtemp(join(tmpdir(), 'fanasa-workbench-legacy-sources-'));
    await writeFile(join(dataDirectory, 'workbench-state.json'), JSON.stringify(state));
    legacy = await start(dataDirectory);
    const readiness = await request(legacy, '/readiness');
    assert.equal(readiness.storage.healthy, true, 'historical source proof absence is structurally supported');
    assert.equal(readiness.ready, true, 'current trusted source definitions remain valid');
    const refusal = await execute(legacy, oldRun);
    assert.equal(refusal.accepted, false);
    assert.equal(refusal.reasonCode, 'sources-changed');
    let persisted = (await request(legacy, '/export')).state;
    assert.equal(persisted.decisions.find(run => run.decisionId === oldRun.decisionId).trustedSourcesFingerprint, undefined,
      'null source proof is omitted to retain legacy serialization and checksum compatibility');
    assert.equal(persisted.admitted, 0);
    assert.equal(persisted.operations.length, 0);
    assert.equal(persisted.resources[0].saveCount, 0);
    const fresh = await evaluate(legacy, 'permit', undefined, 'واحد با تصمیم تازه');
    assert.match(fresh.trustedSourcesFingerprint, /^[a-f0-9]{64}$/);
    assert.equal((await execute(legacy, fresh)).accepted, true);
    persisted = (await request(legacy, '/export')).state;
    assert.equal(persisted.admitted, 1);
    assert.equal(persisted.resources[0].saveCount, 1);
    assert.equal(persisted.resources[0].name, 'واحد با تصمیم تازه');
    assert.equal((await request(legacy, '/readiness')).storage.healthy, true);
  } finally { await stop(source); await stop(legacy); }
});
