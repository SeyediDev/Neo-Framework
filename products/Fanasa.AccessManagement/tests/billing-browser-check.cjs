'use strict';
// Actual compiled Accounting Razor page, isolated loopback Kestrel and synthetic ledger only.
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),{spawn}=require('node:child_process');
const {chromium}=require(process.env.FANASA_UI_PLAYWRIGHT_MODULE || 'C:/Users/JavadSeyediPC/.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright');
const exact=require('../src/Fanasa.AccessManagement.Web/wwwroot/js/billing-exact.js');
const root=path.resolve(__dirname,'../../..'),output=path.resolve(process.argv[2] || path.join(root,'.build/payg-billing-20261024/browser-accounting'));
let server,browser;const checks=[],errors=[],requests=[];
async function check(name,action){await action();checks.push({name,passed:true});console.log('PASS '+name);}
(async()=>{
 fs.mkdirSync(output,{recursive:true});
 server=spawn('dotnet',[path.join(__dirname,'Billing.Api.Tests/bin/Release/net10.0/Billing.Api.Tests.dll'),'--preview'],{cwd:root,windowsHide:true,stdio:['ignore','pipe','pipe']});
 const origin=await new Promise((resolve,reject)=>{
  let text='';const timeout=setTimeout(()=>reject(Error('preview startup timeout: '+text.slice(-2000))),180000);
  server.stdout.on('data',data=>{text+=data;const found=text.match(/BILLING_PREVIEW_URL=(http:\/\/127\.0\.0\.1:\d+\/)/);if(found){clearTimeout(timeout);resolve(found[1].replace(/\/$/,''));}});
  server.stderr.on('data',data=>{text+=data;});
  server.on('exit',code=>{clearTimeout(timeout);reject(Error('preview exited '+code+': '+text.slice(-2000)));});
 });
 browser=await chromium.launch({headless:true,executablePath:process.env.FANASA_UI_BROWSER_EXECUTABLE || 'C:/Program Files/Google/Chrome/Application/chrome.exe'});
 const context=await browser.newContext({viewport:{width:1440,height:1000},reducedMotion:'reduce'});
 await context.route('**/*',route=>new URL(route.request().url()).origin===origin?route.continue():route.abort());
 const page=await context.newPage();page.on('pageerror',error=>errors.push(error.message));
 await context.request.get(origin+'/synthetic-login');
 await page.goto(origin+'/Accounting');
 await page.waitForFunction(()=>document.getElementById('billing-message')?.textContent.includes('حساب به‌روز'));
 const tenant=await page.locator('#accounting-app').getAttribute('data-tenant'),endpoint='/api/accounting/tenants/'+tenant;
 await check('actual Razor page renders the exact large amount',async()=>{
  assert.ok((await page.locator('#billing-metrics').textContent()).includes('۹٬۰۰۷٬۱۹۹٬۲۵۴٬۷۴۰٬۹۹۳'));
  assert.equal(await page.locator('#billing-command').count(),1);
 });
 for(const width of [1440,390,320])await check('RTL layout remains within '+width+' pixels',async()=>{
  await page.setViewportSize({width,height:1000});await page.waitForTimeout(100);
  assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth<=innerWidth+1),true);
  assert.equal(await page.locator('.billing-money-number').evaluateAll(rows=>rows.every(row=>{
   const range=document.createRange();range.selectNodeContents(row);
   return range.getClientRects().length===1 && row.scrollWidth<=row.clientWidth+1;
  })),true,'exact metric amounts stay complete on one line');
  await page.screenshot({path:path.join(output,'accounting-'+width+'.png'),fullPage:true,animations:'disabled'});
 });
 await page.setViewportSize({width:1440,height:1000});
 const state=exact.parse(await (await page.request.get(origin+endpoint)).text()),charge=state.account.entries.find(e=>e.kind==='usage'),invoice=state.account.invoices[0];
 await check('frozen invoice details retain the original historical amount',async()=>{
  await page.locator('#billing-invoices summary').click();
  assert.ok((await page.locator('#billing-invoices').textContent()).includes('۹٬۰۰۷٬۱۹۹٬۲۵۴٬۷۴۰٬۹۹۳'));
 });
 let abortOnce=true;
 await page.route('**/api/accounting/v1/tenants/*/credit-notes',async route=>{
  requests.push(route.request().postData());const response=await route.fetch();
  if(abortOnce){abortOnce=false;assert.equal(response.status(),200);await route.abort('failed');}
  else await route.fulfill({response});
 });
 await page.locator('#billing-command [name=operation]').selectOption('credit-note');
 await page.locator('#billing-command [name=chargeId]').selectOption(charge.id);
 await page.locator('#billing-command [name=invoiceId]').selectOption(invoice.id);
 await page.locator('#billing-command [name=amount]').fill('1');
 await page.locator('#billing-command [name=reason]').fill('اصلاح مصنوعی آزمون مرورگر');
 await page.locator('#billing-command [name=reference]').fill('Synthetic loopback UI evidence');
 await page.locator('#billing-command [type=submit]').click();
 await page.waitForFunction(()=>document.getElementById('billing-message')?.textContent.includes('نتیجهٔ ثبت نامشخص'));
 await check('unknown outcome preserves and freezes the submitted request',async()=>{
  assert.equal(requests.length,1);assert.equal(await page.locator('#billing-command [name=operation]').isDisabled(),true);
 });
 await page.reload();await page.waitForFunction(()=>document.getElementById('billing-message')?.textContent.includes('نتیجهٔ ثبت قبلی نامشخص'));
 await page.locator('#billing-command [type=submit]').click();
 await page.waitForFunction(()=>document.getElementById('billing-message')?.textContent==='ثبت شد.');
 await check('reload and retry reuse one identity and create one credit note',async()=>{
  assert.equal(requests.length,2);assert.equal(requests[0],requests[1]);
  const current=exact.parse(await (await page.request.get(origin+endpoint)).text());
  assert.equal(current.account.entries.filter(e=>e.kind==='credit-note').length,1);
  assert.equal(current.account.invoices[0].contentHash,invoice.contentHash);
  assert.equal(current.account.invoices[0].amount,invoice.amount);
 });
 await check('no browser script errors',async()=>assert.deepEqual(errors,[]));
 fs.writeFileSync(path.join(output,'results.json'),JSON.stringify({synthetic:true,origin:'loopback',checks,errors,screenshots:[1440,390,320].map(width=>'accounting-'+width+'.png')},null,2));
 console.log(checks.length+' browser checks passed');
})().catch(error=>{console.error(error);process.exitCode=1;}).finally(async()=>{if(browser)await browser.close();if(server)server.kill();});
