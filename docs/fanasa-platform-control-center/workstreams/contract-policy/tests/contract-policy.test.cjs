'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const path = require('node:path');
const {
  validateProjection, canonicalScope, counterKey, evaluate, pepCanExecute, projectEvent: projectFromProducer
} = require('../sample/contract-policy.cjs');
const { loadSchema, validateSchema } = require('../sample/schema-validator.cjs');

const fixture = name => JSON.parse(readFileSync(path.join(__dirname, '..', 'fixtures', name), 'utf8'));
const baselineProjection = fixture('projection.v1.json');
const baselineContext = fixture('context.v1.json');
const baselineEvent = fixture('projection-event.v1.json');
const golden = fixture('golden-decisions.v1.json');
const clone = value => structuredClone(value);
const ADMIT = 'urn:fanasa:obligation:admit-allocation:1';
const RESOURCE = 'urn:fanasa:obligation:resource-tenant:1';
const allFulfilled = [ADMIT, RESOURCE];
const NOW = '2026-10-10T10:00:00.000Z';
// The producer principal comes from a trusted boundary, never the event body.
const projectEvent = (state, event) => projectFromProducer(state, event, 'fanasa-contracts');

function scenario(mutate = () => {}) {
  const projection = clone(baselineProjection);
  const context = clone(baselineContext);
  mutate(projection, context);
  return { projection, context };
}

function changedEvent(version, mutate = () => {}) {
  const event = clone(baselineEvent);
  event.eventId = `sample-event-${version}`;
  event.aggregateVersion = version;
  event.projection.contractVersion = version;
  mutate(event);
  return event;
}

// Freezing detects accidental writes as well as checking the returned values.
function freeze(value) {
  if (value && typeof value === 'object') {
    Object.values(value).forEach(freeze);
    Object.freeze(value);
  }
  return value;
}

test('full Decision DTOs match independently authored golden fixtures', async t => {
  const mutations = {
    permit: () => {},
    deny: (_p, c) => { c.membership.active = false; },
    'not-applicable': (_p, c) => { c.resource.productId = 'other-product'; },
    indeterminate: (_p, c) => { delete c.environment.zone; }
  };
  assert.deepEqual(golden.cases.map(x => x.name), Object.keys(mutations));
  for (const entry of golden.cases) {
    await t.test(entry.name, () => {
      const { projection, context } = scenario(mutations[entry.name]);
      assert.deepEqual(evaluate(freeze(projection), freeze(context)), entry.expected);
      if (entry.name !== 'permit') {
        assert.equal(entry.expected.validUntil, null);
        assert.deepEqual(entry.expected.obligations, []);
        assert.equal(pepCanExecute(entry.expected, allFulfilled, NOW), false);
      }
    });
  }
});

