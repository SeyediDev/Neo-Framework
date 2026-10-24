/* Local browser verification; requires Node 22+ and an installed Chrome/Edge. */
'use strict';
const fs = require('node:fs');
const path = require('node:path');
const http = require('node:http');
const { spawn } = require('node:child_process');
const root = __dirname;
const workspace = path.resolve(root, '../../../../../..');
const evidence = path.join(root, 'evidence');
const checks = [], errors = [], externalRequests = [], failedRequests = [];
const expectedHttpErrors = [];
let expectedHttpStatus;
const delay = ms => new Promise(resolve => setTimeout(resolve, ms));
let server, browser, cdp;

class CDP {
  constructor(url) {
    this.socket = new WebSocket(url);
    this.nextId = 1;
    this.pending = new Map();
    this.listeners = new Map();
    this.ready = new Promise((resolve, reject) => {
      this.socket.addEventListener('open', resolve, { once: true });
      this.socket.addEventListener('error', reject, { once: true });
    });
    this.socket.addEventListener('message', event => {
      const message = JSON.parse(event.data);
      if (message.id) {
        const item = this.pending.get(message.id);
        if (!item) return;
        clearTimeout(item.timer);
        this.pending.delete(message.id);
        message.error ? item.reject(new Error(JSON.stringify(message.error))) : item.resolve(message.result);
      } else {
        for (const handler of this.listeners.get(message.method) || []) handler(message.params);
      }
    });
  }
  on(name, handler) {
    this.listeners.set(name, [...(this.listeners.get(name) || []), handler]);
  }
  async send(method, params = {}) {
    await this.ready;
    const id = this.nextId++;
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => { this.pending.delete(id); reject(new Error(`CDP timeout: ${method}`)); }, method === 'Browser.close' ? 2000 : 15000);
      this.pending.set(id, { resolve, reject, timer });
      this.socket.send(JSON.stringify({ id, method, params }));
    });
  }
  close() { this.socket.close(); }
}

async function evaluate(expression) {
  const result = await cdp.send('Runtime.evaluate', { expression, awaitPromise: true, returnByValue: true });
  if (result.exceptionDetails) throw new Error(result.exceptionDetails.text + ': ' + JSON.stringify(result.exceptionDetails.exception));
  return result.result.value;
}
async function until(expression, timeout = 8000) {
  const end = Date.now() + timeout;
  while (Date.now() < end) {
    if (await evaluate(expression)) return;
    await delay(80);
  }
  throw new Error(`Condition timeout: ${expression}`);
}
async function check(name, expression) {
  const value = await evaluate(expression);
  checks.push({ name, passed: !!value });
  if (!value) throw new Error(`Check failed: ${name}`);
}
async function click(selector) {
  await evaluate(`(() => { const el = document.querySelector(${JSON.stringify(selector)}); if (!el) throw new Error('Missing element'); el.scrollIntoView({block:'center',behavior:'instant'}); })()`);
  await delay(250);
  const box = await evaluate(`(() => { const el = document.querySelector(${JSON.stringify(selector)}); const r = el.getBoundingClientRect(); return {x:r.x+r.width/2,y:r.y+r.height/2}; })()`);
  await cdp.send('Input.dispatchMouseEvent', { type: 'mousePressed', x: box.x, y: box.y, button: 'left', clickCount: 1 });
  await cdp.send('Input.dispatchMouseEvent', { type: 'mouseReleased', x: box.x, y: box.y, button: 'left', clickCount: 1 });
  await delay(250);
}
async function value(selector, value, event = 'change') {
  await evaluate(`(() => { const el=document.querySelector(${JSON.stringify(selector)}); el.value=${JSON.stringify(value)}; el.dispatchEvent(new Event(${JSON.stringify(event)},{bubbles:true})); })()`);
}
async function key(key, code = key) {
  await cdp.send('Input.dispatchKeyEvent', { type: 'keyDown', key, code, windowsVirtualKeyCode: key === 'Escape' ? 27 : key === 'Tab' ? 9 : undefined });
  await cdp.send('Input.dispatchKeyEvent', { type: 'keyUp', key, code });
  await delay(80);
}
async function screenshot(name) {
  await evaluate('document.fonts.ready');
  await delay(150);
  const image = await cdp.send('Page.captureScreenshot', { format: 'png', captureBeyondViewport: false });
  fs.writeFileSync(path.join(evidence, name + '.png'), Buffer.from(image.data, 'base64'));
}

