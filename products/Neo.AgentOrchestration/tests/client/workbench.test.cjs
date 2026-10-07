// Unit tests for request/error behavior, not a replacement for browser acceptance.
const { test } = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');
const path = require('node:path');
const source = fs.readFileSync(path.join(__dirname, '../../src/Neo.AgentOrchestration.Web/wwwroot/workbench.js'), 'utf8');
const origin = 'https://neo.example.test';
const current = origin + '/work/org/workspace/items/item';
function harness(fetcher, status = 409, options = {}) {
    const listeners = {}, mainListeners = {}, messages = {};
    const timers = new Map(); let timerId = 0;
    const field = { name: 'Message', type: 'textarea', value: 'retained draft', closest: () => form };
    const form = { action: current + '?handler=Log', method: 'post', elements: [field],
        getAttribute: name => name === 'action' ? form.action : null,
        hasAttribute: () => false, reportValidity: () => true };
    const main = { setAttribute() {}, contains: x => x === form || x === field,
        querySelector: selector => selector === '.kanban' && options.board ? {} : null,
        querySelectorAll: selector => selector === 'form' ? [form] : [],
        addEventListener: (name, fn) => mainListeners[name] = fn,
        replaceChildren() { throw new Error('An error response must not replace main'); } };
    const document = { getElementById: id => id === 'content' ? main : (messages[id] ??= { dataset: {}, addEventListener() {} }),
        documentElement: { dataset: {}, classList: { toggle() {} } }, activeElement: null, visibilityState: 'visible',
        addEventListener: (name, fn) => listeners[name] = fn };
    const window = { fetch: fetcher, DOMParser: class {}, addEventListener: (name, fn) => listeners[name] = fn };
    const context = { window, document, fetch: fetcher, location: new URL(current),
        history: { state: null, replaceState() {}, pushState() {} },
        crypto: { randomUUID: () => 'document-one' }, URL, URLSearchParams,
        setTimeout: options.fakeClock ? (fn, ms) => { const id = ++timerId; timers.set(id, { fn, ms }); return id; } : setTimeout,
        clearTimeout: options.fakeClock ? id => timers.delete(id) : clearTimeout,
        AbortController, scrollX: 0, scrollY: 0,
        FormData: class { entries() { return [[field.name, field.value]]; } },
        DOMParser: class { parseFromString() { return { getElementById: () => null }; } } };
    vm.runInNewContext(source, context);
    const response = () => ({ status, ok: false, url: current, headers: { get: () => 'text/html' }, text: async () => '' });
    const poll = async () => {
        const job = [...timers.entries()].find(([, value]) => value.ms === 6000);
        assert.ok(job, 'a board poll must be scheduled');
        timers.delete(job[0]); await job[1].fn();
    };
    return { window, document, listeners, mainListeners, messages, main, field, form, response, poll };
}

test('background polling continues on an idle visible board', async () => {
    let calls = 0;
    const h = harness(async () => { calls++; return h.response(); }, 409, { board: true, fakeClock: true });
    await h.poll();
    assert.equal(calls, 1);
});

test('GET filter selections survive a poll without an unsaved-write warning', async () => {
    let calls = 0;
    const h = harness(async () => { calls++; return h.response(); }, 409, { board: true, fakeClock: true });
    h.form.method = 'get'; h.field.name = 'ProjectId'; h.field.value = 'selected-project';
    h.mainListeners.change({ target: h.field });
    await h.poll(); await h.poll();
    assert.equal(calls, 0);
    assert.equal(h.field.value, 'selected-project');
    let warned = false;
    h.listeners.beforeunload({ preventDefault() { warned = true; } });
    assert.equal(warned, false);
    h.listeners.submit({ target: h.form, preventDefault() {} });
    await new Promise(setImmediate);
    assert.equal(calls, 1, 'manual filter submission remains enabled');
});

test('polling does not replace a focused form even before its first edit', async () => {
    let calls = 0;
    const h = harness(async () => { calls++; return h.response(); }, 409, { board: true, fakeClock: true });
    h.document.activeElement = h.field;
    await h.poll();
    assert.equal(calls, 0);
});

test('POST draft prevents background polling and retains the unsaved-write warning', async () => {
    let calls = 0;
    const h = harness(async () => { calls++; return h.response(); }, 409, { board: true, fakeClock: true });
    h.mainListeners.input({ target: h.field });
    await h.poll();
    assert.equal(calls, 0);
    let warned = false;
    h.listeners.beforeunload({ preventDefault() { warned = true; } });
    assert.equal(warned, true);
});
test('default submitter URL cannot replace the form handler; duplicate submission is blocked', async () => {
    const calls = [];
    let resolve;
    const h = harness((url, options) => { calls.push({ url, options }); return new Promise(r => resolve = r); });
    const event = { target: h.form, submitter: { formAction: current, hasAttribute: () => false }, preventDefault() {} };
    h.listeners.submit(event);
    h.listeners.submit(event);
    assert.equal(calls.length, 1);
    assert.equal(calls[0].url, current + '?handler=Log');
    assert.equal(calls[0].options.method, 'POST');
    assert.equal(calls[0].options.credentials, 'same-origin');
    assert.equal(calls[0].options.headers['X-Neo-Navigation'], '1');
    assert.equal(h.main.inert, true);
    resolve(h.response());
    await new Promise(setImmediate);
    assert.equal(h.window.NeoWorkbench.busy, false);
    assert.equal(h.main.inert, false);
    assert.equal(h.field.value, 'retained draft');
});
for (const status of [400, 403, 409, 500]) {
    test(`HTTP ${status} preserves inputs, shows feedback and does not retry`, async () => {
        let count = 0;
        const h = harness(async () => { count++; return h.response(); }, status);
        assert.equal(await h.window.NeoWorkbench.navigate(current, { body: {} }), false);
        assert.equal(count, 1);
        assert.equal(h.field.value, 'retained draft');
        assert.equal(h.messages['spa-feedback'].hidden, false);
        assert.equal(h.messages['spa-refresh'].hidden, false);
        assert.equal(h.window.NeoWorkbench.busy, false);
    });
}
test('401 shows native login without following an identity navigation', async () => {
    const h = harness(async () => h.response(), 401);
    await h.window.NeoWorkbench.navigate(current);
    assert.equal(h.messages['spa-login'].hidden, false);
    assert.equal(h.messages['spa-refresh'].hidden, true);
    assert.equal(h.field.value, 'retained draft');
});
test('lost POST response warns about uncertain commit and never retries', async () => {
    let count = 0;
    const h = harness(async () => { count++; throw new TypeError('network'); });
    await h.window.NeoWorkbench.navigate(current, { body: {} });
    assert.equal(count, 1);
    assert.match(h.messages['spa-message'].textContent, /ممکن است عملیات ثبت شده باشد/);
    assert.equal(h.field.value, 'retained draft');
});
test('external destinations are never fetched', async () => {
    const h = harness(() => { throw new Error('must not fetch'); });
    assert.equal(await h.window.NeoWorkbench.navigate('https://other.example.test/work/x'), false);
});