test('each required authorization condition can independently prevent Permit', async t => {
  const conditions = [
    ['invalid authentication', (_p, c) => { c.subject.authenticationValid = false; }],
    ['unauthorized OAuth client', (_p, c) => { c.subject.clientAuthorized = false; }],
    ['wrong audience', (_p, c) => { c.subject.audience = 'another-api'; }],
    ['missing OAuth action scope', (_p, c) => { c.subject.scopes = []; }],
    ['inactive tenant', (_p, c) => { c.tenant.active = false; }],
    ['tenant context mismatch', (_p, c) => { c.tenant.tenantId = 'tenant-c'; }],
    ['resource tenant mismatch', (_p, c) => { c.resource.consumerTenantId = 'tenant-c'; }],
    ['inactive membership', (_p, c) => { c.membership.active = false; }],
    ['membership subject mismatch', (_p, c) => { c.membership.subjectId = 'other-subject'; }],
    ['membership tenant mismatch', (_p, c) => { c.membership.tenantId = 'tenant-c'; }],
    ['membership not yet valid', (_p, c) => { c.membership.validFrom = '2026-10-10T10:00:01.000Z'; }],
    ['membership expires at now', (_p, c) => { c.membership.validTo = NOW; }],
    ['suspended contract', p => { p.status = 'Suspended'; }],
    ['ended contract', p => { p.status = 'Ended'; }],
    ['contract not yet valid', p => { p.validFrom = '2026-10-10T10:00:01.000Z'; }],
    ['contract not yet effective', p => { p.effectiveAt = '2026-10-10T10:00:01.000Z'; }],
    ['contract expires at now', p => { p.validTo = NOW; }],
    ['feature not purchased', p => { p.featureEntitlements = []; }],
    ['eligible role not purchased', p => { p.eligibleRoles = []; }],
    ['unsupported action', (_p, c) => { c.action = 'report.write'; c.subject.scopes = ['report.write']; c.grant.actions = ['report.write']; }],
    ['inactive explicit grant', (_p, c) => { c.grant.active = false; }],
    ['grant subject mismatch', (_p, c) => { c.grant.subjectId = 'other-subject'; }],
    ['grant tenant mismatch', (_p, c) => { c.grant.consumerTenantId = 'tenant-c'; }],
    ['grant product mismatch', (_p, c) => { c.grant.productId = 'other-product'; }],
    ['grant does not include action', (_p, c) => { c.grant.actions = []; }],
    ['grant role is not eligible', (_p, c) => { c.grant.role = 'OrganizationAdmin'; }],
    ['grant not yet valid', (_p, c) => { c.grant.validFrom = '2026-10-10T10:00:01.000Z'; }],
    ['grant expires at now', (_p, c) => { c.grant.validTo = NOW; }],
    ['environment outside allocation', (_p, c) => { c.resource.environment = 'production'; }],
    ['operation group outside allocation', (_p, c) => { c.resource.operationGroup = 'administration'; }],
    ['zone outside contract', (_p, c) => { c.environment.zone = 'other-zone'; }]
  ];
  for (const [name, mutate] of conditions) {
    await t.test(name, () => {
      const { projection, context } = scenario(mutate);
      assert.deepEqual(validateProjection(projection), [], 'case must test a valid projection');
      const decision = evaluate(projection, context);
      assert.equal(decision.decision, 'Deny');
      assert.deepEqual(decision.obligations, []);
      assert.equal(pepCanExecute(decision, allFulfilled, NOW), false);
    });
  }
});

test('buying eligibility does not create a subject grant or platform administrator authority', () => {
  const noGrant = scenario((_p, c) => { delete c.grant; });
  assert.equal(evaluate(noGrant.projection, noGrant.context).decision, 'Indeterminate');
  const inactiveGrant = scenario((_p, c) => { c.grant.active = false; });
  assert.equal(evaluate(inactiveGrant.projection, inactiveGrant.context).decision, 'Deny');
  const admin = scenario(p => { p.eligibleRoles = ['PlatformAdmin']; });
  assert.ok(validateProjection(admin.projection).length > 0);
  assert.equal(evaluate(admin.projection, admin.context).decision, 'Indeterminate');
});

test('the synthetic Club profile cannot authorize another product using the Club audience', () => {
  const { projection, context } = scenario((p, c) => {
    p.productId = 'other-product';
    p.limits.scope.productIds = ['other-product'];
    p.limits.canonicalScope = canonicalScope(p.limits.scope);
    c.resource.productId = 'other-product';
    c.grant.productId = 'other-product';
  });
  assert.ok(validateProjection(projection).length > 0);
  assert.notEqual(evaluate(projection, context).decision, 'Permit');
});

