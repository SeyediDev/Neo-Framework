'use strict';

// Fixture construction is separate from the pure evaluator. No live identity,
// contract, allocation, counter or admission is created by this script.
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const { createHash } = require('node:crypto');
const { validateProjection, canonicalScope, evaluate, projectEvent } = require('./contract-policy.cjs');
const { loadSchema, validateSchema } = require('./schema-validator.cjs');
const root = path.resolve(__dirname, '..');
const bindingPath = path.resolve(root, '../../integration/fixture-binding.v1.json');
const binding = JSON.parse(fs.readFileSync(bindingPath, 'utf8'));
assert.equal(binding.synthetic, true);
assert.equal(binding.schemaVersion, 'fanasa.integration.fixture-binding/1');
assert.equal(binding.productId, 'organization-fabric');
assert.equal(binding.action, 'organization.unit.save');
const iso = value => new Date(value).toISOString();
const subjectId = '33333333-3333-4333-8333-333333333333';
const scope = { consumerTenantId: binding.consumerTenantId, productIds: [binding.productId], environments: [binding.environment], operationGroups: [binding.operationGroup] };
assert.equal(canonicalScope(scope), binding.canonicalScope);
const projection = {
  schemaVersion: '2.0.0', profileId: 'fanasa.contract-policy/2',
  contractId: binding.contractId, contractVersion: binding.contractVersion,
  ownerTenantId: binding.ownerTenantId, consumerTenantId: binding.consumerTenantId,
  productId: binding.productId, offerId: 'fixture-organization-offer', subscriptionId: 'fixture-subscription-b',
  status: 'Active', validFrom: iso(binding.periodStart), validTo: iso(binding.periodEnd), effectiveAt: iso(binding.periodStart),
  featureEntitlements: [binding.action], eligibleRoles: ['ApiInvoker'],
  seats: { maximum: 25, unit: 'active-membership', scope: 'consumer-product-subscription' },
  limits: {
    allocationId: binding.allocationId, configRevision: binding.configRevision, scope,
    canonicalScope: binding.canonicalScope, meter: binding.admissionMeter, unit: 'request',
    rate: { algorithm: 'token-bucket', requestsPerSecond: binding.requestsPerSecond, burstCapacity: binding.burstCapacity },
    quota: { algorithm: 'fixed-calendar-utc', period: 'month', requests: binding.quotaRequests }
  },
  zoneConstraints: ['ir-private-1'], pricingReference: { priceId: 'fixture-price-reference', priceVersion: 1 },
  policy: { policyId: binding.policyId, policyVersion: binding.policyVersion, templateId: 'organization-unit-save/1', combiningAlgorithm: 'deny-overrides', explicitDenyActions: [] }
};
const context = {
  // This fixed synthetic nonce stands in for the Gateway's cryptographic nonce.
  // It is separate from the committed business operation UUID.
  requestId: 'g-aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
  subject: { subjectId, authenticationValid: true, clientAuthorized: true, audience: 'fanasa-organization-fabric-api', scopes: [binding.action] },
  resource: { productId: binding.productId, consumerTenantId: binding.consumerTenantId, environment: binding.environment, operationGroup: binding.operationGroup },
  action: binding.action, environment: { now: iso(binding.evaluatedAt), zone: 'ir-private-1' },
  tenant: { tenantId: binding.consumerTenantId, active: true },
  membership: { subjectId, tenantId: binding.consumerTenantId, active: true, validFrom: iso(binding.periodStart), validTo: iso(binding.periodEnd) },
  grant: { grantId: 'fixture-grant-1', subjectId, consumerTenantId: binding.consumerTenantId, productId: binding.productId, role: 'ApiInvoker', actions: [binding.action], active: true, validFrom: iso(binding.periodStart), validTo: iso(binding.periodEnd) },
  evidence: { validUntil: new Date(Date.parse(binding.evaluatedAt) + 60000).toISOString(), contractVersion: binding.contractVersion, policyVersion: binding.policyVersion }
};
const event = {
  schemaVersion: '2.0.0', eventId: 'fixture-organization-event-7', producer: 'fanasa-contracts',
  aggregateVersion: binding.contractVersion, occurredAt: iso(binding.evaluatedAt), correlationId: context.requestId, projection
};
assert.deepEqual(validateProjection(projection), []);
assert.deepEqual(validateSchema(loadSchema('request-context.v2.schema.json'), context), []);
const projected = projectEvent(null, event, 'fanasa-contracts');
assert.equal(projected.status, 'applied');
const decision = evaluate(projected.state.projection, context);
assert.equal(decision.decision, 'Permit');
assert.deepEqual(validateSchema(loadSchema('decision.v2.schema.json'), decision), []);
const outputs = {
  'integration-projection.v2.json': projection,
  'integration-context.v2.json': context,
  'integration-projection-event.v2.json': event,
  'integration-decision.v2.json': decision
};
if (process.argv.includes('--check')) {
  for (const [name, value] of Object.entries(outputs)) assert.equal(fs.readFileSync(path.join(root, 'fixtures', name), 'utf8'), JSON.stringify(value, null, 2) + '\n', `${name} must match the actual generator`);
  console.log(JSON.stringify({ fixtureOnly: true, generatedFixtures: Object.keys(outputs), decision: decision.decision, schemaVersion: decision.schemaVersion, requestId: decision.requestId, configRevision: projection.limits.configRevision, decisionSha256: createHash('sha256').update(JSON.stringify(decision, null, 2) + '\n').digest('hex') }, null, 2));
} else {
  // The coordinator persists these exact evaluator outputs through its file tool.
  console.log(JSON.stringify(outputs, null, 2));
}
