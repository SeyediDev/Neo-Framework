'use strict';

// Run only against Portal.Tests --workspace-preview --synthetic-admin-review.
// Uses an existing Playwright installation and browser; installs nothing.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const playwrightModule = process.env.FANASA_UI_PLAYWRIGHT_MODULE;
const browserExecutable = process.env.FANASA_UI_BROWSER_EXECUTABLE;
if (!playwrightModule || !browserExecutable) {
    throw new Error('Set FANASA_UI_PLAYWRIGHT_MODULE and FANASA_UI_BROWSER_EXECUTABLE to existing local installations.');
}
const { chromium } = require(playwrightModule);
const origin = 'http://127.0.0.1:5204';
const organizationA = '11111111-1111-4111-8111-111111111111';
const organizationB = '22222222-2222-4222-8222-222222222222';
const output = path.resolve(__dirname, '../docs/fixtures/workspace-2026-10-12');
const results = [], blockedRequests = [], mutationRequests = [], pageErrors = [], screenshots = [];
let browser, currentPage, browserVersion = '', failure;

function canonicalText(value) {
    return String(value || '').replace(/[۰-۹]/g, digit => String('۰۱۲۳۴۵۶۷۸۹'.indexOf(digit)))
        .replace(/[٠-٩]/g, digit => String('٠١٢٣٤٥٦٧٨٩'.indexOf(digit))).replace(/٬/g, ',').replace(/٫/g, '.');
}
async function check(name, action) {
    try { await action(); results.push({ name, passed: true }); console.log('PASS ' + name); }
    catch (error) { results.push({ name, passed: false, error: error.message }); throw error; }
}
async function loaded(page) {
    await page.locator('#service-workspace').waitFor();
    await page.waitForFunction(() => document.getElementById('service-workspace')?.dataset.ready === 'true'
        && document.getElementById('service-workspace')?.getAttribute('aria-busy') === 'false');
}
async function screenshot(page, name) {
    await page.screenshot({ path: path.join(output, name), fullPage: true, animations: 'disabled' });
    screenshots.push(name);
}
async function workspace(page, organization = organizationA, tab = 'catalog') {
    await page.goto(origin + '/Workspace?tenantId=' + organization + '#' + tab);
    await loaded(page);
}
async function bootstrap(page) {
    return page.locator('#workspace-bootstrap').evaluate(node => JSON.parse(node.textContent));
}
async function storage(page) {
    return page.evaluate(() => {
        const data = JSON.parse(document.getElementById('workspace-bootstrap').textContent);
        const key = 'fanasa.workspace.preview.v1.' + data.subjectKey + '.' + data.organizationId;
        const raw = sessionStorage.getItem(key);
        return { key, raw, value: raw ? JSON.parse(raw) : null };
    });
}
async function tab(page, name) {
    await page.locator('#tab-' + name).click();
    assert.equal(await page.locator('#tab-' + name).getAttribute('aria-selected'), 'true');
    assert.equal(await page.locator('#panel-' + name).isVisible(), true);
}
async function newContext(transformDocument) {
    const context = await browser.newContext({
        viewport: { width: 1440, height: 1050 }, reducedMotion: 'reduce',
        acceptDownloads: false, serviceWorkers: 'block'
    });
    // The exact preview origin is the only permitted network destination.
    await context.route('**/*', async route => {
        const request = route.request();
        let url;
        try { url = new URL(request.url()); } catch { blockedRequests.push(request.url()); await route.abort(); return; }
        if (url.origin !== origin) { blockedRequests.push(url.href); await route.abort('blockedbyclient'); return; }
        if (!['GET', 'HEAD', 'OPTIONS'].includes(request.method())) mutationRequests.push({ method: request.method(), path: url.pathname });
        if (transformDocument && request.resourceType() === 'document' && url.pathname === '/Workspace') {
            const response = await route.fetch({ maxRedirects: 0 });
            if (response.status() === 200) {
                await route.fulfill({ response, body: transformDocument(await response.text()) });
                return;
            }
            await route.fulfill({ response }); return;
        }
        await route.continue();
    });
    context.on('page', page => {
        page.on('pageerror', error => pageErrors.push({ url: page.url(), message: error.message }));
        page.on('download', download => download.cancel());
    });
    return context;
}
async function signIn(page, mode) {
    await page.goto(origin + '/preview/bootstrap/' + mode);
    await loaded(page);
}
async function openOffer(page, serviceId) {
    await tab(page, 'catalog');
    const opener = page.locator('#catalog-grid [data-offer-id="' + serviceId + '"]');
    await opener.click();
    await page.locator('#service-request-dialog[open]').waitFor();
    assert.equal(await page.locator('#service-request-dialog').evaluate(node => node.contains(document.activeElement)), true);
    return opener;
}
async function createRequest(page, reason) {
    await openOffer(page, 'service-automation');
    await page.selectOption('#request-form-plan', 'starter');
    await page.fill('#request-form-reason', reason);
    await page.click('#request-dialog-submit');
    await page.locator('#service-request-dialog').waitFor({ state: 'hidden' });
    assert.equal(await page.locator('#tab-requests').getAttribute('aria-selected'), 'true');
    await page.waitForFunction(() => document.activeElement?.id === 'tab-requests');
    const saved = await storage(page);
    const request = saved.value?.requests.find(item => item.reason === reason);
    assert.ok(request, 'new request is saved in the subject and organization scope');
    assert.equal(request.organizationId, organizationA);
    assert.equal(request.status, 'pending');
    assert.equal(request.history.length, 1);
    return request;
}
async function createPublication(page, name) {
    await tab(page, 'publish');
    await page.fill('#publish-name', name);
    const center = await page.locator('#publish-center option').evaluateAll(options => options.map(option => option.value).find(value => value === 'fanasa.rayan' || value === 'rayan'));
    assert.ok(center, 'official Rayan center is available');
    await page.selectOption('#publish-center', center);
    await page.fill('#publish-url', origin + '/preview/product/service-automation');
    await page.fill('#publish-description', 'خدمت نمونه برای بررسی مسیر معرفی، پیگیری و پاسخ مدیر سازمان.');
    await page.click('#publish-submit');
    await page.waitForFunction(() => document.getElementById('publish-message').classList.contains('is-success'));
    const saved = await storage(page);
    const publication = saved.value?.publications.find(item => item.name === name);
    assert.ok(publication, 'publication is saved in the current scope');
    assert.equal(publication.organizationId, organizationA);
    assert.equal(publication.status, 'pending');
    assert.equal(await page.locator('#publish-submit').isDisabled(), true);
    return publication;
}
async function tracking(page, kind, id, list = kind === 'publication' ? 'publication-list' : 'request-list') {
    await page.locator('#' + list + ' [data-request-id="' + id + '"][data-request-kind="' + kind + '"]').click();
    await page.locator('#request-detail-dialog[open]').waitFor();
    assert.equal(await page.locator('#request-detail-dialog').evaluate(node => node.contains(document.activeElement)), true);
}
async function noOverflow(page, label) {
    const geometry = await page.evaluate(() => ({ width: innerWidth, document: document.documentElement.scrollWidth, body: document.body.scrollWidth }));
    assert.ok(geometry.document <= geometry.width + 1 && geometry.body <= geometry.width + 1, label + ' overflows viewport: ' + JSON.stringify(geometry));
    assert.equal(await page.locator('html').getAttribute('dir'), 'rtl');
}
async function dialogKeyboard(page, opener) {
    for (const key of ['Tab', 'Tab', 'Tab', 'Tab', 'Tab', 'Tab', 'Shift+Tab', 'Shift+Tab', 'Shift+Tab']) {
        await page.keyboard.press(key);
        assert.equal(await page.locator('#service-request-dialog').evaluate(node => node.contains(document.activeElement)), true, 'modal focus remains contained');
    }
    await page.keyboard.press('Escape');
    await page.locator('#service-request-dialog').waitFor({ state: 'hidden' });
    await page.waitForFunction(serviceId => document.querySelector('#catalog-grid [data-offer-id="' + serviceId + '"]') === document.activeElement,
        await opener.getAttribute('data-offer-id'));
    assert.equal(await opener.evaluate(node => node === document.activeElement), true, 'Escape restores the offer opener');
}