test('missing, malformed, or stale trusted attributes fail closed', async t => {
  const cases = [
    ['missing authentication', (_p, c) => { delete c.subject.authenticationValid; }],
    ['missing tenant', (_p, c) => { delete c.tenant; }],
    ['missing membership', (_p, c) => { delete c.membership; }],
    ['missing grant', (_p, c) => { delete c.grant; }],
    ['missing evidence', (_p, c) => { delete c.evidence; }],
    ['missing product', (_p, c) => { delete c.resource.productId; }],
    ['missing zone', (_p, c) => { delete c.environment.zone; }],
    ['invalid subject identifier', (_p, c) => { c.subject.subjectId = 'Subject;tenant=tenant-c'; }],
    ['invalid zone identifier', (_p, c) => { c.environment.zone = 'ir-private-1;other-zone'; }],
    ['string boolean', (_p, c) => { c.tenant.active = 'true'; }],
    ['unknown trusted context field', (_p, c) => { c.platformAdmin = true; }],
    ['invalid time text', (_p, c) => { c.environment.now = 'today'; }],
    ['impossible calendar date', (_p, c) => { c.environment.now = '2026-02-30T10:00:00.000Z'; }],
    ['invalid membership time', (_p, c) => { c.membership.validFrom = 'not-a-date'; }],
    ['invalid grant time', (_p, c) => { c.grant.validTo = 'not-a-date'; }],
    ['expired evidence', (_p, c) => { c.evidence.validUntil = '2026-10-10T09:59:59.999Z'; }],
    ['evidence expires at now', (_p, c) => { c.evidence.validUntil = NOW; }],
    ['obsolete contract evidence', (_p, c) => { c.evidence.contractVersion--; }],
    ['obsolete policy evidence', (_p, c) => { c.evidence.policyVersion--; }]
  ];
  for (const [name, mutate] of cases) {
    await t.test(name, () => {
      const { projection, context } = scenario(mutate);
      const decision = evaluate(projection, context);
      assert.equal(decision.decision, 'Indeterminate');
      assert.deepEqual(decision.obligations, []);
      assert.equal(pepCanExecute(decision, allFulfilled, NOW), false);
    });
  }
});

test('explicit Deny overrides Permit and a missing attribute for the same target', () => {
  const { projection, context } = scenario(p => { p.policy.explicitDenyActions = ['report.read']; });
  assert.equal(evaluate(projection, context).decision, 'Deny');
  delete context.membership;
  assert.equal(evaluate(projection, context).decision, 'Deny');
  context.resource.productId = 'other-product';
  assert.equal(evaluate(projection, context).decision, 'NotApplicable');
});

test('validity intervals include their start and exclude their end', () => {
  const { projection, context } = scenario((p, c) => {
    p.validFrom = NOW;
    p.effectiveAt = NOW;
    c.membership.validFrom = NOW;
    c.grant.validFrom = NOW;
  });
  assert.equal(evaluate(projection, context).decision, 'Permit');
});

test('Permit lifetime is bounded by every authority and the one minute cache ceiling', async t => {
  for (const [name, mutate, expected] of [
    ['contract', (p, c) => { p.validTo = '2026-10-10T10:00:12.000Z'; c.evidence.validUntil = '2026-10-10T10:02:00.000Z'; }, '2026-10-10T10:00:12.000Z'],
    ['membership', (_p, c) => { c.membership.validTo = '2026-10-10T10:00:13.000Z'; }, '2026-10-10T10:00:13.000Z'],
    ['grant', (_p, c) => { c.grant.validTo = '2026-10-10T10:00:14.000Z'; }, '2026-10-10T10:00:14.000Z'],
    ['evidence', () => {}, '2026-10-10T10:00:45.000Z'],
    ['cache ceiling', (_p, c) => { c.evidence.validUntil = '2026-10-10T10:02:00.000Z'; }, '2026-10-10T10:01:00.000Z']
  ]) {
    await t.test(name, () => {
      const { projection, context } = scenario(mutate);
      const decision = evaluate(projection, context);
      assert.equal(decision.decision, 'Permit');
      assert.equal(decision.validUntil, expected);
    });
  }
});

