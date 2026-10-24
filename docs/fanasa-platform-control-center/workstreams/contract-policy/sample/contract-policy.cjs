'use strict';
// Offline reference functions. Schemas load at bootstrap; no clock, counters,
// grants, usage or persistence mutations occur during a decision.
const { createHash } = require('node:crypto');
const { loadSchema, validateSchema } = require('./schema-validator.cjs');
const projectionSchema = loadSchema('contract-access-projection.v1.schema.json');
const contextSchema = loadSchema('request-context.v1.schema.json');
const decisionSchema = loadSchema('decision.v1.schema.json');
const eventSchema = loadSchema('projection-event.v1.schema.json');
const limitsSchema = loadSchema('limits.v1.schema.json');
const v2Schemas = {
  projection: loadSchema('contract-access-projection.v2.schema.json'),
  context: loadSchema('request-context.v2.schema.json'),
  decision: loadSchema('decision.v2.schema.json'),
  event: loadSchema('projection-event.v2.schema.json'),
  limits: loadSchema('limits.v2.schema.json')
};
const ID = /^[a-z][a-z0-9.-]{0,63}$/;
const IDENTITY = /^(?:[a-z][a-z0-9.-]{0,63}|[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12})$/;
const isV2 = value => value?.schemaVersion === '2.0.0';
const actionFor = value => isV2(value) ? 'organization.unit.save' : 'report.read';
const ADMIT = 'urn:fanasa:obligation:admit-allocation:1';
const RESOURCE = 'urn:fanasa:obligation:resource-tenant:1';
const clone = value => structuredClone(value);
const canonicalJson = value => JSON.stringify(sortObject(value));
function sortObject(value) {
  if (Array.isArray(value)) return value.map(sortObject);
  if (value && typeof value === 'object') return Object.fromEntries(Object.keys(value).sort().map(key => [key, sortObject(value[key])]));
  return value;
}
function canonicalScope(scope) {
  if (!scope || typeof scope.consumerTenantId !== 'string' || !IDENTITY.test(scope.consumerTenantId)) throw new Error('Invalid scope tenant');
  const list = name => {
    const values = scope[name];
    if (!Array.isArray(values) || !values.length || values.some(x => typeof x !== 'string' || !ID.test(x)) || new Set(values).size !== values.length) throw new Error('Invalid scope dimension');
    return [...values].sort().join(',');
  };
  return `tenant=${scope.consumerTenantId};products=${list('productIds')};environments=${list('environments')};groups=${list('operationGroups')}`;
}
function validateProjection(p) {
  const errors = validateSchema(isV2(p) ? v2Schemas.projection : projectionSchema, p);
  if (errors.length) return errors;
  if (Date.parse(p.validFrom) >= Date.parse(p.validTo)) errors.push('validFrom must precede validTo');
  if (p.status !== 'Ended' && Date.parse(p.effectiveAt) >= Date.parse(p.validTo)) errors.push('effectiveAt must precede validTo for nonterminal snapshots');
  if (p.consumerTenantId !== p.limits.scope.consumerTenantId) errors.push('Allocation consumer mismatch');
  if (!p.limits.scope.productIds.includes(p.productId)) errors.push('Product outside allocation');
  if (canonicalScope(p.limits.scope) !== p.limits.canonicalScope) errors.push('Noncanonical allocation scope');
  for (const name of ['productIds', 'environments', 'operationGroups']) {
    if (canonicalJson(p.limits.scope[name]) !== canonicalJson([...p.limits.scope[name]].sort())) errors.push(`Unsorted scope ${name}`);
  }
  // This template has exactly one operation and product role; no platform authority.
  if (p.featureEntitlements.some(x => x !== actionFor(p))) errors.push('Unsupported feature');
  if (p.eligibleRoles.some(x => x !== 'ApiInvoker')) errors.push('Unsupported eligible role');
  if (p.policy.explicitDenyActions.some(x => x !== actionFor(p))) errors.push('Unsupported Deny action');
  return errors;
}
function counterKey(limits, kind, periodId) {
  const errors = validateSchema(limits && Object.hasOwn(limits, 'configRevision') ? v2Schemas.limits : limitsSchema, limits);
  if (errors.length || canonicalScope(limits.scope) !== limits.canonicalScope) throw new Error('Invalid allocation');
  if (!['rate', 'quota'].includes(kind)) throw new Error('Unsupported limit kind');
  if (kind === 'quota' && !/^\d{4}-(0[1-9]|1[0-2])$/.test(periodId || '')) throw new Error('Quota requires a UTC calendar month');
  if (kind === 'rate' && periodId !== undefined) throw new Error('Rate has no period');
  return `fanasa:allocation:${limits.allocationId}:${limits.canonicalScope}:${kind}${kind === 'quota' ? ':' + periodId : ''}`;
}
function evaluate(p, ctx) {
  const projectionErrors = validateProjection(p);
  const validProjection = projectionErrors.length === 0;
  const nowText = ctx?.environment?.now;
  const validTime = typeof nowText === 'string' && /^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d\.\d{3}Z$/.test(nowText) && Number.isFinite(Date.parse(nowText)) && new Date(nowText).toISOString() === nowText;
  const result = {
    schemaVersion: isV2(p) ? '2.0.0' : '1.0.0', profileId: isV2(p) ? 'fanasa.contract-policy/2' : 'fanasa.contract-policy/1',
    requestId: typeof ctx?.requestId === 'string' && ID.test(ctx.requestId) ? ctx.requestId : 'unknown',
    decision: 'Indeterminate', reasonCode: 'invalid-projection', evaluatedAt: validTime ? nowText : null, validUntil: null,
    policyReferences: validProjection ? [{policyId: p.policy.policyId, policyVersion: p.policy.policyVersion}] : [],
    projectionReference: validProjection ? {contractId: p.contractId, contractVersion: p.contractVersion, subscriptionId: p.subscriptionId} : null,
    allocationReference: validProjection ? {allocationId: p.limits.allocationId, canonicalScope: p.limits.canonicalScope} : null,
    obligations: []
  };
  const finish = (decision, reasonCode) => ({...result, decision, reasonCode});
  if (!validProjection) return result;
  if (!ctx?.resource || typeof ctx.resource.productId !== 'string' || !ID.test(ctx.resource.productId)) return finish('Indeterminate', 'missing-attribute');
  if (ctx.resource.productId !== p.productId) return finish('NotApplicable', 'target-mismatch');
  // A matching explicit Deny beats permit eligibility and errors in other conditions.
  if (p.policy.explicitDenyActions.includes(ctx.action)) return finish('Deny', 'explicit-deny');
  const contextErrors = validateSchema(isV2(p) ? v2Schemas.context : contextSchema, ctx);
  if (contextErrors.length) return finish('Indeterminate', 'missing-or-invalid-attribute');
  const now = Date.parse(nowText);
  const activeWindow = (from, to) => Date.parse(from) <= now && now < Date.parse(to);
  const gates = [
    [ctx.subject.authenticationValid && ctx.subject.clientAuthorized && ctx.subject.audience === (isV2(p) ? 'fanasa-organization-fabric-api' : 'fanasa-club-api') && ctx.subject.scopes.includes(ctx.action), 'identity-denied'],
    [ctx.resource.consumerTenantId === p.consumerTenantId && ctx.tenant.tenantId === p.consumerTenantId && ctx.tenant.active, 'tenant-denied'],
    [ctx.membership.active && ctx.membership.subjectId === ctx.subject.subjectId && ctx.membership.tenantId === p.consumerTenantId && activeWindow(ctx.membership.validFrom, ctx.membership.validTo), 'membership-denied'],
    [p.status === 'Active', 'contract-inactive'],
    [Date.parse(p.effectiveAt) <= now && activeWindow(p.validFrom, p.validTo), 'contract-time-denied'],
    [ctx.action === actionFor(p) && p.featureEntitlements.includes(ctx.action), 'action-denied'],
    [ctx.grant.active && ctx.grant.subjectId === ctx.subject.subjectId && ctx.grant.consumerTenantId === p.consumerTenantId && ctx.grant.productId === p.productId && ctx.grant.actions.includes(ctx.action) && p.eligibleRoles.includes(ctx.grant.role) && activeWindow(ctx.grant.validFrom, ctx.grant.validTo), 'grant-denied'],
    [p.limits.scope.environments.includes(ctx.resource.environment) && p.limits.scope.operationGroups.includes(ctx.resource.operationGroup), 'scope-denied'],
    [p.zoneConstraints.includes(ctx.environment.zone), 'zone-denied']
  ];
  const denied = gates.find(([pass]) => !pass);
  if (denied) return finish('Deny', denied[1]);
  if (ctx.evidence.contractVersion !== p.contractVersion || ctx.evidence.policyVersion !== p.policy.policyVersion || Date.parse(ctx.evidence.validUntil) <= now) return finish('Indeterminate', 'stale-evidence');
  const validUntil = new Date(Math.min(Date.parse(p.validTo), Date.parse(ctx.membership.validTo), Date.parse(ctx.grant.validTo), Date.parse(ctx.evidence.validUntil), now + (isV2(p) ? 30000 : 60000))).toISOString();
  return {...finish('Permit', 'all-conditions-satisfied'), validUntil, obligations: [
    {id: ADMIT, fulfillOn: 'Permit', parameters: clone(p.limits)},
    {id: RESOURCE, fulfillOn: 'Permit', parameters: {consumerTenantId: p.consumerTenantId, productId: p.productId, environment: ctx.resource.environment, action: ctx.action}}
  ]};
}
function pepCanExecute(decision, fulfilledIds = [], now) {
  if (validateSchema(isV2(decision) ? v2Schemas.decision : decisionSchema, decision).length || decision.decision !== 'Permit') return false;
  if (!Array.isArray(fulfilledIds) || fulfilledIds.some(x => typeof x !== 'string')) return false;
  if (typeof now !== 'string' || validateSchema({type:'string',format:'date-time'}, now).length) return false;
  if (Date.parse(now) < Date.parse(decision.evaluatedAt) || Date.parse(now) >= Date.parse(decision.validUntil)) return false;
  if (Date.parse(decision.validUntil) > Date.parse(decision.evaluatedAt) + (isV2(decision) ? 30000 : 60000)) return false;
  const obligations = decision.obligations;
  if (new Set(obligations.map(x => x.id)).size !== 2 || !obligations.some(x => x.id === ADMIT) || !obligations.some(x => x.id === RESOURCE)) return false;
  const admission = obligations.find(x => x.id === ADMIT).parameters;
  const resource = obligations.find(x => x.id === RESOURCE).parameters;
  if (canonicalScope(admission.scope) !== admission.canonicalScope || admission.allocationId !== decision.allocationReference.allocationId || admission.canonicalScope !== decision.allocationReference.canonicalScope) return false;
  if (resource.action !== actionFor(decision) || resource.consumerTenantId !== admission.scope.consumerTenantId || !admission.scope.productIds.includes(resource.productId) || !admission.scope.environments.includes(resource.environment)) return false;
  return obligations.every(x => fulfilledIds.includes(x.id));
}
function projectEvent(state, event, authenticatedProducer) {
  const rejected = errors => ({status: 'rejected', state, errors});
  const errors = validateSchema(isV2(event) ? v2Schemas.event : eventSchema, event);
  if (errors.length) return rejected(errors);
  if (authenticatedProducer !== 'fanasa-contracts' || event.producer !== authenticatedProducer) return rejected(['Untrusted producer']);
  errors.push(...validateProjection(event.projection));
  if (event.aggregateVersion !== event.projection.contractVersion) errors.push('Aggregate/projection version mismatch');
  if (errors.length) return rejected(errors);
  const fingerprint = createHash('sha256').update(canonicalJson(event)).digest('hex');
  const receipt = state?.receipts.find(x => x.eventId === event.eventId);
  if (receipt) return {status: receipt.fingerprint === fingerprint ? 'duplicate' : 'conflict', state};
  const previous = state?.projection;
  if (previous) {
    if (previous.schemaVersion !== event.projection.schemaVersion) return rejected(['Explicit projection schema migration required']);
    for (const key of ['contractId','ownerTenantId','consumerTenantId','productId','offerId','subscriptionId']) {
      if (previous[key] !== event.projection[key]) return rejected(['Aggregate identity changed']);
    }
    if (previous.limits.allocationId !== event.projection.limits.allocationId || previous.limits.canonicalScope !== event.projection.limits.canonicalScope) return rejected(['Allocation identity changed; explicit reallocation is outside this sample']);
    if (isV2(event.projection)) {
      const next = event.projection.limits;
      if (next.configRevision < previous.limits.configRevision) return rejected(['Stale allocation configRevision']);
      if (next.configRevision === previous.limits.configRevision && canonicalJson(next) !== canonicalJson(previous.limits)) return {status:'conflict', state, errors:['Equal allocation configRevision with different limits']};
    }
    if (event.aggregateVersion === previous.contractVersion) {
      if (canonicalJson(previous) !== canonicalJson(event.projection)) return {status:'conflict', state};
      return {status:'duplicate', state:{projection:clone(previous), receipts:[...clone(state.receipts), {eventId:event.eventId,fingerprint}]}};
    }
    if (event.aggregateVersion < previous.contractVersion) {
      return {status:'stale', state: {projection:clone(previous), receipts:[...clone(state.receipts), {eventId:event.eventId,fingerprint}]}};
    }
    if (previous.status === 'Ended' && event.projection.status !== 'Ended') return rejected(['Ended subscription cannot resume']);
  }
  return {status:'applied', state:{projection:clone(event.projection),receipts:[...clone(state?.receipts || []), {eventId:event.eventId,fingerprint}]}};
}
module.exports = {validateProjection, canonicalScope, counterKey, evaluate, pepCanExecute, projectEvent};
