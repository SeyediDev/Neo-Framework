'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const view = require('../usage-report-view.js');
const org = '11111111-1111-4111-8111-111111111111';
const otherOrg = '22222222-2222-4222-8222-222222222222';

// Minimal DOM with destructive textContent semantics; markup assignment is forbidden.
class Element {
    constructor(tag, document) { this.tagName = tag.toUpperCase(); this.ownerDocument = document; this.children = []; this.attributes = {}; this.className = ''; this.ownText = ''; }
    append(...elements) { this.children.push(...elements); }
    replaceChildren(...elements) { this.ownText = ''; this.children = [...elements]; }
    setAttribute(name, value) { this.attributes[name] = String(value); }
    getAttribute(name) { return this.attributes[name] ?? null; }
    set textContent(value) { this.ownText = String(value); this.children = []; }
    get textContent() { return this.ownText + this.children.map(child => child.textContent).join(''); }
    set innerHTML(value) { throw new Error('Unsafe markup assignment: ' + value); }
}
function dom() {
    const document = { createElement(tag) { return new Element(tag, document); } };
    return document.createElement('div');
}
function find(element, tag) {
    return [element, ...element.children.flatMap(child => find(child, tag))].filter(node => node.tagName === tag.toUpperCase());
}
function item(overrides = {}) {
    return { productKey: 'documents', name: 'اسناد', meterId: 'pages', meterVersion: '1', unit: 'صفحه', quantity: '12.50', amount: '3.25', chargeCount: 2, ...overrides };
}
function report(overrides = {}) {
    return { organizationId: org, status: 'ready', from: '2026-10-01T00:00:00Z', to: '2026-11-01T00:00:00Z',
        periodLabel: 'مهر ۱۴۰۵', asOf: '2026-10-13T12:00:00.0000000+00:00', currency: 'USD', totalAmount: '3.25', items: [item()], basis: 'posted-ledger', ...overrides };
}
function empty(status = 'notRecorded') {
    return report({ status, asOf: null, currency: null, totalAmount: null, items: [] });
}
function unavailable(container) {
    assert.equal(find(container, 'tbody').length, 0);
    assert.equal(find(container, 'dl').length, 0);
    assert.match(container.textContent, /گزارش مصرف در دسترس نیست/);
}

test('ready report renders cost, period, meter version, unit and charge count', () => {
    const container = dom();
    assert.deepEqual(view.renderUsageReport(container, report(), org), { status: 'ready', itemCount: 1 });
    assert.equal(find(container, 'h2')[0].textContent, 'مصرف سازمان');
    assert.equal(find(container, 'tbody')[0].children.length, 1);
    const cells = find(container, 'tbody')[0].children[0].children;
    assert.equal(cells[0].textContent, 'اسناد');
    assert.equal(cells[1].textContent, 'pagesنسخه 1');
    assert.equal(cells[2].textContent, '12.50صفحه');
    assert.equal(cells[3].textContent, '3.25 USD');
    assert.equal(cells[4].textContent, '۲');
    assert.ok(find(container, 'time').some(node => node.getAttribute('datetime') === '2026-10-13T12:00:00.0000000+00:00'));
    assert.equal(find(container, 'time')[0].getAttribute('title'), report().asOf);
    assert.match(find(container, 'time')[0].textContent, /UTC$/);
    assert.match(container.textContent, /مهر ۱۴۰۵/);
});

test('large money and small quantities retain every supplied digit in real rendered text', () => {
    const container = dom();
    view.renderUsageReport(container, report({ totalAmount: '9007199254740993.0100', items: [item({ amount: '9007199254740993.0100', quantity: '0.0000000000000000000000000001' })] }), org);
    assert.equal(find(container, 'dd')[0].textContent, '9,007,199,254,740,993.0100 USD');
    assert.match(find(container, 'tbody')[0].textContent, /0\.0000000000000000000000000001/);
    assert.equal(view.formatExactDecimal('79228162514264337593543950335'), '79,228,162,514,264,337,593,543,950,335');
});