test('PEP executes only after both known obligations succeed and before expiry', () => {
  const decision = evaluate(baselineProjection, baselineContext);
  assert.equal(pepCanExecute(decision, [], NOW), false);
  assert.equal(pepCanExecute(decision, [ADMIT], NOW), false);
  assert.equal(pepCanExecute(decision, [RESOURCE], NOW), false);
  assert.equal(pepCanExecute(decision, allFulfilled, NOW), true);
  assert.equal(pepCanExecute(decision, allFulfilled, '2026-10-10T10:00:44.999Z'), true);
  assert.equal(pepCanExecute(decision, allFulfilled, decision.validUntil), false);
  assert.equal(pepCanExecute(decision, allFulfilled, '2026-10-10T10:00:45.001Z'), false);
  assert.equal(pepCanExecute(decision, allFulfilled, '2026-10-10T09:59:59.999Z'), false);
  assert.equal(pepCanExecute(decision, allFulfilled, 'invalid-time'), false);
  assert.equal(pepCanExecute(decision, allFulfilled), false);
  assert.equal(pepCanExecute(decision, 'fulfilled', NOW), false);
});

test('Decision schema requires one admission and one resource obligation for Permit', async t => {
  const schema = loadSchema('decision.v1.schema.json');
  const baseline = clone(golden.cases.find(x => x.name === 'permit').expected);
  assert.deepEqual(validateSchema(schema, baseline), []);
  const mutations = [
    ['duplicate admissions with different parameters', d => {
      d.obligations[1] = clone(d.obligations[0]);
      d.obligations[1].parameters.rate.requestsPerSecond++;
    }],
    ['duplicate resource obligations', d => { d.obligations[0] = clone(d.obligations[1]); }],
    ['missing resource obligation', d => { d.obligations.pop(); }]
  ];
  for (const [name, mutate] of mutations) {
    await t.test(name, () => {
      const decision = clone(baseline);
      mutate(decision);
      assert.ok(validateSchema(schema, decision).length > 0);
    });
  }
});

test('PEP rejects unknown obligations, malformed DTOs and allocation/resource substitution', async t => {
  const mutations = [
    ['unknown obligation', d => { d.obligations[0].id = 'urn:fanasa:obligation:unknown:1'; }],
    ['missing obligation', d => { d.obligations.pop(); }],
    ['duplicate obligation', d => { d.obligations[1] = clone(d.obligations[0]); }],
    ['wrong fulfillment decision', d => { d.obligations[0].fulfillOn = 'Deny'; }],
    ['unknown schema version', d => { d.schemaVersion = '2.0.0'; }],
    ['unknown profile', d => { d.profileId = 'another-profile'; }],
    ['missing projection reference', d => { d.projectionReference = null; }],
    ['missing policy reference', d => { d.policyReferences = []; }],
    ['missing deadline', d => { d.validUntil = null; }],
    ['overlong decision lifetime', d => { d.validUntil = '2026-10-10T10:01:00.001Z'; }],
    ['unknown decision field', d => { d.admin = true; }],
    ['changed allocation reference', d => { d.allocationReference.allocationId = 'other-allocation'; }],
    ['forged canonical scope', d => { d.obligations[0].parameters.canonicalScope = 'tenant=tenant-c;products=club;environments=development;groups=reporting'; }],
    ['changed resource tenant', d => { d.obligations[1].parameters.consumerTenantId = 'tenant-c'; }],
    ['changed resource product', d => { d.obligations[1].parameters.productId = 'other-product'; }],
    ['changed resource environment', d => { d.obligations[1].parameters.environment = 'production'; }],
    ['unsupported resource action', d => { d.obligations[1].parameters.action = 'admin'; }],
    ['invalid admission rate', d => { d.obligations[0].parameters.rate.requestsPerSecond = 0; }]
  ];
  for (const [name, mutate] of mutations) {
    await t.test(name, () => {
      const decision = evaluate(baselineProjection, baselineContext);
      mutate(decision);
      assert.equal(pepCanExecute(decision, [...allFulfilled, 'urn:fanasa:obligation:unknown:1'], NOW), false);
    });
  }
  for (const decision of [null, {}, { decision: 'Permit' }]) assert.equal(pepCanExecute(decision, allFulfilled, NOW), false);
});

