const { test: nodeTest } = require('node:test');
const test = (name, run) => nodeTest(name, { timeout: 5000 }, run);
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

// Execute the shipped script without adding a browser/DOM package to the product.
// This models request and control state, not layout; real layout is checked separately.
const source = fs.readFileSync(path.join(__dirname, '../src/Fanasa.UnifiedPortal.Web/wwwroot/portal.js'), 'utf8');
class Element {
    constructor(tag = 'div') {
        this.tag = tag;
        this.children = [];
        this.listeners = {};
        this.attributes = {};
        this.dataset = {};
        this.value = '';
        this.disabled = false;
        this.hidden = false;
        this.textContent = '';
        const classes = new Set();
        this.classList = {
            add: name => classes.add(name), remove: name => classes.delete(name),
            toggle: (name, enabled) => enabled ? classes.add(name) : classes.delete(name)
        };
    }
    addEventListener(name, callback) { this.listeners[name] = callback; }
    append(...children) { this.children.push(...children); }
    add(option) { this.append(option); if (option.selected) this.value = option.value; }
    replaceChildren(...children) { this.children = children; if (this.tag === 'select') this.value = children.find(x => x.selected)?.value || children[0]?.value || ''; }
    get options() { return this.children; }
    setAttribute(name, value) { this.attributes[name] = value; }
    removeAttribute(name) { delete this.attributes[name]; }
    dispatch(name) { return this.listeners[name]({ preventDefault() {} }); }
}
class Option extends Element {
    constructor(text, value, defaultSelected = false, selected = false) {
        super('option'); this.textContent = text; this.value = value; this.selected = selected;
    }
}
function setup() {
    const ids = ['center-search', 'visible-count', 'no-results', 'workspace', 'organization-form',
        'organization-select', 'organization-submit', 'workspace-refresh', 'workspace-status',
        'product-grid', 'workspace-empty', 'empty-title', 'empty-description', 'product-count', 'workspace-login'];
    const elements = Object.fromEntries(ids.map(id => [id, new Element(id === 'organization-select' ? 'select' : 'div')]));
    elements['organization-select'].replaceChildren(new Option('انتخاب', ''), new Option('سازمان', 'tenant-a', false, true));
    elements['product-grid'].append(new Element('stale-link'));
    const requests = [];
    const timers = new Map();
    let timerId = 0;
    const location = new URL('https://portal.test/?tenantId=tenant-a#workspace');
    const history = [];
    vm.runInNewContext(source, {
        document: {
            getElementById: id => elements[id], querySelectorAll: () => [],
            createElement: tag => new Element(tag)
        }, Option, URL, AbortController, location,
        history: { replaceState: (_, __, url) => history.push(String(url)) },
        setTimeout: (callback, delay) => { const id = ++timerId; timers.set(id, { callback, delay }); return id; },
        clearTimeout: id => timers.delete(id),
        fetch: (url, options) => new Promise((resolve, reject) => {
            const entry = { url: String(url), options, resolve, reject };
            requests.push(entry);
            options.signal.addEventListener('abort', () => reject(Object.assign(new Error('aborted'), { name: 'AbortError' })), { once: true });
        })
    });
    return { elements, requests, timers, history,
        start: () => elements['workspace-refresh'].dispatch('click'),
        expire: () => { const [id, timer] = timers.entries().next().value; timers.delete(id); timer.callback(); }
    };
}
const payload = (tenant = 'tenant-a') => ({
    status: 'Ready', organizations: [{ id: tenant, name: 'سازمان' }], activeOrganizationId: tenant,
    activeOrganizationName: 'سازمان', products: [], message: 'هنوز سامانه‌ای فعال نشده است',
    presentation: { organizationPlaceholder: 'انتخاب سازمان', emptyTitle: 'بدون سامانه', needsSignIn: false, countLabel: '۰ سامانه' }
});
function response(data, json = () => Promise.resolve(data)) {
    return { ok: true, headers: { get: () => 'application/json; charset=utf-8' }, json };
}