test('amount formatter rejects already rounded numbers and inexact decimal precision', () => {
    for (const amount of [3.25, 0, 9007199254740992, NaN, Infinity, null, '', '01', '-0', '-1', '1e2', ' 1', '1.', '.1', '1.00000000000000000000000000001', '79228162514264337593543950336'])
        assert.throws(() => view.formatExactDecimal(amount));
    assert.equal(view.formatExactDecimal('1.00000000000000000000000000000'), '1.00000000000000000000000000000');
});

test('explicit empty ready report shows zero cost, without a table or unknown amount', () => {
    const container = dom();
    assert.equal(view.renderUsageReport(container, report({ totalAmount: '0.00', items: [] }), org).status, 'ready');
    assert.equal(find(container, 'dd')[0].textContent, '0.00 USD');
    assert.equal(find(container, 'dd')[1].textContent, '۰');
    assert.match(container.textContent, /هزینهٔ ثبت‌شده در این دوره صفر است/);
    assert.equal(find(container, 'table').length, 0);
});

test('unrecorded report shows unknown cost rather than a successful zero', () => {
    const container = dom();
    assert.equal(view.renderUsageReport(container, empty(), org).status, 'notRecorded');
    assert.match(container.textContent, /هنوز گزارشی ثبت نشده است/);
    assert.equal(find(container, 'dd').length, 0);
    assert.doesNotMatch(container.textContent, /صفر|USD/);
});

test('every failed state clears the previous organization rows and cost', () => {
    const container = dom();
    for (const state of ['notRecorded', 'incomplete', 'unavailable']) {
        view.renderUsageReport(container, report(), org);
        assert.equal(view.renderUsageReport(container, empty(state), org).status, state);
        assert.equal(find(container, 'tbody').length, 0);
        assert.equal(find(container, 'dd').length, 0);
        assert.doesNotMatch(container.textContent, /documents|3\.25|USD/);
    }
});

test('organization change, missing scope and malformed identifiers erase previous values', () => {
    const container = dom();
    for (const expected of [otherOrg, null, '', '00000000-0000-0000-0000-000000000000', org + ' ']) {
        view.renderUsageReport(container, report(), org);
        assert.equal(view.renderUsageReport(container, report(), expected).status, 'unavailable');
        unavailable(container);
        assert.doesNotMatch(container.textContent, /مهر|documents|USD/);
    }
    assert.equal(view.renderUsageReport(container, report({ organizationId: 'not-an-org' }), org).status, 'unavailable');
});

test('organization identifiers compare case-insensitively without loosening the UUID boundary', () => {
    const id = 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee';
    assert.equal(view.renderUsageReport(dom(), report({ organizationId: id.toUpperCase() }), id).status, 'ready');
});

test('service count counts distinct products while preserving each meter group', () => {
    const container = dom();
    const items = [item(), item({ meterId: 'files', meterVersion: '2', unit: 'فایل', amount: '1.25' }), item({ productKey: 'workflow', name: 'گردش کار', meterId: 'runs', amount: '0.50' })];
    view.renderUsageReport(container, report({ totalAmount: '5.00', items }), org);
    assert.equal(find(container, 'dd')[1].textContent, '۲');
    assert.equal(find(container, 'tbody')[0].children.length, 3);
    assert.match(find(container, 'tbody')[0].textContent, /filesنسخه 2/);
});

test('legacy row keeps quantity unknown and never manufactures a quota or wallet', () => {
    const container = dom();
    view.renderUsageReport(container, report({ items: [item({ quantity: null, unit: null, meterVersion: null })] }), org);
    assert.equal(find(container, 'tbody')[0].children[0].children[2].textContent, 'نامعلوم');
    assert.equal(find(container, 'tbody')[0].children[0].children[1].textContent, 'pages');
    assert.doesNotMatch(container.textContent, /سقف|سهمیه|کیف پول/);
    assert.equal(find(container, 'tbody')[0].children[0].children[3].textContent, '3.25 USD');
});

