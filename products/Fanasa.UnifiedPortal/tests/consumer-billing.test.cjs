'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs'), vm = require('node:vm'), path = require('node:path');
const view = require('../src/Fanasa.UnifiedPortal.Web/wwwroot/consumer-billing.js');
const organizationId = '11111111-2222-4333-8444-555555555555';
class Element {
    constructor(tag, document) { this.tagName = tag.toUpperCase(); this.ownerDocument = document; this.children = []; this.attributes = {}; this.ownText = ''; this.className = ''; }
    append(...values) { this.children.push(...values); }
    replaceChildren(...values) { this.children = values; this.ownText = ''; }
    setAttribute(name, value) { this.attributes[name] = String(value); }
    set textContent(value) { this.ownText = String(value); this.children = []; }
    get textContent() { return this.ownText + this.children.map(value => value.textContent).join(''); }
    set innerHTML(value) { throw Error('unsafe markup'); }
}
function dom() { const document = { createElement: tag => new Element(tag, document) }; return document.createElement('div'); }
function find(container, tag) { return [container, ...container.children.flatMap(child => find(child, tag))].filter(node => node.tagName === tag.toUpperCase()); }
function invoice(overrides = {}) { return { id: 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee', period: '2026-09', amount: '100.00', creditedAmount: '10.00', paidAmount: '30.00', remainingAmount: '60.00', status: 'partiallyPaid', issuedAt: '2026-10-01T00:00:00Z', from: '2026-09-01T00:00:00Z', to: '2026-10-01T00:00:00Z', ...overrides }; }
function report(overrides = {}) { return { organizationId, status: 'ready', asOf: '2026-10-24T12:00:00.0000000+00:00', mode: 'postpaid', currency: 'USD', walletBalance: '0', outstanding: '60.00', availableCredit: '40.00', invoices: [invoice()], ...overrides }; }
function empty(status) { return report({ status, asOf: null, mode: null, currency: null, walletBalance: null, outstanding: null, availableCredit: null, invoices: [] }); }
function failed(container) { assert.equal(find(container, 'dd').length, 0); assert.equal(find(container, 'tbody').length, 0); assert.doesNotMatch(container.textContent, /USD|60\.00|100\.00/); }

test('postpaid account renders balances and immutable amount, credit, payment and remaining separately', () => {
    const container = dom(); assert.equal(view.renderConsumerAccount(container, report(), organizationId).status, 'ready');
    assert.deepEqual(find(container, 'dd').map(node => node.textContent), ['0 USD', '60.00 USD', '40.00 USD']);
    assert.deepEqual(find(container, 'tbody')[0].children[0].children.map(node => node.textContent), ['2026-09', '100.00 USD', '10.00 USD', '30.00 USD', '60.00 USD', 'پرداخت بخشی']);
    assert.equal(find(container, 'time')[0].attributes.datetime, report().asOf);
    assert.equal(find(container, 'time')[0].attributes.title, report().asOf);
    assert.match(find(container, 'time')[0].textContent, /UTC$/);
});
test('PAYG shows a consumption statement without inviting a second invoice payment', () => {
    const container = dom(); view.renderConsumerAccount(container, report({ mode: 'payg', walletBalance: '60', outstanding: '0', availableCredit: '0', invoices: [invoice({ status: 'statement' })] }), organizationId);
    assert.match(container.textContent, /صورت مصرف/);
    assert.deepEqual(find(container, 'th').map(node => node.textContent), ['دوره', 'مبلغ مصرف', 'بستانکاری', 'وضعیت']);
    assert.equal(find(container, 'tbody')[0].children[0].children.length, 4);
    assert.match(find(container, 'tbody')[0].textContent, /از اعتبار کسر شده/);
    assert.equal(find(container, 'button').length, 0); assert.equal(find(container, 'a').length, 0);
});
test('large money strings render without IEEE rounding or removing trailing zeroes', () => {
    const container = dom(); view.renderConsumerAccount(container, report({ outstanding: '9007199254740993.10', invoices: [invoice({ amount: '9007199254740994.10', creditedAmount: '0.50', paidAmount: '0.50', remainingAmount: '9007199254740993.10' })] }), organizationId);
    assert.equal(find(container, 'dd')[1].textContent, '9,007,199,254,740,993.10 USD');
    assert.match(find(container, 'tbody')[0].textContent, /9,007,199,254,740,994\.10/);
});
test('explicit recorded zero differs from absent financial data', () => {
    const container = dom(); view.renderConsumerAccount(container, report({ walletBalance: '0', outstanding: '0', availableCredit: '0', invoices: [] }), organizationId);
    assert.equal(find(container, 'dd').length, 3); assert.match(container.textContent, /صورتحسابی صادر نشده است/);
    assert.equal(view.renderConsumerAccount(container, empty('notRecorded'), organizationId).status, 'notRecorded');
    assert.match(container.textContent, /حسابی ثبت نشده است/); failed(container);
});
test('failed states and invalid organization erase previously rendered money and invoice rows', () => {
    const container = dom();
    for (const next of [empty('unavailable'), empty('notRecorded'), report({ status: 'unknown' }), report({ organizationId: '66666666-7777-4888-8999-aaaaaaaaaaaa' }), null]) {
        view.renderConsumerAccount(container, report(), organizationId); view.renderConsumerAccount(container, next, organizationId); failed(container);
    }
    view.renderConsumerAccount(container, report(), organizationId); view.renderConsumerAccount(container, report(), null); failed(container);
});
test('numeric signed overflowing and inexact money fail rather than showing a partial account', () => {
    for (const amount of [12.5, 0, '-1', '1e2', '01', ' 1', '1.001', '79228162514264337593543950336', '1.00000000000000000000000000001']) {
        const container = dom(); assert.equal(view.renderConsumerAccount(container, report({ outstanding: amount }), organizationId).status, 'unavailable'); failed(container);
    }
});
test('unapproved currency and fractional IRR are rejected, harmless decimal zeros retained', () => {
    const container = dom();
    for (const data of [report({ currency: 'CHF' }), report({ currency: 'IRR', walletBalance: '0.1' })]) {
        assert.equal(view.renderConsumerAccount(container, data, organizationId).status, 'unavailable'); failed(container);
    }
    assert.equal(view.formatAmount('100.0000'), '100.0000');
});
test('inconsistent invoice balances and unsupported statuses fail the entire account', () => {
    for (const row of [invoice({ remainingAmount: '59' }), invoice({ status: '<img src=x>' }), invoice({ status: 'paid' }), invoice({ paidAmount: -30 }), invoice({ amount: '99.999' })]) {
        const container = dom(); assert.equal(view.renderConsumerAccount(container, report({ invoices: [row] }), organizationId).status, 'unavailable'); failed(container);
    }
});
test('duplicate invoice identity or period cannot duplicate displayed debt', () => {
    const container = dom();
    for (const rows of [[invoice(), invoice()], [invoice(), invoice({ id: '66666666-7777-4888-8999-aaaaaaaaaaaa' })], Array(1001).fill(invoice())]) {
        assert.equal(view.renderConsumerAccount(container, report({ invoices: rows }), organizationId).status, 'unavailable'); failed(container);
    }
});
test('dates retain exact UTC source; invalid dates timezone and half periods fail', () => {
    const container = dom();
    for (const row of [invoice({ from: null }), invoice({ from: '2026-09-01T00:00:00.0000001Z' }), invoice({ period: '2026-08' }), invoice({ to: '2026-11-01T00:00:00Z' }), invoice({ issuedAt: '2026-02-30T00:00:00Z' }), invoice({ issuedAt: '2026-10-01T00:00:00+03:30' })]) {
        assert.equal(view.renderConsumerAccount(container, report({ invoices: [row] }), organizationId).status, 'unavailable'); failed(container);
    }
});
test('financial data cannot sneak into nonready state', () => {
    const container = dom(); assert.equal(view.renderConsumerAccount(container, report({ status: 'notRecorded' }), organizationId).status, 'unavailable'); failed(container);
});
test('hostile labels remain rejected or literal text and never become markup', () => {
    const container = dom();
    assert.equal(view.renderConsumerAccount(container, report({ invoices: [invoice({ period: '<script>alert(1)</script>' })] }), organizationId).status, 'unavailable');
    for (const tag of ['img', 'script', 'svg', 'a']) assert.equal(find(container, tag).length, 0);
});
test('RTL table has keyboard scroll, live state and scoped column labels', () => {
    const container = dom(); view.renderConsumerAccount(container, report(), organizationId);
    assert.equal(find(container, 'section')[0].attributes.dir, 'rtl');
    assert.equal(find(container, 'p')[0].attributes['aria-live'], 'polite');
    const region = find(container, 'div').find(node => node.attributes.role === 'region'); assert.equal(region.attributes.tabindex, '0');
    assert.ok(find(container, 'th').every(node => node.attributes.scope === 'col'));
});
test('browser export performs no network storage or mutations and has no submit controls', () => {
    const sandbox = {};
    for (const name of ['fetch', 'XMLHttpRequest', 'localStorage', 'sessionStorage']) Object.defineProperty(sandbox, name, { get() { throw Error('forbidden ' + name); } });
    vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../src/Fanasa.UnifiedPortal.Web/wwwroot/consumer-billing.js'), 'utf8'), sandbox);
    const container = dom(); assert.equal(sandbox.FanasaConsumerBilling.renderConsumerAccount(container, report(), organizationId).status, 'ready');
    for (const tag of ['form', 'input', 'button']) assert.equal(find(container, tag).length, 0);
});
