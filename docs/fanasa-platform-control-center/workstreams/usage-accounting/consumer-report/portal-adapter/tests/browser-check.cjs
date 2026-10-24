'use strict';

// Uses only fresh fixtures exported by PortalAdapter.Tests and existing local tools.
// No server, browser profile, package installation, or network connection is required.
const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const fs = require('node:fs');
const path = require('node:path');

if (process.argv.length !== 4 || process.argv[2] !== '--fixtures') {
    throw new Error('Use --fixtures followed by the fresh C# adapter fixture directory.');
}
const fixtureDirectory = path.resolve(process.argv[3]);
const moduleDirectory = path.resolve(__dirname, '..');
const repository = path.resolve(__dirname, '../../../../../../..');
const playwrightModule = process.env.FANASA_UI_PLAYWRIGHT_MODULE
    || 'C:/Users/JavadSeyediPC/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright';
const browserExecutable = process.env.FANASA_UI_BROWSER_EXECUTABLE
    || 'C:/Program Files/Google/Chrome/Application/chrome.exe';
if (!fs.existsSync(playwrightModule) || !fs.existsSync(browserExecutable)) {
    throw new Error('Provide an existing local Playwright module and browser executable through FANASA_UI_PLAYWRIGHT_MODULE and FANASA_UI_BROWSER_EXECUTABLE.');
}
const { chromium } = require(playwrightModule);
const viewPath = path.join(moduleDirectory, 'usage-report-view.js');
const stylePath = path.join(moduleDirectory, 'usage-report-view.css');
const portalAssets = path.join(repository, 'products/Fanasa.UnifiedPortal/src/Fanasa.UnifiedPortal.Web/wwwroot');
const source = fs.readFileSync(viewPath, 'utf8');
const style = fs.readFileSync(stylePath, 'utf8');
let portalStyle = fs.readFileSync(path.join(portalAssets, 'portal.css'), 'utf8');
for (const weight of ['Regular', 'Bold']) {
    const font = fs.readFileSync(path.join(portalAssets, 'fonts/vazir/Vazir-' + weight + '.woff2'));
    portalStyle = portalStyle.replace("url('/fonts/vazir/Vazir-" + weight + ".woff2')",
        'url("data:font/woff2;base64,' + font.toString('base64') + '")');
}
const fixtureNames = ['ready-mixed', 'ready-empty', 'ready-measured', 'ready-legacy',
    'large-usd', 'not-recorded', 'incomplete', 'unavailable'];
const fixtures = Object.fromEntries(fixtureNames.map(name => [name,
    JSON.parse(fs.readFileSync(path.join(fixtureDirectory, name + '.json'), 'utf8'))]));
const digest = value => crypto.createHash('sha256').update(value).digest('hex');
const runName = 'browser-' + new Date().toISOString().replace(/[:.]/g, '-') + '-' + process.pid;
const output = path.join(__dirname, 'results', runName);
fs.mkdirSync(output, { recursive: true });
const results = [], requests = [], pageErrors = [], dialogs = [], screenshots = [], geometry = [];
let browser, page, failure, version;

