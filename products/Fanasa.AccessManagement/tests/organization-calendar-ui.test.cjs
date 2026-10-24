const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const vm=require('node:vm');

// Exercise the actual calendar/template page handlers; policy decisions remain server data.
class Element{
 constructor(tag='div'){this.tag=tag;this.children=[];this._text='';this.value='';this.hidden=false;this.disabled=false;this.checked=false;this.attrs={};this.listeners={};}
 set textContent(v){this._text=String(v);this.children=[];}
 get textContent(){return this._text+this.children.map(x=>x.textContent).join(' ');}
 append(...items){for(const item of items){item.parent=this;this.children.push(item);}if(this.tag==='select'&&!this.value)this.value=this.children[0]?.value||'';}
 replaceChildren(...items){this.children=[];this._text='';this.append(...items);}
 get options(){return this.children;}
 setAttribute(k,v){this.attrs[k]=v;}
 addEventListener(k,fn){this.listeners[k]=fn;}
 querySelector(selector){return this.querySelectorAll(selector)[0];}
 querySelectorAll(selector){return this.children.flatMap(c=>[...((selector==='[type=submit]'&&c.type==='submit'||selector==='button'&&c.tag==='button')?[c]:[]),...c.querySelectorAll(selector)]);}
 remove(){this.parent.children=this.parent.children.filter(x=>x!==this);}
 focus(){} scrollIntoView(){}
}
function option(text,value){const e=new Element('option');e.textContent=text;e.value=value;return e;}
function form(ids,id,controls){const f=new Element('form');f.elements={};for(const [name,tag,value] of controls){const c=new Element(tag);c.value=value||'';c.name=name;f.elements[name]=c;f.append(c);}const button=new Element('button');button.type='submit';f.append(button);ids.set(id,f);return f;}
const source=fs.readFileSync(path.join(__dirname,'../src/Fanasa.AccessManagement.Web/wwwroot/js/organization.js'),'utf8');
const calendarSource=source.slice(source.indexOf("\n(() => {\n 'use strict';\n const root=document.getElementById('organization-app');if(!root)return;"));
const templateSource=source.slice(source.indexOf("\n(() => {\n 'use strict';\n const root=document.getElementById('organization-template');if(!root)return;"));
const fixture=()=>({tenantId:'tenant-a',revision:7,calendars:[{id:'office',code:'office',name:'دفتر مرکزی',kind:'work',timeZoneId:'Asia/Tehran',coverageFrom:'2026-01-01',coverageThrough:'2026-12-31',weeklyWindows:[{dayOfWeek:6,startMinute:480,endMinute:960}],exceptions:[{date:'2026-10-20',kind:'closed',windows:[],reason:'تعطیلی آموزشی'}],active:true,source:'مصوبهٔ نمونه'},{id:'data',code:'data',name:'پنجره داده',kind:'data',timeZoneId:'UTC',coverageFrom:'2026-01-01',coverageThrough:'2026-12-31',weeklyWindows:[],exceptions:[],active:true,source:'مرجع داده'}]});
async function setup({write=true,template=false,delay}={}){
 const ids=new Map(),root=new Element();root.dataset={tenant:'tenant-a',template:String(template)};root.querySelector=()=>({value:'csrf-token'});ids.set('organization-app',root);
 for(const id of ['org-calendar-message','org-calendar-revision','org-calendar-list','org-calendar-editor','org-weekly-windows','org-date-exceptions','org-calendar-result','org-calendar-clock','org-weekly-add','org-weekly-example','org-exception-add','org-calendar-new','org-refresh'])ids.set(id,new Element());
 if(write){const f=form(ids,'org-calendar-form',[['id','input'],['code','input','new-calendar'],['name','input','تقویم جدید'],['kind','select','work'],['timeZoneId','input','Asia/Tehran'],['coverageFrom','input','2026-01-01'],['coverageThrough','input','2026-12-31'],['source','input','مصوبه'],['reason','input','تغییر آزمایشی'],['active','input']]);f.elements.active.checked=true;}
 const probe=form(ids,'org-calendar-evaluate',[['calendarId','select'],['mode','select','working-time'],['at','input','2026-10-16T12:00']]);probe.elements.mode.append(option('داخل ساعت','working-time'),option('روز کاری','non-holiday'));
 const state=fixture(),calls=[];const fetch=async(url,options)=>{const body=options?JSON.parse(options.body):null;calls.push({url,body,options});return {ok:true,status:200,headers:{get:()=> 'application/json'},json:async()=>{if(url.endsWith('/evaluate')){if(delay)await delay;return {known:true,allowed:false,reason:'holiday',calendarId:body.calendarId,revision:7,timeZoneId:'Asia/Tehran',localTime:'2026-10-16T15:30:00+03:30',isWorkingDay:false};}return url==='/api/organization/template'?{calendars:structuredClone(state)}:structuredClone(state);}};};
 vm.runInNewContext(calendarSource,{document:{getElementById:id=>ids.get(id),createElement:tag=>new Element(tag)},fetch,Option:option,Date,Intl,JSON,Error,Number,String,encodeURIComponent});await new Promise(r=>setImmediate(r));assert.match(ids.get('org-calendar-message').textContent,/خوانده شدند/);return {ids,calls,submit:async id=>ids.get(id).onsubmit({preventDefault(){}})};
}
test('calendar read shows persisted source, coverage and exceptions without granting access',async()=>{
 const {ids}=await setup({write:false});assert.match(ids.get('org-calendar-list').textContent,/Asia\/Tehran/);assert.match(ids.get('org-calendar-list').textContent,/2026-12-31/);assert.match(ids.get('org-calendar-list').textContent,/تعطیلی آموزشی/);assert.equal(ids.get('org-calendar-list').querySelectorAll('button').length,0);assert.match(ids.get('org-calendar-clock').textContent,/وقت دستگاه/);
});
test('calendar save sends exact revision, CSRF, weekly minutes and closed exception without stale windows',async()=>{
 const {ids,calls,submit}=await setup();ids.get('org-weekly-example').onclick();ids.get('org-exception-add').onclick();const row=ids.get('org-date-exceptions').children[0];row.controls.date.value='2026-10-20';row.controls.reason.value='تعطیلی مصوب';row.controls.windows.value='08:00-12:00';await submit('org-calendar-form');const command=calls.find(x=>x.options?.method==='POST');assert.equal(command.body.expectedRevision,7);assert.equal(command.options.headers['X-CSRF-TOKEN'],'csrf-token');assert.equal(command.body.weeklyWindows.length,5);assert.deepEqual(command.body.weeklyWindows[0],{dayOfWeek:6,startMinute:480,endMinute:960});assert.deepEqual(command.body.exceptions[0],{date:'2026-10-20',kind:'closed',windows:[],reason:'تعطیلی مصوب'});assert.equal(command.body.active,true);
});
test('calendar editor preserves midnight endpoint and rejects equal or malformed windows',async()=>{
 const {ids,calls,submit}=await setup();ids.get('org-weekly-add').onclick();const row=ids.get('org-weekly-windows').children[0];row.controls.start.value='16:00';row.controls.end.value='16:00';await submit('org-calendar-form');assert.equal(calls.some(x=>x.body),false);assert.match(ids.get('org-calendar-message').textContent,/یکسان/);row.controls.end.value='24:00';await submit('org-calendar-form');assert.equal(calls.find(x=>x.body).body.weeklyWindows[0].endMinute,1440);
});
test('preview uses server result, keeps calendar permission separate and data windows disable day-only mode',async()=>{
 const {ids,calls,submit}=await setup({write:false});const probe=ids.get('org-calendar-evaluate');probe.elements.calendarId.value='data';probe.elements.mode.value='non-holiday';probe.onchange();assert.equal(probe.elements.mode.value,'working-time');assert.equal(probe.elements.mode.options[1].disabled,true);await submit('org-calendar-evaluate');const call=calls.find(x=>x.url.endsWith('/evaluate'));assert.equal(call.url,'/api/organization/tenants/tenant-a/calendars/data/evaluate');assert.equal(call.body.mode,'working-time');assert.match(call.body.at,/Z$/);assert.match(ids.get('org-calendar-result').textContent,/زمان بسته/);assert.match(ids.get('org-calendar-result').textContent,/مجوز فرد و سیاست دسترسی جداگانه/);
});
test('changed preview input discards a late result',async()=>{
 let release;const delay=new Promise(r=>release=r),{ids,submit}=await setup({delay});const probe=ids.get('org-calendar-evaluate');probe.elements.calendarId.value='office';const pending=submit('org-calendar-evaluate');await new Promise(r=>setImmediate(r));probe.oninput();release();await pending;assert.doesNotMatch(ids.get('org-calendar-result').textContent,/زمان بسته/);assert.match(ids.get('org-calendar-result').textContent,/بررسی تازه/);
});
test('template calendar snapshot uses dedicated read endpoint and no edit controls',async()=>{const {calls,ids}=await setup({write:false,template:true});assert.equal(calls[0].url,'/api/organization/template');assert.equal(ids.get('org-calendar-list').querySelectorAll('button').length,0);});

