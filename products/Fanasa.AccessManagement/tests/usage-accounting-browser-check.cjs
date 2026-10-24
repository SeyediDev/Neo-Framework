'use strict';

// Run against the disposable Organization.Tests --preview host, never a production host.
// Supply an existing Playwright module and installed browser; this script installs nothing.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { parseReportJson } = require('../src/Fanasa.AccessManagement.Web/wwwroot/js/usage-accounting.js');
const { chromium } = require(process.env.FANASA_UI_PLAYWRIGHT_MODULE || 'playwright');
const origin = 'http://127.0.0.1:5198';
const output = path.resolve(process.argv[2] || 'docs/fanasa-platform-control-center/workstreams/usage-accounting/fixtures');
const results = [];
let browser;

async function check(name, action) {
    await action(); results.push({ name, passed: true }); console.log('PASS ' + name);
}
async function loaded(page) {
    await page.waitForFunction(() => document.getElementById('usage-accounting-app')?.getAttribute('aria-busy') === 'false');
}
async function screenshot(page, name) {
    await page.screenshot({ path: path.join(output, name), fullPage: false, animations: 'disabled' });
}
async function reopen(page) {
    await page.goto(origin + '/UsageAccounting'); await loaded(page);
}
async function pendingDetails(page) {
    await page.locator('#ua-pending-list button').first().click();
    await page.locator('#ua-detail[open]').waitFor();
}
async function currentReport(page) {
    const consumer = await page.locator('#usage-accounting-app').getAttribute('data-tenant');
    const response = await page.request.get(origin + '/api/usage-accounting/organization/' + consumer + '/reconciliation');
    assert.equal(response.status(), 200);
    return parseReportJson(await response.text());
}