const exact = value => {
    const [integer, fraction] = value.split('.');
    return integer.replace(/\B(?=(\d{3})+(?!\d))/g, ',')
        + (fraction === undefined ? '' : '.' + fraction);
};
async function check(name, action) {
    try { await action(); results.push({ name, passed: true }); console.log('PASS ' + name); }
    catch (error) { results.push({ name, passed: false, error: error.message }); throw error; }
}
async function render(report, organization = report.organizationId) {
    return page.evaluate(({ report, organization }) => globalThis.FanasaConsumerUsageReport.renderUsageReport(
        document.getElementById('report-host'), report, organization), { report, organization });
}
async function capture(name) {
    await page.screenshot({ path: path.join(output, name + '.png'), fullPage: true, animations: 'disabled' });
    screenshots.push(name + '.png');
}
async function assertGeometry(name) {
    const bounds = await page.evaluate(() => ({
        viewport: document.documentElement.clientWidth,
        document: document.documentElement.scrollWidth,
        body: document.body.scrollWidth,
        direction: getComputedStyle(document.querySelector('.consumer-usage-report')).direction,
        expanding: Array.from(document.querySelectorAll('body *')).map(element => {
            const box = element.getBoundingClientRect();
            return { tag: element.tagName, class: element.className, left: box.left, right: box.right };
        }).filter(box => box.left < -1 || box.right > document.documentElement.clientWidth + 1).slice(0, 8)
    }));
    geometry.push({ name, ...bounds });
    assert.equal(bounds.direction, 'rtl');
    assert.ok(bounds.document <= bounds.viewport + 1,
        'Document overflows: ' + JSON.stringify(bounds));
    assert.ok(bounds.body <= bounds.viewport + 1, 'Body overflows: ' + JSON.stringify(bounds));
}
async function assertRows(report) {
    const actual = await page.locator('.consumer-usage-table tbody tr').evaluateAll(rows => rows.map(row => {
        const cells = row.querySelectorAll('td');
        return { name: cells[0].querySelector('strong').textContent,
            meter: cells[1].textContent, quantity: cells[2].textContent,
            amount: cells[3].textContent, count: cells[4].textContent };
    }));
    assert.equal(actual.length, report.items.length);
    for (let index = 0; index < report.items.length; index++) {
        const item = report.items[index], row = actual[index];
        assert.equal(row.name, item.name);
        assert.ok(row.meter.includes(item.meterId));
        if (item.meterVersion !== null) assert.ok(row.meter.includes('نسخه ' + item.meterVersion));
        assert.equal(row.quantity, item.quantity === null ? 'نامعلوم' : exact(item.quantity) + item.unit);
        assert.equal(row.amount, exact(item.amount) + ' ' + report.currency);
        assert.equal(row.count, new Intl.NumberFormat('fa-IR').format(item.chargeCount));
    }
}
async function assertNoFinancialRows() {
    assert.equal(await page.locator('.consumer-usage-summary').count(), 0);
    assert.equal(await page.locator('.consumer-usage-table').count(), 0);
}

