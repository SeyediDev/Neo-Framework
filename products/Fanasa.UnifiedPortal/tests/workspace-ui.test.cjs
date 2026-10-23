'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const ui = require('../src/Fanasa.UnifiedPortal.Web/wwwroot/workspace.js');
const orgA = '11111111-1111-4111-8111-111111111111';
const orgB = '22222222-2222-4222-8222-222222222222';
function bootstrap(overrides = {}) {
    return { mode: 'synthetic-preview', subjectKey: 'a'.repeat(64), organizationId: orgA, isAdmin: false,
        organizations: [{ id: orgA, name: 'اول' }, { id: orgB, name: 'دوم' }], catalogStatus: 'ready',
        centers: [{ id: 'fanasa.rayan', name: 'رایان', logo: '/brand/center-rayan-mark-color.svg' }],
        catalog: [{ id: 'service', key: 'service', name: 'کار سازمانی', center: 'fanasa.rayan', description: 'اجرای کارهای سازمان', status: 'available',
            plans: [{ id: 'monthly', name: 'ماهانه', amount: '9007199254740993.0100', currency: 'IRR', quota: '1000' }] }],
        activeServices: [{ id: 'real', key: 'authorized', name: 'سامانه مجاز', center: 'fanasa.rayan', url: 'https://service.example.test/' }],
        requests: [], publications: [], usage: { status: 'ready', totalAmount: '9007199254740993.0100', items: [] }, ...overrides };
}
function memory() {
    const data = new Map();
    return { getItem: key => data.get(key) || null, setItem: (key, value) => data.set(key, value), removeItem: key => data.delete(key), data };
}
const draft = { id: 'draft-one', serviceId: 'service', planId: 'monthly', reason: 'نیاز واحد عملیات' };
const publication = { id: 'publish-one', key: 'my-service', name: 'خدمت من', center: 'fanasa.rayan', description: 'این خدمت نیازهای سازمان را پاسخ می‌دهد.', url: 'https://supplier.example.test/' };
const options = extra => ({ now: () => '2026-10-12T10:00:00Z', idFactory: () => 'generated-draft', ...extra });

test('published descriptions never grant entry and only approved safe actions are offered', () => {
    const service = { detailsUrl: 'https://catalog.test/details', requestAccessUrl: 'https://catalog.test/request',
        launchUrl: 'https://service.test/', accessState: 'requestRequired' };
    for (const accessState of ['requestRequired', 'unverified', 'selectOrganization', 'signInRequired', 'unknown']) {
        const actions = ui.publishedActions({ ...service, accessState });
        assert.deepEqual(actions.map(x => x.kind), ['details', 'request']);
        assert.equal(actions.some(x => x.kind === 'launch'), false);
    }
    assert.deepEqual(ui.publishedActions({ ...service, accessState: 'allowed' }).map(x => x.kind), ['details', 'launch']);
    assert.deepEqual(ui.publishedActions({ ...service, detailsUrl: 'javascript:bad()', requestAccessUrl: 'https://user:pass@bad.test/' }), []);
    assert.deepEqual(ui.publishedActions({ ...service, accessState: 'allowed', launchUrl: 'data:text/html,bad' }).map(x => x.kind), ['details']);
});

test('published catalog search normalizes Persian marks, spacing and word order', () => {
    const service = { displayName: 'مُديريّت کار', description: 'هوشـمند فن‌آسا', center: 'fanasa.rayan', key: 'work' };
    for (const query of ['هوشمند مدیریت', 'فن   آسا', 'كَار']) assert.equal(ui.selectCatalog([service], query, 'fanasa.rayan').length, 1);
    assert.equal(ui.selectCatalog([service], 'کار', 'fanasa.cloud').length, 0);
});