async function templateSetup({exists=true}={}){
 const ids=new Map(),root=new Element();root.querySelector=()=>({value:'template-csrf'});ids.set('organization-template',root);for(const id of ['org-template-message','org-template-links','org-template-notes','org-template-lessons','org-template-run','org-template-results'])ids.set(id,new Element());form(ids,'org-template-create',[['reason','input','ایجاد نمونه آموزشی']]);
 const view={marker:{tenantId:'template-id'},tenant:{id:'template-id'},policies:{patterns:[{}]},calendars:{calendars:[{}]},lessons:[{key:'calendar',title:'ساعت کاری',explanation:'تقویم در سازمان تعریف می‌شود.',configurationArea:'Organization'}],notes:['تمام داده‌ها آموزشی‌اند.']},calls=[];
 const fetch=async(url,options)=>{calls.push({url,options,body:options?JSON.parse(options.body):null});const missing=!exists&&!options&&url==='/api/organization/template';return {status:missing?404:200,ok:!missing,headers:{get:()=> 'application/json'},json:async()=>url.endsWith('/scenarios')?{passed:0,total:1,results:[{scenario:{title:'خارج ساعت',expectedDecision:'Deny',lesson:'calendar',at:'2026-10-16T18:00:00+03:30',subject:'synthetic',permission:'organization.read'},actual:{decision:'Permit',reason:'changed-fixture'},passed:false}]}:view};};
 vm.runInNewContext(templateSource,{document:{getElementById:id=>ids.get(id),createElement:tag=>new Element(tag)},fetch,JSON,Error,encodeURIComponent});await new Promise(r=>setImmediate(r));return {ids,calls};
}
test('template creation is explicit, antiforgery protected and links target exact marked tenant',async()=>{const {ids,calls}=await templateSetup({exists:false});assert.equal(ids.get('org-template-create').hidden,false);assert.equal(calls.some(x=>x.body),false);await ids.get('org-template-create').onsubmit({preventDefault(){}});const call=calls.find(x=>x.body);assert.equal(call.options.headers['X-CSRF-TOKEN'],'template-csrf');assert.equal(call.body.reason,'ایجاد نمونه آموزشی');assert.equal(ids.get('org-template-links').children[0].href,'/Organization?tenant=template-id');});
test('template scenario mismatch displays actual server decision and keeps draft simulation explicit',async()=>{const {ids}=await templateSetup();await ids.get('org-template-run').onclick();const text=ids.get('org-template-results').textContent;assert.match(text,/0 از 1/);assert.match(text,/نیازمند بررسی/);assert.match(text,/نتیجهٔ موتور: Permit/);assert.match(text,/changed-fixture/);assert.match(text,/تقویم در سازمان تعریف می‌شود/);assert.match(text,/هیچ انتشار سیاست یا عملیات واقعی/);});
