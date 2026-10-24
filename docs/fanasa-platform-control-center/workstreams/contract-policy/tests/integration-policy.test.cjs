'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { evaluate, validateProjection, projectEvent, counterKey, canonicalScope, pepCanExecute } = require('../sample/contract-policy.cjs');
const { loadSchema, validateSchema } = require('../sample/schema-validator.cjs');
const read = name => JSON.parse(fs.readFileSync(path.join(__dirname, '..', 'fixtures', name), 'utf8'));
const projection = read('integration-projection.v2.json');
const context = read('integration-context.v2.json');
const event = read('integration-projection-event.v2.json');
const generated = read('integration-decision.v2.json');
const binding = JSON.parse(fs.readFileSync(path.resolve(__dirname, '../../../integration/fixture-binding.v1.json'), 'utf8'));
const ADMIT = 'urn:fanasa:obligation:admit-allocation:1';
const RESOURCE = 'urn:fanasa:obligation:resource-tenant:1';
const fulfilled = [ADMIT, RESOURCE];
const clone = value => structuredClone(value);
const freeze = value => { if (value && typeof value === 'object') { Object.values(value).forEach(freeze); Object.freeze(value); } return value; };
const project = (state, input) => projectEvent(state, input, 'fanasa-contracts');
function amendment(version, mutate = () => {}) {
  const value = clone(event);
  value.eventId = `fixture-organization-event-${version}`;
  value.aggregateVersion = version;
  value.projection.contractVersion = version;
  mutate(value.projection);
  return value;
}

test('common Permit is the actual pure evaluator output with the common UUID tuple', () => {
  const before = clone(projection);
  const actual = evaluate(freeze(clone(projection)), freeze(clone(context)));
  assert.deepEqual(actual, generated);
  assert.deepEqual(projection, before);
  assert.deepEqual(validateSchema(loadSchema('decision.v2.schema.json'), actual), []);
  assert.equal(actual.schemaVersion, '2.0.0');
  assert.equal(actual.profileId, 'fanasa.contract-policy/2');
  assert.equal(actual.decision, 'Permit');
  assert.equal(projection.ownerTenantId, binding.ownerTenantId);
  assert.equal(projection.consumerTenantId, binding.consumerTenantId);
  assert.equal(actual.requestId, 'g-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa');
  assert.deepEqual(actual.policyReferences, [{ policyId: binding.policyId, policyVersion: binding.policyVersion }]);
  assert.equal(actual.projectionReference.contractId, binding.contractId);
  assert.equal(actual.projectionReference.contractVersion, binding.contractVersion);
  const limits = actual.obligations.find(x => x.id === ADMIT).parameters;
  assert.equal(limits.configRevision, binding.configRevision);
  assert.notEqual(limits.configRevision, binding.policyVersion);
  assert.notEqual(limits.configRevision, binding.contractVersion);
  assert.equal(limits.allocationId, binding.allocationId);
  assert.equal(limits.canonicalScope, binding.canonicalScope);
  assert.equal(limits.rate.requestsPerSecond, binding.requestsPerSecond);
  assert.equal(limits.rate.burstCapacity, binding.burstCapacity);
  assert.equal(limits.quota.requests, binding.quotaRequests);
  assert.equal(limits.meter, 'api.request.admitted');
  assert.deepEqual(actual.obligations.find(x => x.id === RESOURCE).parameters, { consumerTenantId: binding.consumerTenantId, productId: binding.productId, environment: binding.environment, action: binding.action });
  assert.equal(actual.validUntil, '2026-10-10T06:30:29.000Z');
  assert.equal(pepCanExecute(actual, fulfilled, '2026-10-10T06:30:00.000Z'), true);
});

test('identity rules accept literal UUIDs without weakening requestId or product keys', () => {
  assert.deepEqual(validateProjection(projection), []);
  assert.equal(canonicalScope(projection.limits.scope), binding.canonicalScope);
  const uuid = 'ABCDEFAB-1234-4321-ABCD-ABCDEFABCDEF';
  const copy = clone(context);
  copy.subject.subjectId = copy.membership.subjectId = copy.grant.subjectId = uuid;
  assert.equal(evaluate(projection, copy).decision, 'Permit');
  assert.equal(copy.subject.subjectId, uuid, 'identity string is not rewritten');
  copy.requestId = binding.consumerTenantId;
  assert.equal(evaluate(projection, copy).decision, 'Indeterminate');
  const other = clone(projection);
  other.productId = binding.consumerTenantId;
  assert.ok(validateProjection(other).length);
  const ambiguous = clone(projection.limits.scope);
  ambiguous.consumerTenantId = binding.consumerTenantId + ';products=other';
  assert.throws(() => canonicalScope(ambiguous));
});

test('organization fixture keeps identity, membership, tenant, grant and action gates', async t => {
  const cases = [
    ['wrong audience', c => { c.subject.audience = 'fanasa-club-api'; }, 'Deny'],
    ['wrong consumer', c => { c.resource.consumerTenantId = binding.ownerTenantId; }, 'Deny'],
    ['wrong principal membership', c => { c.membership.subjectId = binding.ownerTenantId; }, 'Deny'],
    ['inactive grant', c => { c.grant.active = false; }, 'Deny'],
    ['wrong action', c => { c.action = 'organization.unit.delete'; }, 'Deny'],
    ['missing grant', c => { delete c.grant; }, 'Indeterminate'],
    ['stale policy evidence', c => { c.evidence.policyVersion++; }, 'Indeterminate']
  ];
  for (const [name, mutate, expected] of cases) await t.test(name, () => {
    const copy = clone(context); mutate(copy);
    const actual = evaluate(projection, copy);
    assert.equal(actual.decision, expected);
    assert.deepEqual(actual.obligations, []);
  });
  const denied = clone(projection); denied.policy.explicitDenyActions = [binding.action];
  const missing = clone(context); delete missing.membership;
  assert.equal(evaluate(denied, missing).decision, 'Deny');
});

