'use strict';
const fs = require('node:fs');
const path = require('node:path');
const { validateProjection, evaluate, projectEvent, counterKey } = require('./contract-policy.cjs');
const fixture = name => JSON.parse(fs.readFileSync(path.join(__dirname, '..', 'fixtures', name), 'utf8'));
const projection = fixture('projection.v1.json');
const context = fixture('context.v1.json');
const event = fixture('projection-event.v1.json');
const errors = validateProjection(projection);
if (errors.length) throw new Error(errors.join('\n'));
const deny = structuredClone(context); deny.membership.active = false;
const noTarget = structuredClone(context); noTarget.resource.productId = 'other-product';
const missing = structuredClone(context); delete missing.environment.zone;
console.log(JSON.stringify({
  fixtureOnly: true,
  projectionResult: projectEvent(null, event, 'fanasa-contracts').status,
  decisions: [context, deny, noTarget, missing].map(ctx => evaluate(projection, ctx)),
  rateKey: counterKey(projection.limits, 'rate'),
  quotaKey: counterKey(projection.limits, 'quota', '2026-10')
}, null, 2));