test('legacy and measured rows for one meter stay separate', () => {
    const container = dom();
    view.renderUsageReport(container, report({ totalAmount: '6.50', items: [item(), item({ quantity: null, unit: null, meterVersion: null })] }), org);
    assert.equal(find(container, 'tbody')[0].children.length, 2);
    assert.equal(find(container, 'dd')[1].textContent, '۱');
    assert.equal(find(container, 'tbody')[0].children[1].children[2].textContent, 'نامعلوم');
});

test('mixed null metadata and zero measured quantities fail without partial rows', () => {
    for (const changes of [{ quantity: null }, { unit: null }, { meterVersion: null }, { quantity: '0' }, { unit: '' }, { meterVersion: '' }, { quantity: 12.5 }]) {
        const container = dom();
        assert.equal(view.renderUsageReport(container, report({ totalAmount: '6.50', items: [item(), item({ meterId: 'bad', ...changes })] }), org).status, 'unavailable');
        unavailable(container);
    }
});

test('unknown state, foreign basis and financial values in failed reports are rejected', () => {
    for (const data of [report({ status: 'loading' }), report({ basis: 'current-tariff' }), empty('READY'), empty(null), empty(''), report({ status: 'incomplete' }), { ...empty(), asOf: report().asOf }]) {
        const container = dom();
        assert.equal(view.renderUsageReport(container, data, org).status, 'unavailable');
        unavailable(container);
    }
});

test('invalid money or mismatched totals never display a successful financial summary', () => {
    for (const data of [report({ totalAmount: 3.25 }), report({ totalAmount: '3.24' }), report({ totalAmount: '1', items: [] }), report({ items: [item({ amount: 3.25 })] }), report({ currency: 'usd' }), report({ currency: 'CHF' }), report({ currency: '<script>' })]) {
        const container = dom();
        assert.equal(view.renderUsageReport(container, data, org).status, 'unavailable');
        unavailable(container);
    }
});

test('exact fractional totals are validated using decimal arithmetic', () => {
    const container = dom();
    const items = [item({ amount: '0.1' }), item({ meterId: 'other', amount: '0.2' })];
    assert.equal(view.renderUsageReport(container, report({ totalAmount: '0.300', items }), org).status, 'ready');
    assert.equal(find(container, 'dd')[0].textContent, '0.300 USD');
    assert.equal(view.renderUsageReport(container, report({ totalAmount: '0.30000000000000004', items }), org).status, 'unavailable');
    unavailable(container);
});

test('money obeys retained currency precision without discarding trailing zeros', () => {
    const container = dom();
    assert.equal(view.renderUsageReport(container, report({ currency: 'IRR', totalAmount: '1234.00', items: [item({ amount: '1234.00' })] }), org).status, 'ready');
    assert.equal(find(container, 'dd')[0].textContent, '1,234.00 IRR');
    for (const data of [report({ currency: 'IRR' }), report({ totalAmount: '3.251', items: [item({ amount: '3.251' })] }), report({ totalAmount: '3.25', items: [item({ amount: '3.245' }), item({ meterId: 'other', amount: '0.005' })] })]) {
        assert.equal(view.renderUsageReport(container, data, org).status, 'unavailable');
        unavailable(container);
    }
});

test('submillisecond period boundaries compare exactly and retain source time strings', () => {
    const container = dom();
    const from = '2026-10-01T00:00:00.0000001Z', to = '2026-10-01T00:00:00.0000002+00:00';
    assert.equal(view.renderUsageReport(container, report({ from, to }), org).status, 'ready');
    const period = find(container, 'p').find(node => node.getAttribute('data-from'));
    assert.equal(period.getAttribute('data-from'), from); assert.equal(period.getAttribute('data-to'), to);
    assert.equal(period.getAttribute('title'), 'از ' + from + ' تا پیش از ' + to);
    assert.equal(view.renderUsageReport(container, report({ from: to, to: from }), org).status, 'unavailable');
    unavailable(container);
});