test('money tokens preserve lexical precision, fractions and quantities', () => {
    const parsed = ui.parseBootstrapJson('{"totalAmount":9007199254740993.0100,"items":[{"amount":1.2300e2,"quantity":0.00000000000001}],"name":"amount: 123"}');
    assert.equal(parsed.totalAmount, '9007199254740993.0100');
    assert.equal(parsed.items[0].amount, '1.2300e2');
    assert.equal(ui.decimalText(parsed.items[0].amount), '123.00');
    assert.equal(parsed.items[0].quantity, '0.00000000000001');
    assert.equal(ui.formatDecimal(parsed.totalAmount), '9,007,199,254,740,993.0100');
    assert.equal(ui.decimalText('1e-7'), '0.0000001');
    assert.throws(() => ui.parseBootstrapJson('{"amount":1 trailing}'));
});
test('invalid and already rounded money is rejected', () => {
    for (const value of [NaN, Infinity, 0.1, 9007199254740992, -1, null, '', '01', '-0.1', '1e101'])
        assert.throws(() => ui.decimalText(value));
    assert.equal(ui.decimalText(1200), '1200');
});
test('launch destinations permit only absolute credential-free web URLs', () => {
    for (const url of ['javascript:alert(1)', 'data:text/html,test', '//example.test', '/relative', 'https://user:pass@example.test', ' https://example.test', null])
        assert.equal(ui.safeServiceUrl(url), null);
    assert.equal(ui.safeServiceUrl('https://example.test/a'), 'https://example.test/a');
});
test('session scope requires verified identity and a nonempty organization UUID', () => {
    assert.notEqual(ui.storageScope(bootstrap()), ui.storageScope(bootstrap({ organizationId: orgB })));
    assert.notEqual(ui.storageScope(bootstrap()), ui.storageScope(bootstrap({ subjectKey: 'b'.repeat(64) })));
    for (const overrides of [{ subjectKey: '' }, { subjectKey: 'private-subject' }, { organizationId: null }, { organizationId: '00000000-0000-0000-0000-000000000000' }])
        assert.throws(() => ui.storageScope(bootstrap(overrides)));
});
test('production never mutates preview state or accesses preview storage', () => {
    let accesses = 0;
    const session = ui.createPreviewSession(bootstrap({ mode: 'connected', isAdmin: true }), options({ storage: { getItem() { accesses++; }, setItem() { accesses++; }, removeItem() { accesses++; } } }));
    for (const action of [() => session.request(draft), () => session.publish(publication), () => session.reviewRequest('id', 'approved'), () => session.reviewPublication('id', 'approved'), () => session.reset()])
        assert.throws(action);
    assert.equal(accesses, 0);
    assert.deepEqual(session.snapshot().activeServices, bootstrap().activeServices);
    for (const mode of ['preview', 'Synthetic-Preview', '', null]) assert.throws(() => ui.createPreviewSession(bootstrap({ mode })).request(draft));
});
test('request validates current offer, plan and bounded reason', () => {
    const base = bootstrap();
    assert.equal(ui.validateRequestDraft(base, draft).planId, 'monthly');
    for (const value of [{ ...draft, serviceId: 'other' }, { ...draft, planId: 'other' }, { ...draft, reason: ' ' }, { ...draft, reason: 'x'.repeat(1001) }, { ...draft, id: '<script>' }])
        assert.throws(() => ui.validateRequestDraft(base, value));
    assert.throws(() => ui.validateRequestDraft(bootstrap({ catalog: [{ ...base.catalog[0], status: 'withdrawn' }] }), draft));
});
test('a failed catalog or missing organization remains read-only in preview', () => {
    for (const overrides of [{ catalogStatus: 'forbidden' }, { catalogStatus: 'unavailable' }, { organizationId: null }]) {
        const session = ui.createPreviewSession(bootstrap(overrides), options());
        assert.throws(() => session.request(draft));
        assert.throws(() => session.publish(publication));
        assert.throws(() => session.reviewRequest(draft.id, 'approved'));
    }
});
test('request ID replay is stable and changed payload is rejected', () => {
    const storage = memory();
    const session = ui.createPreviewSession(bootstrap(), options({ storage }));
    const first = session.request(draft);
    assert.equal(first.organizationId, orgA);
    assert.equal(first.status, 'pending');
    assert.deepEqual(session.request(draft), first);
    assert.throws(() => session.request({ ...draft, reason: 'changed' }));
    assert.throws(() => session.request({ ...draft, id: 'new-id' }));
    const reopened = ui.createPreviewSession(bootstrap(), options({ storage }));
    assert.deepEqual(reopened.request(draft), first);
    assert.equal(reopened.snapshot().requests.length, 1);
    assert.equal(first.history.length, 1);
});
test('stored draft state stays isolated by user and organization', () => {
    const storage = memory();
    ui.createPreviewSession(bootstrap(), options({ storage })).request(draft);
    assert.equal(ui.createPreviewSession(bootstrap({ organizationId: orgB }), options({ storage })).snapshot().requests.length, 0);
    assert.equal(ui.createPreviewSession(bootstrap({ subjectKey: 'b'.repeat(64) }), options({ storage })).snapshot().requests.length, 0);
    assert.equal(ui.createPreviewSession(bootstrap(), options({ storage })).snapshot().requests.length, 1);
});
test('foreign seed rows and poisoned storage are discarded', () => {
    const storage = memory();
    const base = bootstrap({ requests: [{ id: 'foreign', organizationId: orgB, status: 'approved', serviceId: 'service', history: [] }] });
    assert.equal(ui.createPreviewSession(base).snapshot().requests.length, 0);
    storage.setItem(ui.storageScope(base), JSON.stringify({ version: 1, subjectKey: base.subjectKey, organizationId: orgB, requests: [], publications: [], drafts: [] }));
    const snapshot = ui.createPreviewSession(base, options({ storage })).snapshot();
    assert.equal(snapshot.requests.length, 0);
    assert.ok(snapshot.storageWarning);
    storage.setItem(ui.storageScope(base), '{broken');
    assert.ok(ui.createPreviewSession(base, options({ storage })).snapshot().storageWarning);
});
test('review requires admin and only transitions pending requests once', () => {
    const user = ui.createPreviewSession(bootstrap(), options());
    user.request(draft);
    assert.throws(() => user.reviewRequest(draft.id, 'approved'));
    const session = ui.createPreviewSession(bootstrap({ isAdmin: true }), options());
    session.request(draft);
    assert.throws(() => session.reviewRequest(draft.id, 'active'));
    const approved = session.reviewRequest(draft.id, 'approved');
    assert.equal(approved.history.length, 2);
    assert.deepEqual(session.reviewRequest(draft.id, 'approved'), approved);
    assert.throws(() => session.reviewRequest(draft.id, 'rejected'));
    assert.throws(() => session.reviewRequest('unknown', 'approved'));
    const simulated = session.snapshot().activeServices.find(x => x.key === 'service');
    assert.equal(simulated.url, null);
    assert.equal(simulated.simulated, true);
    assert.equal(session.snapshot().activeServices.find(x => x.key === 'authorized').url, 'https://service.example.test/');
});
test('supplier proposal validates identity, official center and safe canonical URL', () => {
    assert.equal(ui.validatePublicationDraft(bootstrap(), publication).center, 'fanasa.rayan');
    for (const value of [{ ...publication, center: 'invented-center' }, { ...publication, url: 'javascript:alert(1)' }, { ...publication, description: 'tiny' }, { ...publication, key: 'Upper Case' }, { ...publication, name: 'x' }])
        assert.throws(() => ui.validatePublicationDraft(bootstrap(), value));
});
test('publication replay and review never create products or tariffs', () => {
    const session = ui.createPreviewSession(bootstrap({ isAdmin: true }), options());
    const before = session.snapshot();
    const sent = session.publish(publication);
    assert.equal(sent.status, 'pending');
    assert.deepEqual(session.publish(publication), sent);
    assert.throws(() => session.publish({ ...publication, name: 'changed name' }));
    session.reviewPublication(sent.id, 'approved');
    assert.deepEqual(session.snapshot().catalog, before.catalog);
    assert.deepEqual(session.snapshot().activeServices, before.activeServices);
    assert.deepEqual(session.snapshot().usage, before.usage);
});
test('storage failure gives an honest warning and retains current page state', () => {
    const session = ui.createPreviewSession(bootstrap(), options({ storage: { getItem() { throw new Error('denied'); }, setItem() { throw new Error('quota'); } } }));
    assert.ok(session.snapshot().storageWarning);
    session.request(draft);
    assert.equal(session.snapshot().requests.length, 1);
    assert.ok(session.snapshot().storageWarning);
});
test('snapshots are copies and reset restores only current scoped seed', () => {
    const storage = memory();
    const session = ui.createPreviewSession(bootstrap(), options({ storage }));
    session.request(draft);
    session.snapshot().requests[0].status = 'approved';
    assert.equal(session.snapshot().requests[0].status, 'pending');
    const other = ui.createPreviewSession(bootstrap({ organizationId: orgB }), options({ storage }));
    other.request(draft);
    session.reset();
    assert.equal(session.snapshot().requests.length, 0);
    assert.equal(ui.createPreviewSession(bootstrap({ organizationId: orgB }), options({ storage })).snapshot().requests.length, 1);
});
test('catalog search normalizes Persian and Arabic variants with center filter', () => {
    const entries = [{ id: 'one', name: 'کار سازمانی', center: 'fanasa.rayan', description: 'بررسی', key: 'work' }, { id: 'two', name: 'اسناد', center: 'fanasa.dana', description: 'جستجو', key: 'docs' }];
    assert.equal(ui.selectCatalog(entries, 'كار', 'fanasa.rayan')[0].id, 'one');
    assert.equal(ui.selectCatalog(entries, 'كار', 'fanasa.dana').length, 0);
    assert.equal(ui.selectCatalog(entries, 'unknown', '').length, 0);
});