async function start() {
  fs.mkdirSync(evidence, { recursive: true });
  const mime = { '.html': 'text/html; charset=utf-8', '.css': 'text/css; charset=utf-8', '.js': 'text/javascript; charset=utf-8', '.svg': 'image/svg+xml', '.woff2': 'font/woff2' };
  server = http.createServer((request, response) => {
    if (request.url === '/api/workbench' || request.url.startsWith('/api/workbench/')) {
      const proxy = http.request({hostname:'127.0.0.1',port:4187,path:request.url,method:request.method,headers:{...request.headers,origin:'http://127.0.0.1:4186'}},reply=>{response.writeHead(reply.statusCode,reply.headers);reply.pipe(response);});
      proxy.on('error',()=>response.writeHead(503).end('{}'));request.pipe(proxy);return;
    }
    let pathname;
    try { pathname = decodeURIComponent(new URL(request.url, 'http://localhost').pathname); }
    catch { response.writeHead(400).end(); return; }
    const filename = path.resolve(root, '.' + (pathname === '/' ? '/index.html' : pathname));
    if (!filename.startsWith(root + path.sep)) { response.writeHead(403).end(); return; }
    try {
      const data = fs.readFileSync(filename);
      response.writeHead(200, { 'Content-Type': mime[path.extname(filename)] || 'application/octet-stream', 'Cache-Control': 'no-store' }).end(data);
    } catch { response.writeHead(404).end(); }
  });
  await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
  const url = `http://127.0.0.1:${server.address().port}/`;
  const executable = [process.env.CONTRACT_UI_BROWSER, 'C:/Program Files/Google/Chrome/Application/chrome.exe', 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe'].find(file => file && fs.existsSync(file));
  if (!executable) throw new Error('Chrome or Edge is required. Set CONTRACT_UI_BROWSER to its executable path.');
  const profile = path.join(workspace, '.build', 'contract-policy-ui-qa', 'profile-' + Date.now());
  fs.mkdirSync(profile, { recursive: true });
  browser = spawn(executable, ['--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check', '--disable-background-networking', '--disable-component-update', '--disable-sync', '--metrics-recording-only', '--remote-debugging-port=0', '--remote-debugging-address=127.0.0.1', '--user-data-dir=' + profile, 'about:blank'], { windowsHide: true, stdio: ['ignore', 'ignore', 'pipe'] });
  let launchError;
  browser.once('error', error => { launchError = error; });
  browser.stderr.on('data', () => {});
  const activePort = path.join(profile, 'DevToolsActivePort');
  let activePortData = '';
  for (let i = 0; i < 150; i++) {
    if (launchError) throw launchError;
    if (browser.exitCode !== null) throw new Error('Browser exited before DevTools started');
    try { activePortData = fs.readFileSync(activePort, 'utf8'); } catch (error) {
      if (!['ENOENT', 'EBUSY', 'EACCES'].includes(error.code)) throw error;
    }
    if (/^\d+\r?\n\/devtools\//.test(activePortData)) break;
    await delay(100);
  }
  if (!/^\d+\r?\n\/devtools\//.test(activePortData)) throw new Error('DevTools did not start');
  const port = activePortData.split('\n')[0].trim();
  const targets = await (await fetch(`http://127.0.0.1:${port}/json/list`)).json();
  cdp = new CDP(targets.find(target => target.type === 'page').webSocketDebuggerUrl);
  await cdp.ready;
  cdp.on('Runtime.exceptionThrown', event => errors.push(event.exceptionDetails.text));
  cdp.on('Log.entryAdded', event => {
    if (event.entry.level !== 'error') return;
    if (expectedHttpStatus && event.entry.text.includes(`status of ${expectedHttpStatus}`)) expectedHttpErrors.push(event.entry.text);
    else errors.push(event.entry.text);
  });
  cdp.on('Network.requestWillBeSent', event => {
    if (!event.request.url.startsWith(url) && !/^(data:|blob:|about:)/.test(event.request.url)) externalRequests.push(event.request.url);
  });
  cdp.on('Network.loadingFailed', event => failedRequests.push({ error: event.errorText, type: event.type }));
  await cdp.send('Runtime.enable');
  await cdp.send('Log.enable');
  await cdp.send('Network.enable');
  await cdp.send('Page.enable');
  const exportPath = path.join(evidence, 'fanasa-contract-policy-demo.json');
  if (fs.existsSync(exportPath)) fs.unlinkSync(exportPath);
  await cdp.send('Browser.setDownloadBehavior', { behavior: 'allow', downloadPath: evidence });
  await cdp.send('Emulation.setDeviceMetricsOverride', { width: 1440, height: 1100, deviceScaleFactor: 1, mobile: false });
  await cdp.send('Page.navigate', { url: url + (process.argv.includes('--workbench') ? '#workbench' : '#contracts') });
  await until("document.querySelectorAll('#contractRows tr').length === 6");
  await evaluate('document.fonts.ready');
  return { browserVersion: (await cdp.send('Browser.getVersion')).product, viewport: '1440 × 1100 / 390 × 844' };
}

async function verify() {
  await check('Persian RTL and six contracts', "document.documentElement.lang==='fa' && document.documentElement.dir==='rtl' && document.querySelectorAll('#contractRows tr').length===6");
  await check('Local font and official logo load', "document.fonts.check('16px Vazirmatn') && [...document.images].every(img=>img.complete && img.naturalWidth>0)");
  await check('Desktop has no page overflow', 'document.documentElement.scrollWidth <= innerWidth');
  await screenshot('desktop-contracts');
  await value('#search', 'سپهر', 'input');
  await check('Search matches tenant and contract names', "document.querySelectorAll('#contractRows tr').length===2");
  await value('#tenantFilter', 'گروه آذر');
  await check('Combined filters show an empty state', "!document.querySelector('#emptyState').hidden && document.querySelector('.table-wrap').hidden");
  await click('#clearFilters');
  await check('Clear filters restores six records', "document.querySelectorAll('#contractRows tr').length===6 && document.querySelector('#search').value===''");
  await click('[data-status="Suspended"]');
  await check('Lifecycle filter selects suspended contract', "document.querySelectorAll('#contractRows tr').length===1 && document.querySelector('#contractRows').textContent.includes('معلق')");
  await click('[data-status="all"]');
  await value('#gatewayFilter', 'pending');
  await check('Gateway observation is a separate filter', "document.querySelectorAll('#contractRows tr').length===2 && [...document.querySelectorAll('#contractRows tr')].every(row=>row.textContent.includes('در انتظار مشاهده'))");
  await value('#gatewayFilter', 'all');
  await click('.contract-link[data-open-contract="FCP-0102"]');
  await check('Drawer opens with focus and tenant details', "document.querySelector('#contractDrawer').open && document.activeElement.id==='closeDrawer' && document.querySelector('#drawerCode').textContent.includes('صنایع سپهر')");
  await click('#tab-rights');
  await key('ArrowLeft');
  await check('RTL tab keyboard changes tab and panel', "document.activeElement.id==='tab-limits' && document.querySelector('#drawerBody').getAttribute('aria-labelledby')==='tab-limits'");
  await check('Limits are explicitly not remaining counters', "document.querySelector('#drawerBody').textContent.includes('موجودی باقی‌مانده نیست')");
  await key('End');
  await check('End key selects history tab', "document.activeElement.id==='tab-history'");
  await key('Home');
  await check('Home key restores rights tab', "document.activeElement.id==='tab-rights'");
  await screenshot('desktop-drawer');
  await click('#tab-policy');
  await click('[data-policy-action="publish"]');
  await check('Publication dialog focuses the reason', "document.querySelector('#policyDialog').open && document.activeElement.id==='policyReason'");
  await click('#policyForm button[type="submit"]');
  await check('Reason is required before publication', "document.querySelector('#policyDialog').open && !document.querySelector('#policyReason').checkValidity()");
  await value('#policyReason', 'بازبینی آزمایشی <img src=x onerror=alert(1)>', 'input');
  await click('#simulateConflict');
  await click('#policyForm button[type="submit"]');
  await check('Revision conflict does not publish policy', "document.querySelector('#policyDialog').open && !document.querySelector('#policyError').hidden && document.querySelector('#policyError').textContent.includes('سیاست اعمال نشد') && document.querySelector('#drawerBody').textContent.includes('سیاست ۲')");
  await screenshot('desktop-revision-conflict');
  await click('#policyForm button[type="submit"]');
  await check('Retry applies local publication and escapes reason', "!document.querySelector('#policyDialog').open && document.querySelector('#drawerBody').textContent.includes('سیاست ۳') && document.querySelector('#drawerBody').textContent.includes('<img src=x') && !document.querySelector('#drawerBody img')");
  await click('#closeDrawer');
  await check('Drawer restores focus to its row button', "!document.querySelector('#contractDrawer').open && document.activeElement.dataset.openContract==='FCP-0102'");
  await click('#exportDemo');
  const exportPath = path.join(evidence, 'fanasa-contract-policy-demo.json');
  for (let i = 0; i < 100 && !fs.existsSync(exportPath); i++) await delay(80);
  const exported = JSON.parse(fs.readFileSync(exportPath, 'utf8'));
  const contract = exported.contracts.find(item => item.id === 'FCP-0102');
  const independent = exported.isDemo === true && contract.policyVersion === 3 && contract.acceptedPolicyVersion === 2 && contract.managementRevision === 7 && contract.configRevision === 3 && contract.contractVersion === 4 && contract.gateway === 'pending';
  checks.push({ name: 'Actual sample download preserves independent revisions and pending observation', passed: independent });
  if (!independent) throw new Error('Downloaded state failed independent revision check');
  await click('.nav-item[data-view="decision"]');
  await value('#scenarioContract', 'FCP-0102');
  await check('New policy without a matching accepted contract binding is Indeterminate', "document.querySelector('#decisionResult').dataset.decision==='Indeterminate' && document.querySelector('#decisionResult').textContent.includes('مرجع سیاست')");
  await click('.nav-item[data-view="contracts"]');
  await click('.contract-link[data-open-contract="FCP-0102"]');
  await click('#tab-policy');
  await click('[data-policy-action="retire"]');
  await value('#policyReason', 'بازنشستگی آزمایشی برای بررسی تصمیم', 'input');
  await click('#policyForm button[type="submit"]');
  await check('Local retirement changes policy status', "!document.querySelector('#policyDialog').open && document.querySelector('#drawerBody .section-title').textContent.includes('بازنشسته')");
  await click('#inspectDecision');
  await check('Retired policy produces Deny without executing', "!document.querySelector('#contractDrawer').open && !document.querySelector('#view-decision').hidden && document.querySelector('#decisionResult').dataset.decision==='Deny'");
  await click('#resetDemo');
  await check('Reset returns initial sample and view', "!document.querySelector('#view-contracts').hidden && document.querySelectorAll('#contractRows tr').length===6 && document.querySelector('#scenarioContract').value==='FCP-0101'");
  await click('.nav-item[data-view="decision"]');
  await check('Permit lists obligations as still needed for execution', "document.querySelector('#decisionResult').dataset.decision==='Permit' && document.querySelector('.obligation-box').textContent.includes('اجرا یا تکمیل نشده‌اند')");
  await screenshot('desktop-decision');
  await value('#scenarioPreset', 'no-grant');
  await check('Role without direct grant is Deny', "document.querySelector('#decisionResult').dataset.decision==='Deny' && document.querySelector('#decisionResult').textContent.includes('مجوز مستقیم')");
  await value('#scenarioPreset', 'no-role');
  await check('Contract without assigned role is Deny', "document.querySelector('#decisionResult').dataset.decision==='Deny' && document.querySelector('#decisionResult').textContent.includes('نقش را خودکار ایجاد نمی‌کند')");
  await value('#scenarioPreset', 'missing');
  await check('Missing fresh evidence is Indeterminate', "document.querySelector('#decisionResult').dataset.decision==='Indeterminate'");
  await value('#scenarioPreset', 'outside');
  await check('Product outside target is NotApplicable', "document.querySelector('#decisionResult').dataset.decision==='NotApplicable' && document.querySelector('#decisionResult').textContent.includes('محصول هدف')");
  await value('#scenarioPreset', 'permit');
  await click('#conditionMember');
  await check('Inactive membership is Deny', "document.querySelector('#decisionResult').dataset.decision==='Deny'");
  await click('#conditionMember');
  await value('#scenarioContract', 'FCP-0103');
  await check('Suspended contract is Deny', "document.querySelector('#decisionResult').dataset.decision==='Deny' && document.querySelector('#decisionResult').textContent.includes('معلق')");
  await value('#scenarioContract', 'FCP-0105');
  await check('Expired window is Deny while stored lifecycle stays Active', "document.querySelector('#decisionResult').dataset.decision==='Deny' && document.querySelector('#decisionResult').textContent.includes('همچنان فعال') && document.querySelector('#scenarioContract option:checked').textContent.includes('فعال')");
  await value('#scenarioContract', 'FCP-0106');
  await check('Future window is Deny while stored lifecycle stays Active', "document.querySelector('#decisionResult').dataset.decision==='Deny' && document.querySelector('#decisionResult').textContent.includes('آغاز نشده')");
  await value('#scenarioContract', 'FCP-0102');
  await check('Pending Gateway observation remains separate from Permit obligations', "document.querySelector('#decisionResult').dataset.decision==='Permit' && document.querySelector('.decision-footnote').textContent.includes('در انتظار مشاهده') && document.querySelector('.obligation-box').textContent.includes('پذیرش تخصیص')");
  await click('#evaluateScenario');
  await click('.nav-item[data-view="history"]');
  await check('Evaluation creates only a labeled local event', "document.querySelector('#historyList').firstElementChild.textContent.includes('بدون اجرا یا مصرف')");
  await click('.nav-item[data-view="architecture"]');
  await click('[data-node="backend"]');
  await check('Architecture node explains resource tenant boundary', "document.querySelector('[data-node=backend]').getAttribute('aria-pressed')==='true' && document.querySelector('#architectureDetail').textContent.includes('سازمان مالک منبع')");
  await screenshot('desktop-architecture');
  await cdp.send('Emulation.setDeviceMetricsOverride', { width: 390, height: 844, deviceScaleFactor: 1, mobile: false });
  await evaluate("window.scrollTo({top:0,behavior:'instant'})");
  await delay(250);
  await check('Mobile architecture has no page overflow', 'document.documentElement.scrollWidth <= innerWidth');
  await click('#mobileMenu');
  await check('Mobile menu opens and moves focus inside navigation', "document.querySelector('#mobileMenu').getAttribute('aria-expanded')==='true' && document.querySelector('#sidebar').contains(document.activeElement)");
  await key('Escape');
  await check('Escape closes mobile menu and restores focus', "document.querySelector('#mobileBackdrop').hidden && document.activeElement.id==='mobileMenu'");
  await click('#mobileMenu');
  await click('.nav-item[data-view="contracts"]');
  await check('Mobile navigation selects view and closes menu', "!document.querySelector('#view-contracts').hidden && document.querySelector('#mobileBackdrop').hidden");
  await screenshot('mobile-contracts');
  await check('Mobile contracts have no page overflow', 'document.documentElement.scrollWidth <= innerWidth');
  await check('Closed mobile sidebar is removed from keyboard navigation', "getComputedStyle(document.querySelector('#sidebar')).display==='none'");
  await evaluate("document.querySelector('.contracts-panel').scrollIntoView({block:'start',behavior:'instant'})");
  await screenshot('mobile-contracts-table');
  await click('.contract-link[data-open-contract="FCP-0102"]');
  await check('Mobile drawer fits viewport', "document.querySelector('#contractDrawer').getBoundingClientRect().width<=innerWidth && document.querySelector('#contractDrawer').getBoundingClientRect().height<=innerHeight");
  await screenshot('mobile-drawer');
  await key('Escape');
  await check('Escape closes drawer and restores focus on mobile', "!document.querySelector('#contractDrawer').open && document.activeElement.dataset.openContract==='FCP-0102'");
  await click('#mobileMenu');
  await click('.nav-item[data-view="decision"]');
  await check('Mobile decision view has no page overflow', 'document.documentElement.scrollWidth <= innerWidth');
  await screenshot('mobile-decision');
  await check('Local workbench scope remains visible on every view', "getComputedStyle(document.querySelector('.demo-notice')).display!=='none' && document.querySelector('.demo-notice').textContent.includes('محیط آزمایش')");
  await check('No browser persistence is created', 'localStorage.length===0 && sessionStorage.length===0 && document.cookie.length===0');
  await cdp.send('Page.reload', { ignoreCache: true });
  await until("document.querySelectorAll('#contractRows tr').length===6");
  await check('Reload preserves navigation but resets sample state', "!document.querySelector('#view-decision').hidden && document.querySelector('#decisionResult').dataset.decision==='Permit' && document.querySelector('#scenarioContract').value==='FCP-0101'");
}

(async () => {
  const started = new Date().toISOString();
  let metadata, failure;
  try {
    metadata = await start();
    if (process.argv.includes('--layout-only')) {
      await cdp.send('Emulation.setDeviceMetricsOverride', { width: 390, height: 844, deviceScaleFactor: 1, mobile: false });
      await delay(250);
      await check('Mobile contracts have no page overflow', 'document.documentElement.scrollWidth <= innerWidth');
    } else if (process.argv.includes('--workbench')) await require('./qa-workbench.cjs')({check,click,value,evaluate,until,screenshot,cdp,delay,expectHttpStatus:status=>{expectedHttpStatus=status;}});
    else await verify();
  }
  catch (error) {
    failure = error.stack; process.exitCode = 1;
    if (cdp) {
      try {
        await screenshot('last-failure');
        console.log('Failure context: ' + JSON.stringify(await evaluate("({focus:{tag:document.activeElement?.tagName,id:document.activeElement?.id}, menuExpanded:document.querySelector('#mobileMenu')?.getAttribute('aria-expanded'), sidebar:document.querySelector('#sidebar')?.className, scrollY, viewport:innerWidth,scrollWidth:document.documentElement.scrollWidth,overflow:[...document.querySelectorAll('body *')].filter(el=>!el.closest('table') && (el.getBoundingClientRect().right>innerWidth+1 || el.getBoundingClientRect().left < -1) && getComputedStyle(el).display!=='none').slice(0,20).map(el=>({tag:el.tagName,class:el.className,width:el.getBoundingClientRect().width,right:el.getBoundingClientRect().right,left:el.getBoundingClientRect().left}))})")));
      } catch {}
    }
  }
  finally {
    const result = { started, completed: new Date().toISOString(), ...metadata, passed: !failure && errors.length===0 && externalRequests.length===0 && failedRequests.length===0, checks, errors, expectedHttpErrors, externalRequests, failedRequests, failure: failure || null };
    const reportName = process.argv.includes('--workbench') ? 'qa-workbench-results.json' : 'qa-results.json';
    fs.writeFileSync(path.join(evidence, reportName), JSON.stringify(result, null, 2) + '\n');
    console.log(JSON.stringify({ passed: result.passed, browserVersion: result.browserVersion, checksPassed: checks.filter(check=>check.passed).length, checksTotal: checks.length, errors, externalRequests, failedRequests, failure: result.failure, report: path.join(evidence, reportName) }, null, 2));
    if (!result.passed) process.exitCode = 1;
    if (cdp) { try { await cdp.send('Browser.close'); } catch {} cdp.close(); }
    if (browser && browser.exitCode === null) browser.kill();
    if (browser) { browser.stderr.destroy(); browser.unref(); }
    if (server) { server.closeAllConnections(); await new Promise(resolve => server.close(resolve)); }
  }
})();