(async () => {
    fs.mkdirSync(output, { recursive: true });
    browser = await chromium.launch({ executablePath: browserExecutable, headless: true, chromiumSandbox: true });
    browserVersion = await browser.version();
    const consumerContext = await newContext();
    const consumer = await consumerContext.newPage(); currentPage = consumer;
    let consumerRequest, consumerPublication, consumerSaved;
    const consumerReason = 'درخواست بررسی مرورگر برای خودکارسازی عملیات سازمان آفتاب';
    const consumerPublicationName = 'خدمت بررسی مرورگر آفتاب';

    await check('anonymous home and protected workspace use the synthetic cookie bootstrap', async () => {
        await consumer.goto(origin + '/');
        assert.equal(await consumer.locator('.account').count(), 0);
        assert.equal(await consumer.locator('.nav-login').isVisible(), true);
        const challenge = consumer.waitForResponse(response => new URL(response.url()).pathname === '/Workspace' && response.status() === 302);
        await workspace(consumer);
        assert.ok((await challenge).headers().location.includes('/preview/bootstrap/consumer'));
        assert.equal((await bootstrap(consumer)).mode, 'synthetic-preview');
        assert.equal((await bootstrap(consumer)).organizationId, organizationA);
        const cookie = (await consumerContext.cookies(origin)).find(item => item.name === 'fanasa.portal.synthetic-preview');
        assert.ok(cookie && cookie.httpOnly && cookie.sameSite === 'Lax');
        assert.equal(await consumer.locator('#workspace-mode').isVisible(), true);
    });

    await check('consumer sees two organizations and no admin controls or admin authorization', async () => {
        assert.equal(await consumer.locator('#workspace-tenant option').count(), 3);
        assert.equal(await consumer.locator('#tab-review').count(), 0);
        assert.equal(await consumer.locator('#panel-review').count(), 0);
        assert.equal((await bootstrap(consumer)).isAdmin, false);
        const denied = await consumer.request.get(origin + '/preview/admin-check');
        assert.equal(denied.status(), 403);
        await workspace(consumer);
        await screenshot(consumer, 'workspace-desktop-1440.png');
    });

    await check('catalog search, center filter and no-results state are functional', async () => {
        assert.equal(await consumer.locator('#catalog-grid .workspace-service-card').count(), 3);
        await consumer.fill('#catalog-search', 'اسناد');
        assert.equal(await consumer.locator('#catalog-grid .workspace-service-card').count(), 1);
        await consumer.fill('#catalog-search', '');
        await consumer.selectOption('#catalog-center', 'fanasa.bonyan');
        assert.equal(await consumer.locator('#catalog-grid .workspace-service-card').count(), 1);
        await consumer.fill('#catalog-search', 'no-such-service-2026');
        assert.equal(await consumer.locator('#catalog-grid .workspace-service-card').count(), 0);
        assert.equal(await consumer.locator('#catalog-empty').isVisible(), true);
        await consumer.fill('#catalog-search', ''); await consumer.selectOption('#catalog-center', '');
    });

    await check('plan details preserve a large exact decimal and native modal focus', async () => {
        const opener = await openOffer(consumer, 'managed-compute');
        await consumer.selectOption('#request-form-plan', 'capacity-review');
        assert.ok(canonicalText(await consumer.locator('#request-dialog-summary').textContent()).includes('9,007,199,254,740,993.0100'));
        assert.equal(await consumer.locator('#service-request-form').evaluate(node => node.checkValidity()), false);
        await screenshot(consumer, 'workspace-plan-exact-desktop.png');
        await dialogKeyboard(consumer, opener);
    });

    await check('new request has a timeline and survives reload without a backend write', async () => {
        consumerRequest = await createRequest(consumer, consumerReason);
        assert.equal(await consumer.locator('#request-list .workspace-request-card').count(), 4);
        await tracking(consumer, 'request', consumerRequest.id);
        assert.equal(await consumer.locator('#request-detail-body .workspace-timeline li').count(), 1);
        assert.ok((await consumer.locator('#request-detail-body').textContent()).includes(consumerReason));
        await screenshot(consumer, 'workspace-request-timeline.png');
        await consumer.keyboard.press('Escape');
        await consumer.reload(); await loaded(consumer);
        assert.equal(await consumer.locator('#request-list [data-request-id="' + consumerRequest.id + '"]').count(), 1);
        assert.equal((await storage(consumer)).value.requests.find(item => item.id === consumerRequest.id).reason, consumerReason);
        await consumer.selectOption('#request-status', 'pending');
        assert.equal(await consumer.locator('#request-list .workspace-request-card').count(), 2);
        await consumer.selectOption('#request-status', '');
    });

    await check('organization switching isolates requests and usage and restores organization A', async () => {
        await Promise.all([consumer.waitForURL(url => url.searchParams.get('tenantId') === organizationB), consumer.selectOption('#workspace-tenant', organizationB)]);
        await loaded(consumer);
        assert.equal(await consumer.locator('#workspace-tenant').inputValue(), organizationB);
        assert.equal(await consumer.locator('#request-list .workspace-request-card').count(), 3);
        assert.equal(await consumer.locator('#request-list [data-request-id="' + consumerRequest.id + '"]').count(), 0);
        assert.ok(canonicalText(await consumer.locator('#workspace-consumption-amount').textContent()).includes('2,160,000'));
        await workspace(consumer, organizationA, 'requests');
        assert.equal(await consumer.locator('#request-list [data-request-id="' + consumerRequest.id + '"]').count(), 1);
        assert.ok(canonicalText(await consumer.locator('#workspace-consumption-amount').textContent()).includes('4,080,000'));
    });

    await check('current usage amounts remain exact and unavailable previous period is explicit', async () => {
        await tab(consumer, 'usage');
        assert.equal(await consumer.locator('#usage-rows tr').count(), 2);
        const report = canonicalText(await consumer.locator('#usage-summary').textContent());
        assert.ok(report.includes('4,080,000') && report.includes('ریال'));
        const rows = canonicalText(await consumer.locator('#usage-rows').textContent());
        assert.ok(rows.includes('1,200,000') && rows.includes('2,880,000'));
        assert.equal(await consumer.locator('#usage-period option[value=previous]').isDisabled(), true);
        assert.ok((await consumer.locator('#usage-period option[value=previous]').textContent()).includes('در دسترس نیست'));
        assert.equal(await consumer.locator('#usage-period').inputValue(), 'current');
        await screenshot(consumer, 'workspace-usage-desktop.png');
    });

    await check('publication form creates one scoped pending history and persists after reload', async () => {
        await tab(consumer, 'publish');
        assert.equal(await consumer.locator('#publish-form').evaluate(node => node.checkValidity()), false);
        consumerPublication = await createPublication(consumer, consumerPublicationName);
        assert.equal(await consumer.locator('#publication-list .workspace-request-card').count(), 2);
        await tracking(consumer, 'publication', consumerPublication.id);
        assert.equal(await consumer.locator('#request-detail-body .workspace-timeline li').count(), 1);
        await consumer.keyboard.press('Escape');
        await consumer.reload(); await loaded(consumer);
        assert.equal(await consumer.locator('#publication-list [data-request-id="' + consumerPublication.id + '"]').count(), 1);
        consumerSaved = await storage(consumer);
        await screenshot(consumer, 'workspace-publication-desktop.png');
    });

    await check('RTL tab keyboard behavior and layouts at 1440, 390 and 320 pixels remain usable', async () => {
        for (const width of [1440, 390, 320]) {
            await consumer.setViewportSize({ width, height: width === 1440 ? 1050 : 844 });
            await tab(consumer, 'catalog'); await noOverflow(consumer, 'catalog ' + width);
            await consumer.locator('#tab-catalog').focus(); await consumer.keyboard.press('ArrowLeft');
            assert.equal(await consumer.locator('#tab-requests').getAttribute('aria-selected'), 'true');
            assert.equal(await consumer.locator('#tab-requests').evaluate(node => node === document.activeElement && node.tabIndex === 0), true);
            assert.equal(await consumer.locator('.workspace-tabs [aria-selected=true]').count(), 1);
            assert.equal(await consumer.locator('[data-workspace-panel]:visible').count(), 1);
            await consumer.keyboard.press('ArrowRight');
            assert.equal(await consumer.locator('#tab-catalog').getAttribute('aria-selected'), 'true');
            await consumer.keyboard.press('End');
            assert.equal(await consumer.locator('#tab-publish').getAttribute('aria-selected'), 'true');
            const controls = await consumer.locator('#publish-form input, #publish-form select, #publish-form textarea').evaluateAll(nodes => nodes.map(node => {
                const rectangle = node.getBoundingClientRect(); return { id: node.id, left: rectangle.left, right: rectangle.right, width: innerWidth, label: node.labels?.length > 0 };
            }));
            assert.ok(controls.every(control => control.label && control.left >= -1 && control.right <= control.width + 1), JSON.stringify(controls));
            assert.equal(await consumer.locator('#publish-form').evaluate(node => getComputedStyle(node).gridTemplateColumns.trim().split(/\s+/).length), width === 1440 ? 2 : 1);
            await noOverflow(consumer, 'publication ' + width);
            await consumer.keyboard.press('Home');
            assert.equal(await consumer.locator('#tab-catalog').getAttribute('aria-selected'), 'true');
            await screenshot(consumer, 'workspace-catalog-' + width + '.png');
            const opener = await openOffer(consumer, 'managed-compute');
            await consumer.selectOption('#request-form-plan', 'capacity-review');
            const dialog = await consumer.locator('#service-request-dialog').evaluate(node => {
                const rectangle = node.getBoundingClientRect(); return { left: rectangle.left, right: rectangle.right, top: rectangle.top, bottom: rectangle.bottom, width: innerWidth, height: innerHeight, scroll: node.scrollWidth, client: node.clientWidth };
            });
            assert.ok(dialog.left >= -1 && dialog.right <= dialog.width + 1 && dialog.top >= -1 && dialog.bottom <= dialog.height + 1 && dialog.scroll <= dialog.client + 1, JSON.stringify(dialog));
            assert.ok(canonicalText(await consumer.locator('#request-dialog-summary').textContent()).includes('9,007,199,254,740,993.0100'));
            await screenshot(consumer, 'workspace-plan-exact-' + width + '.png');
            await dialogKeyboard(consumer, opener);
            for (const panel of ['requests', 'services']) { await tab(consumer, panel); await noOverflow(consumer, panel + ' ' + width); }
            await tab(consumer, 'usage'); await noOverflow(consumer, 'usage ' + width);
            const table = await consumer.locator('#usage-table-container').evaluate(node => {
                const rectangle = node.getBoundingClientRect(); return { left: rectangle.left, right: rectangle.right, width: innerWidth, overflow: getComputedStyle(node).overflowX, scroll: node.scrollWidth, client: node.clientWidth };
            });
            assert.ok(table.left >= -1 && table.right <= table.width + 1 && table.overflow === 'auto', JSON.stringify(table));
            if (width < 700) assert.ok(table.scroll > table.client, 'mobile usage table scrolls inside its own container');
        }
        await consumer.setViewportSize({ width: 1440, height: 1050 });
    });

    const adminContext = await newContext();
    const admin = await adminContext.newPage(); currentPage = admin;
    await check('explicit admin cookie has an isolated scope and can approve or reject own sample items', async () => {
        await signIn(admin, 'admin');
        assert.equal((await bootstrap(admin)).isAdmin, true);
        assert.equal(await admin.locator('#tab-review').isVisible(), true);
        assert.equal(await admin.locator('#request-list [data-request-id="' + consumerRequest.id + '"]').count(), 0);
        assert.notEqual((await bootstrap(admin)).subjectKey, (await bootstrap(consumer)).subjectKey);
        const authorized = await admin.goto(origin + '/preview/admin-check'); assert.equal(authorized.status(), 200);
        await workspace(admin);
        const ownRequest = await createRequest(admin, 'درخواست نمونه مدیر برای بررسی پاسخ و فعال‌سازی نمایشی');
        const ownPublication = await createPublication(admin, 'عرضهٔ نمونه مدیر مرورگر');
        await tab(admin, 'review');
        async function review(kind, id, decision) {
            await admin.locator('#review-list [data-review-id="' + id + '"][data-review-kind="' + kind + '"][data-review-action="' + decision + '"]').click();
            const saved = await storage(admin), items = kind === 'publication' ? saved.value.publications : saved.value.requests;
            assert.equal(items.find(item => item.id === id).status, decision);
            assert.equal(await admin.locator('#service-workspace').evaluate(node => node.contains(document.activeElement)), true, 'review retains keyboard focus in workspace');
        }
        await review('request', ownRequest.id, 'approved');
        await review('request', 'request-compute-aftab', 'rejected');
        await review('publication', 'publication-aftab', 'approved');
        await review('publication', ownPublication.id, 'rejected');
        await admin.selectOption('#review-status', '');
        await tracking(admin, 'request', ownRequest.id, 'review-list');
        assert.equal(await admin.locator('#request-detail-body .workspace-timeline li').count(), 2);
        assert.equal(await admin.locator('#request-detail-body a').count(), 0, 'tracking approval exposes no launch link');
        await admin.keyboard.press('Escape');
        await screenshot(admin, 'workspace-admin-review.png');
        await tab(admin, 'services');
        assert.equal(await admin.locator('#active-services .workspace-service-card').filter({ hasText: 'خودکارسازی خدمات' }).count(), 1);
        assert.equal(await admin.locator('#active-services a').count(), 0, 'sample approval exposes no launch grant');
        await admin.reload(); await loaded(admin);
        assert.equal((await storage(admin)).value.requests.find(item => item.id === ownRequest.id).status, 'approved');
        await tab(consumer, 'requests');
        assert.equal((await storage(consumer)).value.requests.find(item => item.id === consumerRequest.id).status, 'pending');
    });

    const deniedContext = await newContext();
    const denied = await deniedContext.newPage(); currentPage = denied;
    await check('denied subject and tampered tenant expose no stale scoped data or enabled mutations', async () => {
        await signIn(denied, 'denied');
        assert.equal((await bootstrap(denied)).catalogStatus, 'forbidden');
        assert.equal((await bootstrap(denied)).organizations.length, 0);
        for (const id of ['catalog-grid', 'request-list', 'publication-list', 'active-services', 'usage-rows'])
            assert.equal(await denied.locator('#' + id).evaluate(node => node.children.length), 0);
        assert.equal(await denied.locator('#publish-submit').isDisabled(), true);
        assert.equal(await denied.locator('#tab-review').count(), 0);
        assert.equal(await denied.locator('#workspace-services-count').textContent(), '—');
        assert.equal(await denied.locator('#workspace-requests-count').textContent(), '—');
        assert.equal(await denied.locator('#workspace-publish-count').textContent(), '—');
        await screenshot(denied, 'workspace-denied.png');
        await workspace(consumer, 'ffffffff-ffff-4fff-8fff-ffffffffffff'); currentPage = consumer;
        assert.equal((await bootstrap(consumer)).organizationId, null);
        for (const id of ['catalog-grid', 'request-list', 'publication-list', 'active-services', 'usage-rows'])
            assert.equal(await consumer.locator('#' + id).evaluate(node => node.children.length), 0);
        assert.equal(await consumer.locator('#publish-submit').isDisabled(), true);
        assert.equal(await consumer.locator('#workspace-consumption-amount').textContent(), '—');
        assert.equal(await consumer.locator('#workspace-services-count').textContent(), '—');
        assert.equal(await consumer.locator('#workspace-tenant').inputValue(), '');
        await screenshot(consumer, 'workspace-tampered-tenant.png');
        await workspace(consumer);
        assert.equal(await consumer.locator('#request-list [data-request-id="' + consumerRequest.id + '"]').count(), 1);
    });

    await check('connected bootstrap UI simulation ignores saved preview drafts and disables mutations', async () => {
        const connectedContext = await newContext(html => html.replace(/(<script id="workspace-bootstrap" type="application\/json">)([\s\S]*?)(<\/script>)/, (_, before, json, after) => {
            const data = JSON.parse(json);
            data.mode = 'connected'; data.isAdmin = false; data.catalog = []; data.requests = []; data.publications = [];
            data.usage = { status: 'notConnected', periodLabel: '', asOf: '', currency: 'IRR', totalAmount: '', items: [] };
            return before + JSON.stringify(data).replace(/</g, '\\u003c') + after;
        }));
        await connectedContext.addInitScript(saved => {
            if (location.origin === saved.origin) sessionStorage.setItem(saved.key, saved.raw);
        }, { ...consumerSaved, origin });
        const connected = await connectedContext.newPage(); currentPage = connected;
        await signIn(connected, 'consumer');
        // A login redirect is not intercepted again, and navigating to the same
        // fragment can stay in the document. Reload guarantees the override runs.
        await connected.reload(); await loaded(connected);
        assert.equal((await bootstrap(connected)).mode, 'connected');
        assert.equal(await connected.locator('#catalog-grid .workspace-service-card').count(), 0);
        assert.equal(await connected.locator('#request-list .workspace-request-card').count(), 0);
        assert.equal(await connected.locator('#publication-list .workspace-request-card').count(), 0);
        assert.equal(await connected.locator('#publish-submit').isDisabled(), true);
        assert.equal(await connected.locator('#request-dialog-submit').isDisabled(), true);
        assert.equal(await connected.locator('#usage-rows tr').count(), 0);
        assert.equal(await connected.locator('#workspace-consumption-amount').textContent(), '—');
        assert.equal(await connected.locator('#workspace-requests-count').textContent(), '—');
        assert.equal(await connected.locator('#workspace-publish-count').textContent(), '—');
        assert.equal(await connected.locator('#usage-period').isDisabled(), true);
        await connected.locator('#publish-form').dispatchEvent('submit');
        await connected.locator('#service-request-form').dispatchEvent('submit');
        assert.equal((await storage(connected)).raw, consumerSaved.raw);
        await tab(connected, 'services');
        assert.equal(await connected.locator('#active-services a').count(), 2);
        assert.ok((await connected.locator('#active-services a').evaluateAll(nodes => nodes.map(node => node.href))).every(href => new URL(href).origin === origin));
        await screenshot(connected, 'workspace-connected-bootstrap-simulation.png');
        await connectedContext.close();
    });

    await check('failed session persistence warns visibly and keeps current changes until reload', async () => {
        const failingContext = await newContext();
        await failingContext.addInitScript(() => {
            window.__fanasaFailSessionWrites = false;
            const original = Storage.prototype.setItem;
            Storage.prototype.setItem = function (key, value) {
                if (this === window.sessionStorage && window.__fanasaFailSessionWrites)
                    throw new DOMException('Synthetic session persistence failure', 'QuotaExceededError');
                return original.call(this, key, value);
            };
        });
        const failing = await failingContext.newPage(); currentPage = failing;
        await signIn(failing, 'consumer');
        await failing.evaluate(() => { window.__fanasaFailSessionWrites = true; });
        const reason = 'درخواست نمونه در زمان شکست حفظ نشست مرورگر';
        await openOffer(failing, 'service-automation');
        await failing.fill('#request-form-reason', reason);
        await failing.click('#request-dialog-submit');
        await failing.locator('#service-request-dialog').waitFor({ state: 'hidden' });
        assert.equal(await failing.locator('#request-list .workspace-request-card').filter({ hasText: reason }).count(), 1);
        assert.equal((await storage(failing)).raw, null);
        await failing.waitForFunction(() => {
            const text = document.getElementById('workspace-message').textContent;
            return text.includes('مرورگر') && /حفظ|ذخیره|محفوظ/.test(text);
        });
        assert.equal(await failing.locator('#workspace-message').isVisible(), true);
        await screenshot(failing, 'workspace-storage-warning.png');
        await failing.reload(); await loaded(failing);
        assert.equal(await failing.locator('#request-list .workspace-request-card').filter({ hasText: reason }).count(), 0);
        assert.equal(await failing.locator('#request-list .workspace-request-card').count(), 3);
        await failingContext.close();
    });

    await check('all browser traffic stayed on the preview origin without backend mutations or page errors', async () => {
        assert.deepEqual(blockedRequests, []);
        assert.deepEqual(mutationRequests, []);
        assert.deepEqual(pageErrors, []);
    });
})().catch(async error => {
    failure = error.stack || String(error); console.error(failure); process.exitCode = 1;
    if (currentPage && !currentPage.isClosed()) try { await screenshot(currentPage, 'workspace-failure.png'); } catch { /* Preserve the original failure. */ }
}).finally(async () => {
    fs.mkdirSync(output, { recursive: true });
    fs.writeFileSync(path.join(output, 'workspace-browser-checks.json'), JSON.stringify({
        verifiedAtUtc: new Date().toISOString(), browser: browserVersion, origin,
        scope: 'Disposable synthetic cookie preview in isolated headless contexts. The connected case overrides only the bootstrap JSON on the synthetic host; it does not verify production authentication, central request APIs, accounting or grants.',
        passed: !failure, checks: results, screenshots, blockedRequests, mutationRequests, pageErrors, failure: failure || null
    }, null, 2));
    if (browser) await browser.close();
});