test('canonical scope sorts dimensions without modifying its input', () => {
  const scope = {
    consumerTenantId: 'tenant-b', productIds: ['hyper', 'club'],
    environments: ['production', 'development'], operationGroups: ['reporting', 'lookup']
  };
  const before = clone(scope);
  assert.equal(canonicalScope(freeze(scope)), 'tenant=tenant-b;products=club,hyper;environments=development,production;groups=lookup,reporting');
  assert.deepEqual(scope, before);
  assert.equal(canonicalScope(baselineProjection.limits.scope), baselineProjection.limits.canonicalScope);
});

test('canonical scope refuses ambiguous delimiters, uppercase, duplicate and empty IDs', async t => {
  const mutations = [
    ['missing tenant', s => { delete s.consumerTenantId; }],
    ['null tenant', s => { s.consumerTenantId = null; }],
    ['nonstring tenant', s => { s.consumerTenantId = ['tenant-b']; }],
    ['tenant delimiter', s => { s.consumerTenantId = 'tenant-b;products=hyper'; }],
    ['list delimiter', s => { s.productIds = ['club,hyper']; }],
    ['uppercase identifier', s => { s.productIds = ['Club']; }],
    ['duplicate identifier', s => { s.productIds = ['club', 'club']; }],
    ['empty list', s => { s.environments = []; }],
    ['missing dimension', s => { delete s.operationGroups; }],
    ['nonstring identifier', s => { s.productIds = [42]; }]
  ];
  for (const [name, mutate] of mutations) {
    await t.test(name, () => {
      const scope = clone(baselineProjection.limits.scope);
      mutate(scope);
      assert.throws(() => canonicalScope(scope));
    });
  }
});

test('projection validator rejects scope spoofing, unsupported authority and invalid limits', async t => {
  assert.deepEqual(validateProjection(baselineProjection), []);
  const mutations = [
    ['unknown field', p => { p.admin = true; }],
    ['invalid schema version', p => { p.schemaVersion = '2.0.0'; }],
    ['tenant substitution', p => { p.limits.scope.consumerTenantId = 'tenant-c'; p.limits.canonicalScope = canonicalScope(p.limits.scope); }],
    ['product substitution', p => { p.limits.scope.productIds = ['hyper']; p.limits.canonicalScope = canonicalScope(p.limits.scope); }],
    ['forged canonical scope', p => { p.limits.canonicalScope += ';groups=administration'; }],
    ['unsorted products', p => { p.limits.scope.productIds = ['hyper', 'club']; p.limits.canonicalScope = canonicalScope(p.limits.scope); }],
    ['duplicate product', p => { p.limits.scope.productIds = ['club', 'club']; }],
    ['platform administrator role', p => { p.eligibleRoles = ['PlatformAdmin']; }],
    ['unsupported feature', p => { p.featureEntitlements = ['admin']; }],
    ['unsupported policy action', p => { p.policy.explicitDenyActions = ['admin']; }],
    ['negative seats', p => { p.seats.maximum = -1; }],
    ['zero rate', p => { p.limits.rate.requestsPerSecond = 0; }],
    ['zero burst', p => { p.limits.rate.burstCapacity = 0; }],
    ['fractional burst', p => { p.limits.rate.burstCapacity = 1.5; }],
    ['negative quota', p => { p.limits.quota.requests = -1; }],
    ['unsafe contract version', p => { p.contractVersion = Number.MAX_SAFE_INTEGER + 1; }],
    ['invalid date', p => { p.validFrom = 'not-a-date'; }],
    ['empty validity interval', p => { p.validFrom = p.validTo; }],
    ['effective date after expiry', p => { p.effectiveAt = p.validTo; }]
  ];
  for (const [name, mutate] of mutations) {
    await t.test(name, () => {
      const { projection, context } = scenario(mutate);
      assert.ok(validateProjection(projection).length > 0);
      assert.equal(evaluate(projection, context).decision, 'Indeterminate');
    });
  }
  for (const invalid of [null, {}, [], 'projection']) assert.ok(validateProjection(invalid).length > 0);
});

