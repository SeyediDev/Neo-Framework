// Disposable Organization.Tests --preview only. No production requests or writes.
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const {chromium}=require(process.env.FANASA_UI_PLAYWRIGHT_MODULE||'playwright');
const origin='http://127.0.0.1:5198';
const output=path.resolve(process.argv[2]||'../../../../NeoCluster/.build/roadmap-20261023');
(async()=>{
  fs.mkdirSync(output,{recursive:true});
  const browser=await chromium.launch({channel:'chrome',headless:true});
  const context=await browser.newContext({viewport:{width:1440,height:1000},serviceWorkers:'block'});
  const blocked=[],errors=[],checks=[];
  await context.route('**/*',route=>{
    const req=route.request();if(new URL(req.url()).origin===origin&&req.method()==='GET')return route.continue();
    blocked.push(req.method()+' '+new URL(req.url()).origin);return route.abort();
  });
  const page=await context.newPage();page.on('pageerror',error=>errors.push(error.message));
  const check=async(name,action)=>{await action();checks.push(name);console.log('PASS '+name)};
  try{
    await page.goto(origin+'/Architecture');
    await check('active route opens its group and roadmap uses canonical internal URL',async()=>{
      await page.locator('.arch-delivery-status').waitFor();
      const active=page.locator('.platform-nav a[aria-current="page"]');assert.equal(await active.count(),1);assert.equal(await active.getAttribute('href'),'/Architecture');
      assert.equal(await page.locator('.platform-nav-group[open]').count(),1);
      assert.equal(await page.locator('.arch-delivery-status a').getAttribute('href'),'https://developer.fanasa.net.local/?workspace=studio&view=roadmap');
      assert.match(await page.locator('.arch-delivery-status').textContent(),/localhost فقط محیط موقت/);
    });
    await check('desktop menu groups keep all sixteen original routes and keyboard selection works',async()=>{
      assert.equal(await page.locator('.platform-nav a').count(),16);
      assert.equal(await page.locator('.platform-nav-group').count(),5);
      const products=page.locator('.platform-nav-group').filter({has:page.locator('summary').filter({hasText:'محصول و عرضه'})});
      await products.locator('summary').focus();await page.keyboard.press('Enter');
      await page.getByRole('link',{name:'کاتالوگ محصولات',exact:true}).click();
      assert.equal(new URL(page.url()).pathname,'/Products');
      assert.equal(await page.locator('.platform-nav a[aria-current="page"]').getAttribute('href'),'/Products');
      await page.goto(origin+'/Architecture');
    });
    await check('desktop and mobile layout, native compact navigation, assets and no overflow',async()=>{
      for(const [label,width,height] of [['desktop',1440,1000],['mobile',390,844],['narrow',320,740]]){
        await page.setViewportSize({width,height});await page.evaluate(()=>document.fonts.ready);
        if(width<850){
          assert.equal(await page.locator('.platform-menu').getAttribute('open'),null);
          await page.locator('.platform-menu>summary').click();
          assert.equal(await page.locator('.platform-menu').getAttribute('open'),'');
          await page.screenshot({path:path.join(output,`platform-menu-${label}.png`)});
          await page.locator('.platform-menu>summary').click();
        }
        const layout=await page.evaluate(()=>({width:document.documentElement.clientWidth,scroll:document.documentElement.scrollWidth,assets:[...document.querySelectorAll('.brand img')].every(img=>img.complete&&img.naturalWidth>0)}));
        assert.ok(layout.scroll<=layout.width,JSON.stringify(layout));assert.equal(layout.assets,true);
        await page.locator('.arch-delivery-status').scrollIntoViewIfNeeded();
        await page.screenshot({path:path.join(output,`platform-architecture-${label}.png`)});
      }
    });
    assert.deepEqual(blocked,[]);assert.deepEqual(errors,[]);
    fs.writeFileSync(path.join(output,'platform-browser-results.json'),JSON.stringify({fixtureOnly:true,operationalAcceptance:false,checks,blocked,errors},null,2));
  }finally{await browser.close()}
})().catch(error=>{console.error(error);process.exitCode=1});
