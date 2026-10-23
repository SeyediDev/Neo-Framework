const { test } = require('node:test');
const assert = require('node:assert/strict');
const { createLoader } = require('../src/Fanasa.UnifiedPortal.Web/wwwroot/workspace-transport.js');

function setup() {
    const requests = [], events = [], timers = new Map();
    let timer = 0;
    const loader = createLoader({ url: 'https://portal.test/Workspace?view=compact&tenantId=old#catalog',
        fetch(url, options) { return new Promise((resolve, reject) => {
            requests.push({ url, options, resolve, reject });
            options.signal.addEventListener('abort', () => reject(new Error('aborted')), { once: true });
        }); },
        setTimeout(fn, delay) { timers.set(++timer, { fn, delay }); return timer; },
        clearTimeout(id) { timers.delete(id); },
        parse: JSON.parse,
        validate(data) { if (!data || typeof data.organizationId !== 'string') throw new Error('invalid'); },
        onStart: () => events.push('clear'), onData: data => events.push(data.organizationId),
        onError: timeout => events.push(timeout ? 'timeout' : 'error'), onEnd: () => events.push('end')
    });
    return { requests, events, timers, loader };
}
const response = text => new Response(text, { headers: { 'content-type': 'application/json' } });

test('organization refresh clears old access first and uses a bounded no-store same-origin snapshot', async () => {
    const app = setup(), pending = app.loader.load('consumer-a'), request = app.requests[0];
    assert.deepEqual(app.events, ['clear']);
    assert.equal(request.url.pathname, '/Workspace');
    assert.equal(request.url.searchParams.get('handler'), 'Snapshot');
    assert.equal(request.url.searchParams.get('view'), 'compact');
    assert.equal(request.url.searchParams.get('tenantId'), 'consumer-a');
    assert.equal(request.url.hash, '');
    assert.equal(request.options.cache, 'no-store');
    assert.equal(request.options.credentials, 'same-origin');
    assert.equal(request.options.redirect, 'error');
    assert.equal([...app.timers.values()][0].delay, 50000);
    request.resolve(response('{"organizationId":"consumer-a"}'));
    await pending;
    assert.deepEqual(app.events, ['clear', 'consumer-a', 'end']);
    assert.equal(app.timers.size, 0);
});

test('new organization cancels old request and old completion cannot release current busy state', async () => {
    const app = setup(), first = app.loader.load('consumer-a'), second = app.loader.load('consumer-b');
    await first;
    assert.equal(app.requests[0].options.signal.aborted, true);
    assert.deepEqual(app.events, ['clear', 'clear']);
    app.requests[1].resolve(response('{"organizationId":"consumer-b"}'));
    await second;
    assert.deepEqual(app.events, ['clear', 'clear', 'consumer-b', 'end']);
});

test('timeout clears its timer, enables retry and never treats a failure as an empty success', async () => {
    const app = setup(), pending = app.loader.load('consumer-a');
    [...app.timers.values()][0].fn();
    await pending;
    assert.deepEqual(app.events, ['clear', 'timeout', 'end']);
    assert.equal(app.timers.size, 0);
    const retry = app.loader.load('consumer-a');
    app.requests[1].resolve(response('{"organizationId":"consumer-a"}'));
    await retry;
    assert.deepEqual(app.events.slice(-3), ['clear', 'consumer-a', 'end']);
});

test('login HTML, malformed JSON, invalid envelopes and network errors cannot restore stale access', async () => {
    for (const value of [new Response('<html>login</html>'), response('{'), response('null'), response('{}'), null]) {
        const app = setup(), pending = app.loader.load('consumer-a');
        if (value) app.requests[0].resolve(value); else app.requests[0].reject(new Error('offline'));
        await pending;
        assert.deepEqual(app.events, ['clear', 'error', 'end']);
        assert.equal(app.timers.size, 0);
    }
});

test('a late response body from the old organization cannot overwrite a new snapshot', async () => {
    const app = setup(), first = app.loader.load('consumer-a');
    let body, started;
    const reading = new Promise(resolve => { started = resolve; });
    app.requests[0].resolve({ ok: true, headers: new Headers({ 'content-type': 'application/json' }),
        text: () => new Promise(resolve => { body = resolve; started(); }) });
    await reading;
    const second = app.loader.load('consumer-b');
    app.requests[1].resolve(response('{"organizationId":"consumer-b"}'));
    await second;
    body('{"organizationId":"consumer-a"}'); await first;
    assert.deepEqual(app.events, ['clear', 'clear', 'consumer-b', 'end']);
});

test('streamed and declared oversized snapshots fail closed and release retry controls', async () => {
    for (const data of [response('x'.repeat(4 * 1024 * 1024 + 1)),
        new Response('{}', { headers: { 'content-type': 'application/json', 'content-length': '4194305' } })]) {
        const app = setup(), pending = app.loader.load('consumer-a');
        app.requests[0].resolve(data); await pending;
        assert.deepEqual(app.events, ['clear', 'error', 'end']);
    }
});