test('allocation keys survive price, contract and limit amendments while remaining scoped', () => {
  const original = clone(baselineProjection);
  const amended = clone(original);
  amended.contractVersion++;
  amended.policy.policyVersion++;
  amended.pricingReference.priceVersion++;
  amended.limits.rate.requestsPerSecond = 180;
  amended.limits.rate.burstCapacity = 360;
  amended.limits.quota.requests = 150000;
  assert.deepEqual(validateProjection(amended), []);
  const rate = counterKey(freeze(original.limits), 'rate');
  const quota = counterKey(original.limits, 'quota', '2026-10');
  assert.equal(counterKey(amended.limits, 'rate'), rate);
  assert.equal(counterKey(amended.limits, 'quota', '2026-10'), quota);
  assert.notEqual(rate, quota);
  assert.notEqual(quota, counterKey(amended.limits, 'quota', '2026-11'));
  assert.ok(rate.includes(original.limits.allocationId));
  assert.ok(rate.includes(original.limits.canonicalScope));
  assert.ok(!rate.includes(original.contractId));
  assert.ok(!rate.includes(original.subscriptionId));
  const independent = clone(original.limits);
  independent.allocationId = 'other-allocation';
  assert.notEqual(counterKey(independent, 'rate'), rate);
  independent.allocationId = original.limits.allocationId;
  independent.scope.environments = ['production'];
  independent.canonicalScope = canonicalScope(independent.scope);
  assert.notEqual(counterKey(independent, 'rate'), rate);
});

test('invalid key kinds, quota periods and allocation tampering are rejected', () => {
  const limits = clone(baselineProjection.limits);
  for (const kind of ['route', 'burst', undefined]) assert.throws(() => counterKey(limits, kind));
  for (const period of [undefined, '', '2026-00', '2026-13', '2026-1', '2026-10-10', '2026-10;other']) {
    assert.throws(() => counterKey(limits, 'quota', period));
  }
  assert.throws(() => counterKey(limits, 'rate', '2026-10'));
  limits.canonicalScope = 'tenant=tenant-c';
  assert.throws(() => counterKey(limits, 'rate'));
  limits.canonicalScope = baselineProjection.limits.canonicalScope;
  limits.route = '/api/report';
  assert.throws(() => counterKey(limits, 'rate'));
});

test('event application is pure, idempotent and detects ID/version conflicts', () => {
  const input = freeze(clone(baselineEvent));
  const applied = projectEvent(null, input);
  assert.equal(applied.status, 'applied');
  assert.deepEqual(applied.state.projection, baselineProjection);
  assert.equal(applied.state.receipts.length, 1);
  assert.notEqual(applied.state.projection, input.projection);
  const state = freeze(clone(applied.state));
  assert.equal(projectEvent(state, input).status, 'duplicate');
  const reordered = Object.fromEntries(Object.entries(clone(input)).reverse());
  assert.equal(projectEvent(state, reordered).status, 'duplicate', 'property order is not a new payload');
  const reusedId = clone(input);
  reusedId.projection.status = 'Suspended';
  const idConflict = projectEvent(state, reusedId);
  assert.equal(idConflict.status, 'conflict');
  assert.deepEqual(idConflict.state, state);
  const versionConflict = clone(reusedId);
  versionConflict.eventId = 'another-event-7';
  assert.equal(projectEvent(state, versionConflict).status, 'conflict');
  const equivalentVersion = clone(input);
  equivalentVersion.eventId = 'equivalent-event-7';
  const equivalent = projectEvent(state, equivalentVersion);
  assert.equal(equivalent.status, 'duplicate');
  const reusedEquivalentId = changedEvent(8);
  reusedEquivalentId.eventId = equivalentVersion.eventId;
  assert.equal(projectEvent(equivalent.state, reusedEquivalentId).status, 'conflict', 'a duplicate snapshot must remember its new event ID');
});

