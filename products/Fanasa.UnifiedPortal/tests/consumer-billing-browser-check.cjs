'use strict';
// Network-free renderer QA. No application server, real credential, cookie or storage is used.
const assert = require('node:assert/strict');
const fs = require('node:fs'), path = require('node:path');
const modulePath = process.env.FANASA_UI_PLAYWRIGHT_MODULE;
const browserPath = process.env.FANASA_UI_BROWSER_EXECUTABLE;
if (!modulePath || !browserPath) throw Error('Existing Playwright module and Chromium executable paths are required.');
const { chromium } = require(modulePath);
const assets = path.resolve(__dirname, '../src/Fanasa.UnifiedPortal.Web/wwwroot');
const output = path.resolve(__dirname, '../docs/fixtures/consumer-billing-2026-10-24');
const org = '11111111-2222-4333-8444-555555555555';
const invoice = { id: 'aaaaaaaa-bbbb-4ccc-8ddd-eeeeeeeeeeee', period: '2026-09', amount: '9007199254740994.10', creditedAmount: '0.50', paidAmount: '0.50', remainingAmount: '9007199254740993.10', status: 'partiallyPaid', issuedAt: '2026-10-01T00:00:00Z', from: '2026-09-01T00:00:00Z', to: '2026-10-01T00:00:00Z' };
const account = { organizationId: org, status: 'ready', asOf: '2026-10-24T12:00:00.0000000+00:00', mode: 'postpaid', currency: 'USD', walletBalance: '0', outstanding: '9007199254740993.10', availableCredit: '0.90', invoices: [invoice] };
const usage = { organizationId: org, status: 'ready', from: '2026-10-01T00:00:00+00:00', to: '2026-10-24T12:00:00+00:00', periodLabel: '2026/10/01 تا پیش از 2026/10/24 12:00 · UTC', asOf: account.asOf, currency: 'USD', totalAmount: '9007199254740993.10', basis: 'posted-ledger', items: [
    { productKey: 'documents', name: 'اسناد سازمان', meterId: 'pages', meterVersion: '1', unit: 'صفحه', quantity: '1250.50', amount: '0.10', chargeCount: 1 },
    { productKey: 'documents', name: 'اسناد سازمان', meterId: 'pages', meterVersion: null, unit: null, quantity: null, amount: '9007199254740993.00', chargeCount: 2 }
] };
const checks = [], pageErrors = [], network = [], screenshots = [];
async function check(name, action) { await action(); checks.push(name); console.log('PASS ' + name); }
async function boundedClose(action, name) {
    let timer;
    try {
        await Promise.race([action(), new Promise((_, reject) => { timer = setTimeout(() => reject(Error(name + ' shutdown exceeded 10 seconds')), 10000); })]);
    } finally { clearTimeout(timer); }
}
(async () => {
    fs.mkdirSync(output, { recursive: true });
    // launchServer exposes the exact child process owned by this harness, so a
    // shutdown failure cannot leave an unbounded Chrome process behind.
    const server = await chromium.launchServer({ executablePath: browserPath, headless: true, args: ['--disable-background-networking', '--disable-component-update', '--no-first-run'] });
    const browser = await chromium.connect(server.wsEndpoint());
    let context, page, evidence;
    try {
        context = await browser.newContext({ serviceWorkers: 'block' });
        await context.route('**/*', route => { network.push(route.request().url()); return route.abort(); });
        page = await context.newPage(); page.on('pageerror', error => pageErrors.push(error.message));
        const css = ['portal.css', 'workspace.css', 'consumer-usage-report.css'].map(file => fs.readFileSync(path.join(assets, file), 'utf8')).join('\n');
        await page.setContent('<!doctype html><html lang="fa" dir="rtl"><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><style>' + css + '\nbody{margin:0}main{max-width:1280px;margin:auto;padding:16px;min-width:0}</style><main class="service-workspace"><div id="usage"></div><div id="account"></div></main></html>');
        for (const file of ['consumer-usage-report.js', 'consumer-billing.js']) await page.addScriptTag({ content: fs.readFileSync(path.join(assets, file), 'utf8') });
        const render = async (a = account, u = usage, expected = org) => page.evaluate(({ a, u, expected }) => {
            FanasaConsumerUsageReport.renderUsageReport(document.getElementById('usage'), u, expected);
            FanasaConsumerBilling.renderConsumerAccount(document.getElementById('account'), a, expected);
        }, { a, u, expected });
        for (const width of [1440, 390, 320]) {
            await page.setViewportSize({ width, height: 1000 }); await render();
            await check('RTL geometry and exact large money at ' + width, async () => {
                assert.ok(await page.locator('#account').innerText().then(text => text.includes('9,007,199,254,740,993.10')));
                assert.equal(await page.locator('#usage dd').nth(1).textContent(), '۱');
                assert.match(await page.locator('#usage tbody tr').nth(1).innerText(), /نامعلوم/);
                const geometry = await page.evaluate(() => ({ viewport: innerWidth, body: document.body.scrollWidth, html: document.documentElement.scrollWidth,
                    regions: [...document.querySelectorAll('[role=region]')].map(element => ({ left: element.getBoundingClientRect().left, right: element.getBoundingClientRect().right, width: element.clientWidth, scroll: element.scrollWidth })) }));
                assert.ok(geometry.body <= width + 1 && geometry.html <= width + 1, JSON.stringify(geometry));
                assert.ok(geometry.regions.every(region => region.left >= -1 && region.right <= width + 1), JSON.stringify(geometry));
                assert.equal(await page.locator('html').getAttribute('dir'), 'rtl');
            });
            const filename = 'billing-report-' + width + '.png'; await page.screenshot({ path: path.join(output, filename), fullPage: true }); screenshots.push(filename);
        }
        await check('old account and usage cleared on scope change', async () => {
            await render(account, usage, '66666666-7777-4888-8999-aaaaaaaaaaaa');
            assert.equal(await page.locator('tbody').count(), 0); assert.equal(await page.locator('dd').count(), 0);
            assert.doesNotMatch(await page.locator('main').innerText(), /USD|9,007,199/);
        });
        await check('PAYG statement exposes no due or payment action', async () => {
            await render({ ...account, mode: 'payg', outstanding: '0', invoices: [{ ...invoice, status: 'statement' }] });
            assert.match(await page.locator('#account').innerText(), /صورت مصرف|از اعتبار کسر شده/);
            assert.equal(await page.locator('#account th').count(), 4); assert.equal(await page.locator('button,form,a,input').count(), 0);
            assert.equal(await page.locator('#account [role=region]').getAttribute('tabindex'), '0');
        });
        await check('recorded zero and absent report remain distinct', async () => {
            await render({ ...account, walletBalance: '0', outstanding: '0', availableCredit: '0', invoices: [] }, { ...usage, totalAmount: '0', items: [] });
            assert.match(await page.locator('#usage').innerText(), /صفر است/);
            await render({ organizationId: org, status: 'notRecorded', asOf: null, mode: null, currency: null, walletBalance: null, outstanding: null, availableCredit: null, invoices: [] }, null);
            assert.match(await page.locator('#account').innerText(), /حسابی ثبت نشده است/); assert.equal(await page.locator('dd').count(), 0);
        });
        await check('untrusted names stay text and no external requests or page errors occur', async () => {
            const poisoned = { ...usage, items: usage.items.map(row => ({ ...row, name: '<img src=x onerror=alert(1)>' })) };
            await render(account, poisoned); assert.equal(await page.locator('img,script[src]').count(), 0);
            assert.match(await page.locator('#usage').innerText(), /<img src=x/);
            assert.deepEqual(network, []); assert.deepEqual(pageErrors, []);
        });
        evidence = { verifiedAtUtc: new Date().toISOString(), browser: browser.version(), checks, pageErrors, network, screenshots };
    } finally {
        try {
            if (page && !page.isClosed()) await boundedClose(() => page.close({ runBeforeUnload: false }), 'page');
            if (context) await boundedClose(() => context.close(), 'context');
            await boundedClose(() => browser.close(), 'browser connection');
            await boundedClose(() => server.close(), 'owned browser process');
        } catch (error) {
            try { await boundedClose(() => server.kill(), 'forced owned browser'); }
            catch { server.process().kill(); }
            throw error;
        }
    }
    evidence.shutdown = 'clean';
    fs.writeFileSync(path.join(output, 'result.json'), JSON.stringify(evidence, null, 2));
    console.log('PASS ' + checks.length + ' browser groups; no network or real credentials; clean shutdown.');
})().catch(error => { console.error(error); process.exitCode = 1; });
