const { test } = require('node:test');
const assert = require('node:assert/strict');
const { elapsed, budget, format } = require('../../src/Neo.AgentOrchestration.Web/wwwroot/metering.js');
test('timer only advances active intervals, without wall-clock skew', () => {
    assert.equal(elapsed(120, true, 1000, 4500), 123);
    assert.equal(elapsed(120, false, 1000, 4500), 120);
    assert.equal(elapsed(120, true, 4000, 1000), 120);
});
test('budget is nullable and can exceed 100 percent', () => {
    assert.equal(budget(10, 0), null);
    assert.equal(budget(150, 100), 150);
    assert.equal(budget(1, 60), 1.7);
});
test('time retains hours, minutes and seconds', () => {
    assert.match(format(3661), /01 دقیقه و 01 ثانیه/);
});