test('out-of-order events cannot undo suspension, and stale event replay is remembered', () => {
  const first = projectEvent(null, baselineEvent).state;
  const suspension = changedEvent(8, e => { e.projection.status = 'Suspended'; e.projection.effectiveAt = '2026-10-10T09:00:00.000Z'; });
  const suspended = projectEvent(freeze(first), freeze(suspension));
  assert.equal(suspended.status, 'applied');
  assert.equal(evaluate(suspended.state.projection, baselineContext).decision, 'Deny');
  const older = changedEvent(6);
  const stale = projectEvent(freeze(suspended.state), older);
  assert.equal(stale.status, 'stale');
  assert.deepEqual(stale.state.projection, suspension.projection);
  assert.equal(stale.state.receipts.length, suspended.state.receipts.length + 1);
  assert.equal(projectEvent(freeze(stale.state), older).status, 'duplicate');
  const replayConflict = clone(older);
  replayConflict.correlationId = 'different-correlation';
  assert.equal(projectEvent(stale.state, replayConflict).status, 'conflict');
});

test('a newer immediate suspension supersedes a future activation regardless of effective timestamp', () => {
  const first = projectEvent(null, baselineEvent).state;
  const scheduledActivation = changedEvent(8, e => {
    e.projection.effectiveAt = '2026-10-11T00:00:00.000Z';
  });
  const scheduled = projectEvent(freeze(first), freeze(scheduledActivation));
  assert.equal(scheduled.status, 'applied');
  const immediateSuspension = changedEvent(9, e => {
    e.projection.status = 'Suspended';
    e.projection.effectiveAt = '2026-10-10T09:00:00.000Z';
  });
  const suspended = projectEvent(freeze(scheduled.state), freeze(immediateSuspension));
  assert.equal(suspended.status, 'applied');
  assert.equal(suspended.state.projection.contractVersion, 9);
  assert.equal(suspended.state.projection.status, 'Suspended');
  const currentContext = clone(baselineContext);
  currentContext.evidence.contractVersion = 9;
  assert.equal(evaluate(suspended.state.projection, currentContext).decision, 'Deny');
  const futureContext = clone(currentContext);
  futureContext.environment.now = '2026-10-11T10:00:00.000Z';
  futureContext.evidence.validUntil = '2026-10-11T10:00:45.000Z';
  assert.equal(evaluate(suspended.state.projection, futureContext).decision, 'Deny');
  const replay = projectEvent(freeze(suspended.state), scheduledActivation);
  assert.equal(replay.status, 'duplicate');
  assert.deepEqual(replay.state.projection, immediateSuspension.projection);
  assert.equal(evaluate(replay.state.projection, futureContext).decision, 'Deny');
});

test('snapshot gaps are accepted and ended subscriptions cannot resume', () => {
  const first = projectEvent(null, baselineEvent).state;
  const ended = changedEvent(10, e => { e.projection.status = 'Ended'; e.projection.effectiveAt = '2026-10-10T09:00:00.000Z'; });
  const result = projectEvent(first, ended);
  assert.equal(result.status, 'applied');
  assert.equal(result.state.projection.contractVersion, 10);
  assert.equal(evaluate(result.state.projection, baselineContext).decision, 'Deny');
  const resumed = changedEvent(11, e => { e.projection.effectiveAt = '2026-10-10T09:30:00.000Z'; });
  const rejected = projectEvent(result.state, resumed);
  assert.equal(rejected.status, 'rejected');
  assert.deepEqual(rejected.state, result.state);
});

