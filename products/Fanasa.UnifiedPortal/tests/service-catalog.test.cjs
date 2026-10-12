const { test } = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');
const path = require('node:path');
const source = fs.readFileSync(path.join(__dirname, '../src/Fanasa.UnifiedPortal.Web/wwwroot/service-catalog.js'), 'utf8');
class Element {
    constructor(tag = 'div') { this.tag = tag; this.children = []; this.listeners = {}; this.dataset = {}; this.attributes = {}; this.textContent = ''; this.disabled = false; }
    append(...children) { children.forEach(child => { child.parent = this; this.children.push(child); }); }
    replaceChildren(...children) { this.children = []; this.append(...children); }
    addEventListener(type, action) { this.listeners[type] = action; }
    setAttribute(key, value) { this.attributes[key] = value; }
    remove() { this.parent.children = this.parent.children.filter(x => x !== this); }
    querySelectorAll(selector) {
        const all = this.children.flatMap(child => [child, ...child.querySelectorAll('*')]);
        return selector === '*' ? all : all.filter(child => selector === '[data-services]' ? child.dataset.services !== undefined : (child.className || '').split(' ').includes(selector.slice(1)));
    }
    querySelector(selector) { return this.querySelectorAll(selector)[0] || null; }
    click() { return this.listeners.click?.(); }
}
function setup(withWorkspace = false) {
    const centers = ['fanasa.rayan', 'fanasa.cloud'].map(id => {
        const element = new Element('article');
        element.dataset = { center: id, centerSearch: id === 'fanasa.rayan' ? 'رایان ساخت' : 'ابر فن آسا' };
        const mount = new Element(); mount.dataset.services = id; element.append(mount); return element;
    });
    const status = new Element();
    const refresh = new Element('button');
    const workspaceRefresh = withWorkspace ? new Element('button') : null;
    const requests = [], timers = new Map();
    let counter = 0, filters = 0, workspaceClicks = 0;
    if (workspaceRefresh) workspaceRefresh.addEventListener('click', () => { workspaceClicks++; });
    const context = {
        document: { querySelectorAll: () => centers,
            getElementById: id => ({ 'service-catalog-status': status, 'service-catalog-refresh': refresh, 'workspace-refresh': workspaceRefresh })[id],
            createElement: tag => new Element(tag) },
        URL, AbortController, location: new URL('https://portal.test/Index#centers'),
        FanasaCenterFilter: () => { filters++; },
        setTimeout: (fn, delay) => { timers.set(++counter, { fn, delay }); return counter; }, clearTimeout: id => timers.delete(id),
        fetch: (url, options) => new Promise((resolve, reject) => {
            requests.push({ url: String(url), options, resolve, reject });
            options.signal.addEventListener('abort', () => reject(new Error('aborted')), { once: true });
        })
    };
    vm.runInNewContext(source, context);
    return { centers, status, refresh, workspaceRefresh, requests, timers,
        api: context.FanasaServiceCatalog, get filters() { return filters; }, get workspaceClicks() { return workspaceClicks; } };
}
function payload(accessState = 'requestRequired', overrides = {}) {
    return { status: 'Ready', message: 'کاتالوگ منتشرشده', services: [{ productId: 'id', key: 'work', displayName: 'خدمت تست',
        center: 'fanasa.rayan', description: 'توضیح تست', detailsUrl: 'https://catalog.test/details',
        requestAccessUrl: 'https://catalog.test/request', launchUrl: 'https://app.test/', accessState, ...overrides }] };
}
function links(app, selector) { return app.centers.flatMap(center => center.querySelectorAll(selector)); }
test('no grant keeps service description, details and acquisition link, never launch', () => {
    const app = setup(); app.api.render(payload());
    assert.equal(links(app, '.service-card').length, 1);
    assert.equal(links(app, '.service-launch').length, 0);
    assert.equal(links(app, '.service-request')[0].href, 'https://catalog.test/request');
    assert.equal(links(app, '.service-description')[0].textContent, 'توضیح تست');
    assert.equal(links(app, '.service-request')[0].rel, 'noopener noreferrer');
    assert.equal(app.centers[1].querySelector('[data-services]').children.length, 1);
});
test('only explicitly allowed state may show a safe launch link', () => {
    const app = setup();
    for (const state of ['signInRequired', 'selectOrganization', 'unverified', 'unexpected']) {
        app.api.render(payload(state));
        assert.equal(links(app, '.service-launch').length, 0);
    }
    app.api.render(payload('allowed'));
    assert.equal(links(app, '.service-launch')[0].href, 'https://app.test/');
    assert.equal(links(app, '.service-request').length, 0);
});
test('tenant switch removes old launch immediately and error cannot restore it', () => {
    const app = setup(); app.api.render(payload('allowed')); app.api.invalidate();
    assert.equal(links(app, '.service-launch').length, 0);
    assert.equal(links(app, '.service-access')[0].dataset.accessState, 'unverified');
    app.api.failed();
    assert.equal(links(app, '.service-launch').length, 0);
    assert.equal(links(app, '.service-card').length, 1);
});
test('render escapes metadata as text, rejects unsafe links and updates center search', () => {
    const app = setup();
    app.api.render(payload('allowed', { displayName: '<script>alert(1)</script>', description: '<img onerror=bad>',
        launchUrl: 'javascript:bad()', detailsUrl: 'https://user:password@evil.test/', requestAccessUrl: 'data:text/html,bad' }));
    assert.equal(links(app, '.service-launch').length, 0);
    assert.equal(app.centers[0].querySelector('[data-services]').children[0].children[0].textContent, '<script>alert(1)</script>');
    assert.equal(links(app, '.service-description')[0].textContent, '<img onerror=bad>');
    assert.match(app.centers[0].dataset.search, /رایان ساخت.*<script>/);
    assert.equal(app.filters, 1);
});
test('a discovery outage is unknown, not a successful empty center catalog', () => {
    const app = setup(); app.api.render(payload('allowed'));
    app.api.render({ status: 'Unavailable', message: 'خطا', services: payload().services });
    assert.equal(links(app, '.service-launch').length, 0);
    assert.match(app.centers[0].querySelector('[data-services]').children[0].textContent, /دریافت نشده/);
    assert.equal(app.status.textContent, 'خطا');
});
test('authenticated catalog refresh reuses workspace request and does not double-fetch grants', async () => {
    const app = setup(true); await app.refresh.click();
    assert.equal(app.workspaceClicks, 1);
    assert.equal(app.requests.length, 0);
    app.workspaceRefresh.disabled = true; await app.refresh.click();
    assert.equal(app.workspaceClicks, 1);
});
test('anonymous refresh is no-store on home alias, bounded and retryable', async () => {
    const app = setup(); const first = app.refresh.click();
    assert.equal(app.refresh.disabled, true);
    assert.equal(new URL(app.requests[0].url).pathname, '/Index');
    assert.equal(new URL(app.requests[0].url).searchParams.get('handler'), 'Workspace');
    assert.equal(app.requests[0].options.cache, 'no-store');
    assert.equal([...app.timers.values()][0].delay, 50000);
    [...app.timers.values()][0].fn(); await first;
    assert.equal(app.refresh.disabled, false);
    assert.equal(app.timers.size, 0);
    const retry = app.refresh.click();
    app.requests[1].resolve({ ok: true, headers: { get: () => 'application/json' }, json: async () => ({ serviceCatalog: payload('signInRequired') }) });
    await retry;
    assert.equal(links(app, '.service-launch').length, 0);
    assert.equal(links(app, '.service-request').length, 1);
    assert.equal(app.refresh.disabled, false);
});
