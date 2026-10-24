const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const vm=require('node:vm');
const root=path.join(__dirname,'../src/Fanasa.AccessManagement.Web');

test('navigation retains every route, filters admin links, and marks exact active page',()=>{
  const layout=fs.readFileSync(path.join(root,'Pages/Shared/_Layout.cshtml'),'utf8');
  const routes=['Index','Centers','Products','Architecture','Platform','Tenants','Users','Roles','Organization','OrganizationHr','Tenancy','Governance','Billing','Accounting','UsageAccounting','Audit'];
  for(const route of routes)assert.ok(layout.includes(`Page: "/${route}"`),route);
  assert.match(layout,/Where\(link => !link.Admin \|\| admin\)/);
  assert.match(layout,/link.Page == currentPage \? "page" : null/);
  for(const route of ['OrganizationHr','UsageAccounting'])assert.match(layout,new RegExp(`Page: "/${route}", Label: "[^"]+", Admin: true`));
});
test('mobile disclosure initially closes, desktop opens, resize restores accessibility',()=>{
  for(const mobile of [true,false]){
    const menu={open:true};let change;
    const media={matches:mobile,addEventListener(event,listener){assert.equal(event,'change');change=listener}};
    vm.runInNewContext(fs.readFileSync(path.join(root,'wwwroot/js/platform-navigation.js'),'utf8'),{document:{querySelector:()=>menu},window:{matchMedia:()=>media}});
    assert.equal(menu.open,!mobile);
    media.matches=!mobile;change();assert.equal(menu.open,mobile);
  }
});
test('architecture exposes dated delivery boundaries and the real developer roadmap route',()=>{
  const page=fs.readFileSync(path.join(root,'Pages/Architecture.cshtml'),'utf8');
  for(const phrase of ['۲۳ اکتبر ۲۰۲۶','CI/CD خودکار هنوز پذیرش نهایی نشده','localhost فقط محیط موقت','نه پایش زنده','مدیریت قراردادها با GitLab CI','NeoAgA با Jenkins','view=roadmap'])assert.ok(page.includes(phrase),phrase);
  for(const gate of ['G1','G2','G3','G4'])assert.ok(page.includes(gate));
});
