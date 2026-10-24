const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
// Reuse the established DOM/API harness, then supply the new PIP form controls.
const harness=fs.readFileSync(path.join(__dirname,'governance-ui.test.cjs'),'utf8').split("\ntest('catalog exposes")[0];
const setupBase=new Function('require','__dirname',harness+'\nreturn setup;')(require,__dirname);
async function setup(options){const value=await setupBase(options);const form=value.ids.get('pattern-form');if(form)for(const [name,tag]of [['pipSourceId','select'],['attributeName','select'],['attributeValue','input']]){const e=new form.constructor(tag);e.name=name;form.elements[name]=e;form.append(e);}return value;}

test('PIP policy selects tenant source and typed attribute, requires fresh evidence and clears role assertions',async()=>{
 const {ids,state,calls,submit}=await setup();state.pipSources=[{id:'hr-source',name:'منابع انسانی',enabled:true,maxAgeSeconds:300,definitions:[{name:'employment.active',dataType:'boolean'}]}];await ids.get('governance-refresh').onclick();const form=ids.get('pattern-form');form.elements.template.value='attribute';form.elements.roleId.value='role-a';form.elements.unitId.value='unit-a';form.elements.pipSourceId.value='hr-source';form.onchange();form.elements.attributeName.value='employment.active';form.elements.attributeValue.value='true';form.onchange();assert.equal(form.elements.roleId.disabled,true);assert.equal(form.elements.unitId.disabled,true);assert.equal(form.elements.attributeName.required,true);assert.match(ids.get('pattern-preview').textContent,/employment.active/);await submit('pattern-form');const call=calls.find(x=>x.url.endsWith('/patterns'));assert.equal(call.body.pipSourceId,'hr-source');assert.equal(call.body.attributeName,'employment.active');assert.equal(call.body.attributeValue,'true');assert.equal(call.body.requireFreshPip,true);assert.equal(call.body.roleId,null);assert.equal(call.body.unitId,null);assert.equal(call.options.headers['X-CSRF-TOKEN'],'csrf-token');
});
test('PIP fields do not leak into another template and incomplete PIP drafts are rejected',async()=>{
 const {ids,calls,submit}=await setup(),form=ids.get('pattern-form');form.elements.template.value='attribute';form.onchange();await submit('pattern-form');assert.equal(calls.some(x=>x.url.endsWith('/patterns')),false);assert.match(ids.get('governance-message').textContent,/منبع، ویژگی/);form.elements.template.value='role';form.elements.roleId.value='role-a';form.elements.pipSourceId.value='stale-source';form.elements.attributeValue.value='false';form.onchange();await submit('pattern-form');const call=calls.find(x=>x.url.endsWith('/patterns'));assert.equal(call.body.pipSourceId,undefined);assert.equal(call.body.attributeValue,undefined);assert.equal(form.elements.pipSourceId.disabled,true);
});
test('PIP decision shows evidence freshness without rendering attribute values',async()=>{
 const {ids,submit}=await setup({decision:{decision:'Indeterminate',reason:'stale-or-missing-pip',policies:[],obligations:[],advice:[],pip:{status:'stale',error:'snapshot-expired',snapshot:{sourceVersion:'hr-42',observedAt:'2026-10-23T08:00:00Z',freshUntil:'2026-10-23T08:05:00Z',attributes:{privateSalary:'do-not-display'}}}}});await submit('decision-form');const text=ids.get('access-decision').textContent;assert.match(text,/منقضی/);assert.match(text,/پایان اعتبار/);assert.match(text,/hr-42/);assert.doesNotMatch(text,/do-not-display/);assert.match(text,/enforced = false/);
});