test('a terminal snapshot at contract expiry applies and cannot be resumed', () => {
  const first = projectEvent(null, baselineEvent).state;
  const ended = changedEvent(8, e => {
    e.projection.status = 'Ended';
    e.projection.effectiveAt = e.projection.validTo;
    e.occurredAt = e.projection.validTo;
  });
  assert.deepEqual(validateProjection(ended.projection), []);
  const result = projectEvent(first, ended);
  assert.equal(result.status, 'applied');
  assert.equal(result.state.projection.status, 'Ended');
  assert.equal(projectEvent(freeze(result.state), ended).status, 'duplicate');
  const resumed = changedEvent(9, e => {
    e.projection.validTo = '2026-12-01T00:00:00.000Z';
    e.projection.effectiveAt = '2026-11-01T00:00:00.000Z';
    e.occurredAt = e.projection.effectiveAt;
  });
  assert.deepEqual(validateProjection(resumed.projection), []);
  assert.equal(projectEvent(result.state, resumed).status, 'rejected');
});

test('projector rejects untrusted producers, invalid snapshots and identity/reallocation changes', async t => {
  const state = freeze(projectEvent(null, baselineEvent).state);
  const mutations = [
    ['producer payload substitution', e => { e.producer = 'untrusted-producer'; }],
    ['missing event identifier', e => { delete e.eventId; }],
    ['missing correlation identifier', e => { delete e.correlationId; }],
    ['invalid event timestamp', e => { e.occurredAt = 'not-a-date'; }],
    ['schema mismatch', e => { e.schemaVersion = '2.0.0'; }],
    ['aggregate version mismatch', e => { e.aggregateVersion++; }],
    ['invalid projection', e => { e.projection.limits.quota.requests = -1; }],
    ['aggregate identity substitution', e => { e.projection.contractId = 'another-contract'; }],
    ['owner substitution', e => { e.projection.ownerTenantId = 'tenant-c'; }],
    ['subscription substitution', e => { e.projection.subscriptionId = 'another-subscription'; }],
    ['allocation identity substitution', e => { e.projection.limits.allocationId = 'another-allocation'; }],
    ['scope expansion', e => { e.projection.limits.scope.environments = ['development', 'production']; e.projection.limits.canonicalScope = canonicalScope(e.projection.limits.scope); }]
  ];
  for (const [name, mutate] of mutations) {
    await t.test(name, () => {
      const event = changedEvent(8, mutate);
      const result = projectEvent(state, event);
      assert.equal(result.status, 'rejected');
      assert.deepEqual(result.state, state);
      assert.ok(result.errors.length > 0);
    });
  }
  assert.equal(projectFromProducer(state, changedEvent(8), 'untrusted-producer').status, 'rejected');
  assert.equal(projectFromProducer(state, changedEvent(8)).status, 'rejected');
  assert.equal(projectEvent(null, null).status, 'rejected');
});

test('projector preserves counter identity across a valid contract amendment', () => {
  const original = freeze(projectEvent(null, baselineEvent).state);
  const amendment = changedEvent(8, e => {
    e.projection.effectiveAt = '2026-10-10T09:00:00.000Z';
    e.projection.pricingReference.priceVersion++;
    e.projection.limits.rate.requestsPerSecond = 180;
    e.projection.limits.quota.requests = 150000;
  });
  const next = projectEvent(original, freeze(amendment));
  assert.equal(next.status, 'applied');
  assert.equal(counterKey(next.state.projection.limits, 'rate'), counterKey(original.projection.limits, 'rate'));
  assert.equal(counterKey(next.state.projection.limits, 'quota', '2026-10'), counterKey(original.projection.limits, 'quota', '2026-10'));
  const context = clone(baselineContext);
  context.evidence.contractVersion = 8;
  assert.equal(evaluate(next.state.projection, context).decision, 'Permit');
});

test('returned obligations and projector state cannot mutate the original inputs', () => {
  const projection = clone(baselineProjection);
  const context = clone(baselineContext);
  const projectionBefore = clone(projection);
  const contextBefore = clone(context);
  const decision = evaluate(freeze(projection), freeze(context));
  decision.obligations[0].parameters.quota.requests = 0;
  assert.deepEqual(projection, projectionBefore);
  assert.deepEqual(context, contextBefore);
  const event = clone(baselineEvent);
  const state = projectEvent(null, freeze(event)).state;
  state.projection.status = 'Suspended';
  assert.deepEqual(event, baselineEvent);
});