test('refresh is no-store and removes stale links while retaining the verified selection', async () => {
    const app = setup();
    const pending = app.start();
    assert.equal(app.elements['workspace'].attributes['aria-busy'], 'true');
    assert.equal(app.elements['workspace-refresh'].disabled, true);
    assert.equal(app.elements['product-grid'].children.length, 0);
    assert.equal(app.elements['organization-select'].value, 'tenant-a');
    assert.equal(app.requests[0].options.cache, 'no-store');
    assert.equal(app.requests[0].options.credentials, 'same-origin');
    assert.equal(new URL(app.requests[0].url).searchParams.get('tenantId'), 'tenant-a');
    app.requests[0].resolve(response(payload()));
    await pending;
    assert.equal(app.elements['workspace-refresh'].disabled, false);
    assert.equal(app.elements['organization-select'].disabled, false);
    assert.equal(app.elements['workspace'].attributes['aria-busy'], undefined);
    assert.equal(app.timers.size, 0);
    assert.match(app.history[0], /tenantId=tenant-a#workspace$/);
});

test('a hung request reaches a bounded error and retry controls recover', async () => {
    const app = setup();
    const pending = app.start();
    assert.equal([...app.timers.values()][0].delay, 50000);
    app.expire();
    await pending;
    assert.equal(app.requests[0].options.signal.aborted, true);
    assert.equal(app.elements['workspace'].dataset.status, 'Unavailable');
    assert.match(app.elements['workspace-status'].textContent, /بیش از حد/);
    assert.equal(app.elements['workspace-refresh'].disabled, false);
    assert.equal(app.elements['organization-select'].disabled, false);
    assert.equal(app.elements['workspace'].attributes['aria-busy'], undefined);
    assert.equal(app.elements['product-grid'].children.length, 0);
    const retry = app.start();
    app.requests[1].resolve(response(payload()));
    await retry;
    assert.equal(app.elements['workspace'].dataset.status, 'Ready');
    assert.equal(app.timers.size, 0);
});

test('the deadline also aborts a stalled body after successful response headers', async () => {
    const app = setup();
    const pending = app.start();
    let bodyStarted;
    const readingBody = new Promise(resolve => { bodyStarted = resolve; });
    app.requests[0].resolve(response(null, () => new Promise((_, reject) => {
        app.requests[0].options.signal.addEventListener('abort', () => reject(Object.assign(new Error('body aborted'), { name: 'AbortError' })), { once: true });
        bodyStarted();
    })));
    await readingBody;
    app.expire();
    await pending;
    assert.equal(app.elements['workspace'].dataset.status, 'Unavailable');
    assert.match(app.elements['workspace-status'].textContent, /بیش از حد/);
    assert.equal(app.elements['workspace-refresh'].disabled, false);
});

test('a body that resolves after abort is never rendered', async () => {
    const app = setup();
    const pending = app.start();
    let resolveBody;
    let bodyStarted;
    const readingBody = new Promise(resolve => { bodyStarted = resolve; });
    app.requests[0].resolve(response(null, () => new Promise(resolve => { resolveBody = resolve; bodyStarted(); })));
    await readingBody;
    app.expire();
    resolveBody(payload());
    await pending;
    assert.equal(app.elements['workspace'].dataset.status, 'Unavailable');
    assert.equal(app.history.length, 0);
});

test('an old request cannot clear the busy state or overwrite a newer response', async () => {
    const app = setup();
    const first = app.start();
    app.elements['organization-select'].value = 'tenant-b';
    const second = app.elements['organization-select'].dispatch('change');
    await first;
    assert.equal(app.requests[0].options.signal.aborted, true);
    assert.equal(app.elements['workspace'].attributes['aria-busy'], 'true');
    assert.equal(app.elements['workspace-refresh'].disabled, true);
    assert.equal(app.timers.size, 1);
    app.requests[1].resolve(response(payload('tenant-b')));
    await second;
    assert.equal(app.elements['organization-select'].value, 'tenant-b');
    assert.equal(app.elements['workspace-refresh'].disabled, false);
    assert.equal(app.timers.size, 0);
    assert.equal(app.history.length, 1);
});

test('network failure is retryable, is not a successful zero, and clears its deadline', async () => {
    const app = setup();
    const pending = app.start();
    app.requests[0].reject(new Error('offline'));
    await pending;
    assert.equal(app.elements['workspace'].dataset.status, 'Unavailable');
    assert.equal(app.elements['product-count'].textContent, 'دریافت ناموفق');
    assert.equal(app.elements['workspace-refresh'].disabled, false);
    assert.equal(app.timers.size, 0);
});

test('a login HTML redirect is not parsed or displayed as a successful empty catalog', async () => {
    const app = setup();
    const pending = app.start();
    app.requests[0].resolve({ ok: true, headers: { get: () => 'text/html' }, json: () => { throw new Error('must not parse'); } });
    await pending;
    assert.equal(app.elements['workspace'].dataset.status, 'Unavailable');
    assert.equal(app.history.length, 0);
    assert.equal(app.timers.size, 0);
});

test('an expired session retains the server sign-in state without showing old links', async () => {
    const app = setup();
    const pending = app.start();
    app.requests[0].resolve(response({ ...payload(), status: 'SignInRequired', organizations: [], activeOrganizationId: null,
        presentation: { ...payload().presentation, needsSignIn: true, countLabel: 'ورود لازم است' } }));
    await pending;
    assert.equal(app.elements['workspace'].dataset.status, 'SignInRequired');
    assert.equal(app.elements['workspace-login'].hidden, false);
    assert.equal(app.elements['organization-select'].disabled, true);
    assert.equal(app.elements['product-grid'].children.length, 0);
    assert.equal(app.elements['workspace-refresh'].disabled, false);
});