test('duplicates, invalid counts and malformed row collections are rejected', () => {
    for (const data of [report({ totalAmount: '6.50', items: [item(), item()] }), report({ items: null }), report({ items: [null] }), report({ items: [item({ chargeCount: 0 })] }), report({ items: [item({ chargeCount: 1.5 })] }), report({ items: [item({ chargeCount: '2' })] }), report({ items: [item({ chargeCount: 10001 })] }), report({ totalAmount: '6.50', items: [item({ chargeCount: 10000 }), item({ meterId: 'second' })] }), report({ items: [item({ name: '' })] }), report({ items: [item({ name: 'one\u0000two' })] }), report({ items: [item({ name: 'x'.repeat(201) })] }), report({ items: Array(10001).fill(item()) })]) {
        const container = dom();
        assert.equal(view.renderUsageReport(container, data, org).status, 'unavailable');
        unavailable(container);
    }
});

test('invalid or non-UTC times, empty labels and invalid periods fail closed', () => {
    for (const changes of [{ from: '2026-02-30T00:00:00Z' }, { to: '2026-11-01T00:00:00+03:30' }, { asOf: '2026-10-13' }, { asOf: '2026-10-13T24:00:00Z' }, { asOf: null }, { from: report().to }, { to: '2028-01-01T00:00:00Z' }, { periodLabel: '' }]) {
        const container = dom();
        assert.equal(view.renderUsageReport(container, report(changes), org).status, 'unavailable');
        unavailable(container);
    }
});

test('untrusted labels are literal text and never become HTML, links or script elements', () => {
    const container = dom(), payload = '<img src=x onerror=alert(1)>';
    view.renderUsageReport(container, report({ periodLabel: payload, items: [item({ productKey: payload, name: '<script>alert(1)</script>', meterId: '<svg onload=alert(2)>', unit: payload, meterVersion: payload })] }), org);
    assert.match(container.textContent, /<script>alert\(1\)<\/script>/);
    assert.match(container.textContent, /<img src=x onerror=alert\(1\)>/);
    for (const tag of ['script', 'img', 'svg', 'a']) assert.equal(find(container, tag).length, 0);
});

test('ready and failed states remain live, RTL and keyboard-scroll accessible', () => {
    const container = dom();
    view.renderUsageReport(container, report(), org);
    assert.equal(find(container, 'section')[0].getAttribute('dir'), 'rtl');
    const status = find(container, 'p').find(node => node.getAttribute('role') === 'status');
    assert.equal(status.getAttribute('aria-live'), 'polite');
    assert.equal(status.getAttribute('aria-atomic'), 'true');
    assert.match(status.className, /consumer-usage-visually-hidden/);
    const region = find(container, 'div').find(node => node.getAttribute('role') === 'region');
    assert.equal(region.getAttribute('tabindex'), '0');
    assert.equal(region.getAttribute('aria-label'), 'جزئیات مصرف سازمان');
    assert.equal(find(container, 'caption')[0].textContent, 'جزئیات مصرف سازمان');
    assert.ok(find(container, 'th').every(node => node.getAttribute('scope') === 'col'));
    view.renderUsageReport(container, empty(), org);
    assert.equal(find(container, 'p')[0].getAttribute('aria-live'), 'polite');
    assert.doesNotMatch(find(container, 'p')[0].className, /consumer-usage-visually-hidden/);
});

test('malformed object access also clears previous values', () => {
    const container = dom();
    view.renderUsageReport(container, report(), org);
    const malformed = { get organizationId() { throw new Error('malformed report'); } };
    assert.equal(view.renderUsageReport(container, malformed, org).status, 'unavailable');
    unavailable(container);
});