(async () => {
    try {
        browser = await chromium.launch({ executablePath: browserExecutable, headless: true,
            args: ['--disable-background-networking', '--disable-component-update', '--disable-domain-reliability',
                '--no-first-run', '--no-default-browser-check', '--disable-sync'] });
        version = browser.version();
        const context = await browser.newContext({ viewport: { width: 1440, height: 1000 },
            locale: 'fa-IR', timezoneId: 'Asia/Tehran', reducedMotion: 'reduce',
            acceptDownloads: false, serviceWorkers: 'block' });
        await context.route('**/*', async route => { requests.push(route.request().url()); await route.abort(); });
        page = await context.newPage();
        page.on('pageerror', error => pageErrors.push(error.message));
        page.on('dialog', async dialog => { dialogs.push(dialog.message()); await dialog.dismiss(); });
        await page.setContent('<!doctype html><html lang="fa" dir="rtl"><head><meta charset="utf-8">'
            + '<title>Consumer usage report verification</title><style>' + portalStyle + '\n' + style
            + '</style></head><body><main class="portal-shell"><section class="workspace-panel">'
            + '<div id="report-host"></div></section></main></body></html>');
        await page.addScriptTag({ content: source });
        await page.evaluate(() => document.fonts.ready);
        await check('local renderer and embedded Vazir load without a server', async () => {
            assert.equal(await page.evaluate(() => typeof globalThis.FanasaConsumerUsageReport.renderUsageReport), 'function');
            assert.equal(await page.evaluate(() => document.fonts.check('13px Vazir')), true);
        });

        const mixed = fixtures['ready-mixed'];
        for (const width of [1440, 390, 320]) {
            await page.setViewportSize({ width, height: width === 1440 ? 1000 : 900 });
            await check('mixed measured and unknown consumption renders exactly at ' + width + 'px', async () => {
                assert.deepEqual(await render(mixed), { status: 'ready', itemCount: mixed.items.length });
                await assertRows(mixed);
                const summary = page.locator('.consumer-usage-summary dd');
                assert.equal(await summary.nth(0).textContent(), exact(mixed.totalAmount) + ' ' + mixed.currency);
                assert.equal(await summary.nth(1).textContent(), new Intl.NumberFormat('fa-IR').format(new Set(mixed.items.map(item => item.productKey)).size));
                assert.equal(await page.locator('.consumer-usage-period').textContent(), mixed.periodLabel);
                assert.equal(await page.locator('.consumer-usage-period').getAttribute('data-from'), mixed.from);
                assert.equal(await page.locator('.consumer-usage-period').getAttribute('data-to'), mixed.to);
                assert.equal(await page.locator('.consumer-usage-summary time').getAttribute('datetime'), mixed.asOf);
                assert.equal(await page.locator('.consumer-usage-summary time').getAttribute('title'), mixed.asOf);
                assert.ok((await page.locator('.consumer-usage-summary time').textContent()).includes('UTC'));
                assert.equal(await page.locator('[role="status"]').getAttribute('aria-live'), 'polite');
                assert.doesNotMatch(await page.locator('#report-host').textContent(), /پیش.?نمایش|داده.*نمونه|سقف طرح|سهمیه|preview|synthetic/i);
            });
            await check('RTL document contains its table at ' + width + 'px', () => assertGeometry('mixed-' + width));
            await check('table scroll region is keyboard accessible at ' + width + 'px', async () => {
                const scroll = page.locator('.consumer-usage-table-scroll');
                assert.equal(await scroll.getAttribute('tabindex'), '0');
                assert.equal(await scroll.getAttribute('role'), 'region');
                assert.equal(await scroll.getAttribute('aria-label'), 'جزئیات مصرف سازمان');
                await page.evaluate(() => document.activeElement.blur());
                await page.keyboard.press('Tab');
                assert.equal(await scroll.evaluate(element => document.activeElement === element), true);
                if (width < 600) {
                    assert.equal(await scroll.evaluate(element => element.scrollWidth > element.clientWidth), true);
                    await page.keyboard.press('ArrowLeft');
                    await page.waitForFunction(() => Math.abs(document.querySelector('.consumer-usage-table-scroll').scrollLeft) > 1);
                    assert.ok(await scroll.evaluate(element => Math.abs(element.scrollLeft) > 1));
                    await scroll.evaluate(element => { element.scrollLeft = 0; });
                }
            });
            await capture('ready-mixed-' + width);
            if (width < 600) {
                await page.locator('.consumer-usage-table-scroll').evaluate(element => {
                    element.scrollLeft = element.clientWidth - element.scrollWidth;
                });
                await capture('ready-mixed-' + width + '-amounts');
                await page.locator('.consumer-usage-table-scroll').evaluate(element => { element.scrollLeft = 0; });
            }
        }

        await page.setViewportSize({ width: 390, height: 900 });
        await check('persisted empty ready report displays known zero without unknown rows', async () => {
            assert.deepEqual(await render(fixtures['ready-empty']), { status: 'ready', itemCount: 0 });
            assert.equal(await page.locator('.consumer-usage-table').count(), 0);
            assert.equal(await page.locator('.consumer-usage-summary dd').first().textContent(), '0 IRR');
            assert.equal(await page.locator('.consumer-usage-empty').textContent(), 'هزینهٔ ثبت‌شده در این دوره صفر است.');
            await assertGeometry('ready-empty-390');
            await capture('ready-empty-390');
        });
        for (const [key, status, message] of [
            ['not-recorded', 'notRecorded', 'هنوز گزارشی ثبت نشده است.'],
            ['incomplete', 'incomplete', 'گزارش مصرف کامل نیست.'],
            ['unavailable', 'unavailable', 'گزارش مصرف در دسترس نیست.']
        ]) {
            await check(status + ' remains distinct with no partial or fabricated amounts', async () => {
                assert.deepEqual(await render(fixtures[key]), { status, itemCount: 0 });
                await assertNoFinancialRows();
                assert.equal(await page.locator('[role="status"]').textContent(), message);
                await assertGeometry(key + '-390');
                await capture(key + '-390');
            });
        }
        await check('large USD values retain every digit and fractional zero', async () => {
            const large = fixtures['large-usd'];
            assert.deepEqual(await render(large), { status: 'ready', itemCount: large.items.length });
            assert.equal(await page.locator('.consumer-usage-summary dd').first().textContent(), exact(large.totalAmount) + ' USD');
            await assertRows(large);
            await assertGeometry('large-usd-390');
            await capture('large-usd-390');
            await page.locator('.consumer-usage-table-scroll').evaluate(element => {
                element.scrollLeft = element.clientWidth - element.scrollWidth;
            });
            await capture('large-usd-390-amounts');
        });
        await check('approved hostile display name remains text without execution or requests', async () => {
            const hostile = structuredClone(fixtures['ready-measured']);
            hostile.items[0].name = '<img src="https://invalid.example/usage" onerror="globalThis.usageNameExecuted=1">';
            await render(hostile);
            assert.equal(await page.locator('.consumer-usage-table tbody strong').first().textContent(), hostile.items[0].name);
            assert.equal(await page.locator('#report-host img, #report-host script').count(), 0);
            assert.equal(await page.evaluate(() => globalThis.usageNameExecuted), undefined);
            await assertGeometry('hostile-name-390');
        });
        await check('organization change clears all previous financial and historical display data', async () => {
            await render(mixed);
            const other = mixed.organizationId === '33333333-3333-4333-8333-333333333333'
                ? '44444444-4444-4444-8444-444444444444' : '33333333-3333-4333-8333-333333333333';
            assert.deepEqual(await render(mixed, other), { status: 'unavailable', itemCount: 0 });
            await assertNoFinancialRows();
            assert.equal(await page.locator('.consumer-usage-period').count(), 0);
            const visible = await page.locator('#report-host').textContent();
            for (const item of mixed.items) { assert.ok(!visible.includes(item.name)); assert.ok(!visible.includes(item.productKey)); }
            assert.equal(await page.locator('.consumer-usage-report').count(), 1);
            await capture('scope-rejected-390');
        });
        await check('malformed numeric money clears a previously ready report', async () => {
            await render(mixed);
            const invalid = structuredClone(mixed); invalid.totalAmount = 17;
            assert.deepEqual(await render(invalid), { status: 'unavailable', itemCount: 0 });
            await assertNoFinancialRows();
        });
        await check('all pages remain free of network traffic, dialogs, and uncaught errors', async () => {
            assert.deepEqual(requests, []); assert.deepEqual(pageErrors, []); assert.deepEqual(dialogs, []);
        });
    } catch (error) {
        failure = error.stack || String(error);
        if (page) try { await capture('failure'); } catch { /* Keep the original failure. */ }
        console.error(failure); process.exitCode = 1;
    } finally {
        if (browser) await browser.close();
        const evidence = { passed: !failure, browserVersion: version, browserExecutable, playwrightModule,
            fixtureDirectory, viewSha256: digest(source), styleSha256: digest(style),
            fixtureSha256: Object.fromEntries(fixtureNames.map(name => [name,
                digest(fs.readFileSync(path.join(fixtureDirectory, name + '.json')))])),
            groups: results.length, passedGroups: results.filter(result => result.passed).length,
            results, geometry, requests, pageErrors, dialogs, screenshots, failure };
        fs.writeFileSync(path.join(output, 'results.json'), JSON.stringify(evidence, null, 2) + '\n');
        console.log((failure ? 'FAIL' : 'PASS') + ' consumer usage browser: '
            + evidence.passedGroups + '/' + evidence.groups + ' groups; evidence ' + output);
    }
})();