(async () => {
    fs.mkdirSync(output, { recursive: true });
    browser = await chromium.launch({ executablePath: process.env.FANASA_UI_BROWSER_EXECUTABLE, headless: true, chromiumSandbox: true });
    const context = await browser.newContext({ viewport: { width: 1440, height: 1050 }, reducedMotion: 'reduce' });
    // No external requests, existing browser profile, or security/TLS overrides are used.
    await context.route('**/*', route => new URL(route.request().url()).origin === origin ? route.continue() : route.abort());
    const page = await context.newPage();
    page.on('dialog', dialog => dialog.accept());
    const pageErrors = []; page.on('pageerror', error => { pageErrors.push(error.message); console.error('PAGE ERROR ' + error.message); });
    let original;

    await check('live preview renders 27 exact operations and responsive overview', async () => {
        await reopen(page); original = await currentReport(page);
        assert.equal(original.counts.committed, 24); assert.equal(original.counts.pending, 3);
        assert.equal(await page.locator('#ua-table-body tr').count(), 27);
        assert.equal(await page.locator('#ua-metric-committed').textContent(), '۲۴');
        assert.equal(await page.locator('#ua-metric-pending').textContent(), '۳');
        assert.equal(await page.locator('.ua-demo').isVisible(), true);
        assert.equal(await page.locator('html').getAttribute('dir'), 'rtl');
        await screenshot(page, 'usage-accounting-desktop.v3.png');
    });

    await check('status filter, Persian number search and empty search state', async () => {
        await page.selectOption('#ua-filter', 'accounting-pending');
        assert.equal(await page.locator('#ua-table-body tr').count(), 3);
        await page.fill('#ua-search', '۱۲۰۰۰۰');
        assert.equal(await page.locator('#ua-table-body tr').count(), 3);
        await page.fill('#ua-search', 'no-such-retained-operation');
        assert.equal(await page.locator('#ua-table-empty').isVisible(), true);
        assert.equal(await page.locator('#ua-table-body tr').count(), 0);
        await page.fill('#ua-search', ''); await page.selectOption('#ua-filter', 'all');
    });

    await check('recovery form requires review reason and antiforgery rejects missing token', async () => {
        await pendingDetails(page);
        assert.equal(await page.locator('#ua-recovery-submit').isDisabled(), true);
        const operation = original.operations.find(x => x.status === 'accounting-pending');
        const response = await page.request.post(origin + '/api/usage-accounting/organization/' + original.consumerTenantId + '/recover', {
            data: { businessKey: operation.ledgerKey, expectedOperationFingerprint: operation.operationFingerprint,
                requestId: '11111111-1111-4111-8111-111111111111', reason: 'Synthetic missing-token check' }
        });
        assert.equal(response.status(), 400);
        assert.equal((await currentReport(page)).counts.committed, 24);
        await page.fill('#ua-recovery-reason', 'رفع محدودیت حساب نمونه برای بررسی رابط');
        assert.equal(await page.locator('#ua-recovery-submit').isEnabled(), true);
        await screenshot(page, 'usage-accounting-detail.v3.png');
    });

    await check('real recovery changes one pending operation once and refreshes dashboard', async () => {
        const posted = page.waitForResponse(response => response.url().endsWith('/recover') && response.request().method() === 'POST');
        await page.click('#ua-recovery-submit'); const response = await posted;
        assert.equal(response.status(), 200);
        const receipt = parseReportJson(await response.text()); assert.equal(receipt.status, 'committed');
        await page.waitForFunction(() => document.getElementById('ua-metric-pending')?.textContent === '۲');
        assert.equal((await currentReport(page)).counts.committed, 25);
        assert.equal(await page.locator('#ua-recovery-submit').isDisabled(), true);
        await page.click('#ua-detail-close');
    });

    await check('lost response after server commit preserves request across refresh and retries idempotently', async () => {
        await pendingDetails(page); await page.fill('#ua-recovery-reason', 'بررسی پاسخ نامشخص حساب نمونه');
        const commands = []; let attempts = 0;
        await page.route('**/api/usage-accounting/organization/*/recover', async route => {
            commands.push(route.request().postDataJSON()); attempts++;
            const response = await route.fetch();
            if (attempts === 1) await route.abort('failed'); else await route.fulfill({ response });
        });
        await page.click('#ua-recovery-submit');
        await page.waitForFunction(() => document.getElementById('ua-recovery-message')?.textContent.includes('مشخص نیست'));
        assert.equal((await currentReport(page)).counts.committed, 26);
        assert.equal(await page.locator('#ua-recovery-reason').isEditable(), false);
        assert.equal(await page.locator('#ua-tenant').isDisabled(), true);
        assert.equal(await page.locator('#ua-new-request').isVisible(), false);
        await page.reload(); await loaded(page);
        await page.locator('#ua-banner button').click();
        assert.equal(await page.locator('#ua-recovery-request').textContent(), commands[0].requestId);
        assert.equal(await page.locator('#ua-recovery-reason').inputValue(), commands[0].reason);
        await page.click('#ua-recovery-submit');
        await page.waitForFunction(() => document.getElementById('ua-recovery-message')?.textContent.includes('انجام'));
        assert.equal(attempts, 2); assert.deepEqual(commands[1], commands[0]);
        assert.equal((await currentReport(page)).counts.committed, 26);
        await page.unroute('**/api/usage-accounting/organization/*/recover');
        await page.click('#ua-detail-close');
    });

    await check('known pending receipt requires deliberate new request identity', async () => {
        await pendingDetails(page); await page.fill('#ua-recovery-reason', 'محدودیت حساب نمونه هنوز باقی است');
        const commands = [];
        await page.route('**/api/usage-accounting/organization/*/recover', async route => {
            const command = route.request().postDataJSON(); commands.push(command);
            await route.fulfill({ status: 200, contentType: 'application/json', body: JSON.stringify({
                ...command, consumerTenantId: original.consumerTenantId, operationFingerprint: command.expectedOperationFingerprint,
                requestFingerprint: 'f'.repeat(64), previousStatus: 'accounting-pending',
                status: 'accounting-pending', amount: 120000, actor: 'synthetic-browser-check',
                recordedAt: new Date().toISOString()
            }) });
        });
        await page.click('#ua-recovery-submit');
        await page.locator('#ua-new-request').waitFor();
        assert.equal(await page.locator('#ua-recovery-submit').isDisabled(), true);
        await page.click('#ua-new-request'); await page.fill('#ua-recovery-reason', 'رفع محدودیت جدید حساب نمونه');
        await page.click('#ua-recovery-submit'); await page.locator('#ua-new-request').waitFor();
        assert.equal(commands.length, 2); assert.notEqual(commands[1].requestId, commands[0].requestId);
        assert.equal((await currentReport(page)).counts.committed, 26);
        await page.unroute('**/api/usage-accounting/organization/*/recover');
        await page.click('#ua-detail-close');
    });

    const live = await currentReport(page);
    async function mockReport(value) {
        await page.unroute('**/reconciliation*');
        await page.route('**/reconciliation*', route => route.fulfill({ status: 200, contentType: 'application/json',
            body: typeof value === 'string' ? value : JSON.stringify(value) }));
        await reopen(page);
    }

    await check('incomplete or corrupt report disables recovery and explains evidence limits', async () => {
        await mockReport({ ...live, coverage: 'incomplete', status: 'incomplete', integrityVerified: false, canRecoverPending: false,
            findings: [...live.findings, { code: 'scan-limit-exceeded' }] });
        assert.equal(await page.locator('#ua-metric-coverage').textContent(), 'ناقص');
        await pendingDetails(page);
        assert.equal(await page.locator('#ua-recovery-reason').isDisabled(), true);
        assert.equal(await page.locator('#ua-recovery-submit').isDisabled(), true);
        await page.click('#ua-detail-close');
        await mockReport({ ...live, status: 'issues', integrityVerified: false, canRecoverPending: false,
            findings: [...live.findings, { code: 'missing-ledger-charge', ledgerKey: live.operations[0].ledgerKey }] });
        await pendingDetails(page);
        assert.equal(await page.locator('#ua-recovery-reason').isDisabled(), true);
        assert.equal(await page.locator('#ua-recovery-submit').isDisabled(), true);
        await page.click('#ua-detail-close');
    });

    await check('failed refresh keeps last report visible but disables a new recovery', async () => {
        await mockReport(live); await pendingDetails(page);
        await page.fill('#ua-recovery-reason', 'بازبینی پیش از شکست دریافت گزارش');
        assert.equal(await page.locator('#ua-recovery-submit').isEnabled(), true);
        await page.click('#ua-detail-close'); await page.unroute('**/reconciliation*');
        await page.route('**/reconciliation*', route => route.fulfill({ status: 503, contentType: 'application/json', body: '{}' }));
        await page.click('#ua-refresh'); await loaded(page);
        assert.equal(await page.locator('#ua-content').isVisible(), true);
        assert.ok((await page.locator('#ua-load-status').textContent()).includes('به‌روز نیست'));
        await pendingDetails(page);
        assert.equal(await page.locator('#ua-recovery-submit').isDisabled(), true);
        await page.click('#ua-detail-close');
    });

    await check('large wire decimals remain exact on mobile and drawer stays within viewport', async () => {
        const amount = '999999999999999999.123456789';
        const value = { ...live, operations: live.operations.map(x => ({ ...x, amount: '__EXACT_DECIMAL__' })) };
        const body = JSON.stringify(value).replaceAll('"__EXACT_DECIMAL__"', amount);
        await page.setViewportSize({ width: 390, height: 844 }); await mockReport(body);
        const overflow = await page.evaluate(() => document.documentElement.scrollWidth > innerWidth);
        assert.equal(overflow, false);
        await screenshot(page, 'usage-accounting-mobile.v3.png');
        await pendingDetails(page);
        assert.equal(await page.locator('#ua-detail-amount').textContent(), '999,999,999,999,999,999.123456789');
        const geometry = await page.locator('#ua-detail-amount').evaluate(node => ({
            overflow: node.scrollWidth > node.clientWidth + 1, right: node.getBoundingClientRect().right,
            left: node.getBoundingClientRect().left, width: innerWidth
        }));
        assert.equal(geometry.overflow, false); assert.ok(geometry.left >= 0 && geometry.right <= geometry.width);
        await screenshot(page, 'usage-accounting-mobile-detail.v3.png');
        await page.click('#ua-detail-close');
        await page.setViewportSize({ width: 320, height: 844 }); await mockReport(body);
        assert.equal(await page.evaluate(() => document.documentElement.scrollWidth > innerWidth), false);
        await pendingDetails(page);
        assert.equal(await page.locator('#ua-detail-amount').textContent(), '999,999,999,999,999,999.123456789');
        assert.equal(await page.locator('#ua-detail-amount').evaluate(node => node.scrollWidth > node.clientWidth + 1), false);
        await screenshot(page, 'usage-accounting-mobile-detail-320.v3.png');
        await page.click('#ua-detail-close');
        await page.setViewportSize({ width: 390, height: 844 });
    });

    await check('empty report and failed read have clear distinct states', async () => {
        await mockReport({ ...live, status: 'clean', integrityVerified: true, canRecoverPending: false, findings: [], operations: [],
            counts: Object.fromEntries(Object.keys(live.counts).map(key => [key, 0])) });
        assert.equal(await page.locator('#ua-content').isVisible(), false);
        assert.ok((await page.locator('#ua-state-title').textContent()).includes('هنوز'));
        await screenshot(page, 'usage-accounting-empty.v3.png');
        await page.unroute('**/reconciliation*');
        await page.route('**/reconciliation*', route => route.fulfill({ status: 503, body: '' }));
        await reopen(page);
        assert.equal(await page.locator('#ua-content').isVisible(), false);
        assert.equal(await page.locator('#ua-state').isVisible(), true);
        assert.ok((await page.locator('#ua-state-text').textContent()).length > 0);
        await screenshot(page, 'usage-accounting-error.v3.png');
        await page.unroute('**/reconciliation*');
    });

    await check('malformed and foreign reports are rejected without rendering injected text', async () => {
        await mockReport({ ...live, consumerTenantId: 'ffffffff-ffff-4fff-8fff-ffffffffffff' });
        assert.equal(await page.locator('#ua-content').isVisible(), false);
        await mockReport({ ...live, operations: [{ ...live.operations[0], ledgerKey: '<img src=x onerror=alert(1)>' }] });
        assert.equal(await page.locator('#ua-content').isVisible(), false);
        assert.equal(await page.locator('#ua-table-body img').count(), 0);
        await page.unroute('**/reconciliation*'); await page.setViewportSize({ width: 1440, height: 1050 });
        await reopen(page);
    });

    await check('page reports no uncaught JavaScript errors', async () => assert.deepEqual(pageErrors, []));
    fs.writeFileSync(path.join(output, 'usage-accounting-browser-checks.v3.json'), JSON.stringify({
        verifiedAtUtc: new Date().toISOString(), browser: await browser.version(),
        scope: 'Disposable loopback preview with synthetic cookie identity. Live local recovery and CSRF; intercepted responses for UI state scenarios. No live SSO or production data.',
        checks: results
    }, null, 2));
})().catch(error => { console.error(error.stack); process.exitCode = 1; }).finally(async () => { if (browser) await browser.close(); });