test('allocation revision is required, positive and independent of contract/policy versions', async t => {
  for (const revision of [undefined, 0, -1, 1.5, '1', Number.MAX_SAFE_INTEGER + 1]) await t.test(String(revision), () => {
    const copy = clone(projection);
    if (revision === undefined) delete copy.limits.configRevision; else copy.limits.configRevision = revision;
    assert.ok(validateProjection(copy).length);
    assert.equal(evaluate(copy, context).decision, 'Indeterminate');
  });
  const state = freeze(project(null, event).state);
  const changedContract = amendment(8, p => { p.policy.policyVersion = 4; });
  const changed = project(state, changedContract);
  assert.equal(changed.status, 'applied');
  assert.equal(changed.state.projection.limits.configRevision, 1);
});

test('projector rejects conflicting or stale allocation revisions without resetting identity', () => {
  const first = freeze(project(null, event).state);
  const conflict = amendment(8, p => { p.limits.rate.requestsPerSecond = 6; });
  assert.equal(project(first, conflict).status, 'conflict');
  assert.deepEqual(project(first, conflict).state, first);
  const newer = amendment(8, p => { p.limits.configRevision = 2; p.limits.rate.requestsPerSecond = 6; });
  const advanced = project(first, newer);
  assert.equal(advanced.status, 'applied');
  const stale = amendment(9);
  assert.deepEqual(project(freeze(advanced.state), stale), { status: 'rejected', state: advanced.state, errors: ['Stale allocation configRevision'] });
  assert.equal(counterKey(first.projection.limits, 'rate'), counterKey(advanced.state.projection.limits, 'rate'));
  assert.equal(counterKey(first.projection.limits, 'quota', binding.allocationPeriodId), counterKey(advanced.state.projection.limits, 'quota', binding.allocationPeriodId));
});

test('policy amendments and suspend/resume keep the allocation and UTC month keys', () => {
  const first = freeze(project(null, event).state);
  const suspended = project(first, amendment(8, p => { p.status = 'Suspended'; p.policy.policyVersion++; }));
  assert.equal(suspended.status, 'applied');
  assert.equal(evaluate(suspended.state.projection, context).decision, 'Deny');
  const resumed = project(freeze(suspended.state), amendment(9));
  assert.equal(resumed.status, 'applied');
  for (const state of [suspended.state, resumed.state]) {
    assert.equal(state.projection.limits.configRevision, 1);
    assert.equal(counterKey(state.projection.limits, 'quota', '2026-10'), counterKey(first.projection.limits, 'quota', '2026-10'));
  }
  assert.notEqual(counterKey(first.projection.limits, 'quota', '2026-11'), counterKey(first.projection.limits, 'quota', '2026-10'));
  assert.equal(projection.limits.quota.algorithm, 'fixed-calendar-utc');
  assert.equal(projection.limits.quota.period, 'month');
  assert.equal(new Date(binding.periodStart).toISOString(), '2026-10-01T00:00:00.000Z');
  assert.equal(new Date(binding.periodEnd).toISOString(), '2026-11-01T00:00:00.000Z');
  const zero = clone(projection); zero.limits.quota.requests = 0;
  assert.deepEqual(validateProjection(zero), []);
  assert.equal(evaluate(zero, context).obligations[0].parameters.quota.requests, 0, 'zero remains a mandatory deny-all admission limit, never an unlimited default');
});

test('v2 PEP fixture gate rejects missing obligations, unsupported policy composition and deadlines', async t => {
  const cases = [
    ['missing resource obligation', d => { d.obligations.pop(); }],
    ['wrong consumer', d => { d.obligations[1].parameters.consumerTenantId = binding.ownerTenantId; }],
    ['missing config revision', d => { delete d.obligations[0].parameters.configRevision; }],
    ['unsupported period', d => { d.obligations[0].parameters.quota.period = 'day'; }],
    ['multiple policy references', d => { d.policyReferences.push({ policyId: 'other-policy', policyVersion: 1 }); }],
    ['deadline after thirty seconds', d => { d.validUntil = '2026-10-10T06:30:29.001Z'; }]
  ];
  for (const [name, mutate] of cases) await t.test(name, () => {
    const copy = clone(generated); mutate(copy);
    assert.equal(pepCanExecute(copy, fulfilled, '2026-10-10T06:30:00.000Z'), false);
  });
  assert.equal(pepCanExecute(generated, fulfilled, generated.validUntil), false);
  assert.equal(pepCanExecute(generated, [ADMIT], '2026-10-10T06:30:00.000Z'), false);
  const shortEvidence = clone(context); shortEvidence.evidence.validUntil = '2026-10-10T06:30:00.500Z';
  assert.equal(evaluate(projection, shortEvidence).validUntil, '2026-10-10T06:30:00.500Z');
});