test('browser export renders without touching network, storage or actions', () => {
    const sandbox = {};
    for (const name of ['fetch', 'localStorage', 'sessionStorage', 'XMLHttpRequest'])
        Object.defineProperty(sandbox, name, { get() { throw new Error('Forbidden side effect: ' + name); } });
    vm.runInNewContext(fs.readFileSync(path.join(__dirname, '../usage-report-view.js'), 'utf8'), sandbox);
    assert.equal(typeof sandbox.FanasaConsumerUsageReport.renderUsageReport, 'function');
    const container = dom();
    assert.equal(sandbox.FanasaConsumerUsageReport.renderUsageReport(container, report(), org).status, 'ready');
    assert.equal(find(container, 'button').length, 0);
    assert.equal(find(container, 'form').length, 0);
    assert.equal(find(container, 'input').length, 0);
});

// Optional end-to-end fixtures are freshly exported by the C# SQLite harness.
// Ordinary unit runs do not depend on a build or retained results directory.
if (process.env.CONSUMER_USAGE_REPORT_FIXTURES) test('fresh Fabric report and C# mapper outputs render with the same amount, scope and state', () => {
    const directory = path.resolve(process.env.CONSUMER_USAGE_REPORT_FIXTURES), container = dom();
    const cases = [
        ['ready-empty.json', 'ready'], ['ready-legacy.json', 'ready'], ['ready-measured.json', 'ready'],
        ['ready-mixed.json', 'ready'], ['large-usd.json', 'ready'], ['not-recorded.json', 'notRecorded'],
        ['incomplete.json', 'incomplete'], ['unavailable.json', 'unavailable']
    ];
    for (const [file, expectedState] of cases) {
        const input = fs.readFileSync(path.join(directory, file), 'utf8'), data = JSON.parse(input), before = JSON.stringify(data);
        assert.equal(Object.hasOwn(data, 'subject'), false, file);
        assert.equal(view.renderUsageReport(container, data, data.organizationId).status, expectedState, file);
        assert.equal(JSON.stringify(data), before, file + ' remains read-only');
        if (expectedState !== 'ready') {
            assert.equal(find(container, 'dd').length, 0, file);
            assert.equal(find(container, 'tbody').length, 0, file);
            continue;
        }
        assert.equal(find(container, 'dd')[0].textContent, view.formatExactDecimal(data.totalAmount) + ' ' + data.currency, file);
        assert.equal(find(container, 'dd')[1].textContent, new Intl.NumberFormat('fa-IR').format(new Set(data.items.map(row => row.productKey)).size), file);
        const period = find(container, 'p').find(node => node.getAttribute('data-from'));
        assert.equal(period.getAttribute('data-from'), data.from, file);
        assert.equal(period.getAttribute('data-to'), data.to, file);
        const rows = find(container, 'tbody')[0]?.children ?? [];
        assert.equal(rows.length, data.items.length, file);
        rows.forEach((row, index) => {
            const source = data.items[index], cells = row.children;
            assert.equal(cells[0].textContent, source.name, file);
            assert.equal(cells[2].textContent, source.quantity === null ? 'نامعلوم' : view.formatExactDecimal(source.quantity) + source.unit, file);
            assert.equal(cells[3].textContent, view.formatExactDecimal(source.amount) + ' ' + data.currency, file);
        });
    }
    const large = JSON.parse(fs.readFileSync(path.join(directory, 'large-usd.json'), 'utf8'));
    view.renderUsageReport(container, large, large.organizationId);
    assert.equal(find(container, 'dd')[0].textContent, '9,007,199,254,740,993.10 USD');
    view.renderUsageReport(container, large, otherOrg);
    unavailable(container);
    assert.doesNotMatch(container.textContent, /USD|9,007,199/);
});
