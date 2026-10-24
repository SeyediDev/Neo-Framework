const {test}=require('node:test');
const assert=require('node:assert/strict');
const vm=require('node:vm');
const fs=require('node:fs');
const path=require('node:path');

// Drive the page's actual events and API contract without reimplementing its evaluator.
class Element {
 constructor(tag='div'){this.tag=tag;this.children=[];this._text='';this.value='';this.hidden=false;this.disabled=false;this.attrs={};}
 set textContent(value){this._text=String(value);this.children=[];}
 get textContent(){return this._text+this.children.map(x=>x.textContent).join(' ');}
 append(...items){this.children.push(...items);if(this.tag==='select'&&this.value===''&&this.children.length===items.length)this.value=items[0]?.value||'';}
 replaceChildren(...items){this._text='';this.children=[];this.append(...items);}
 get options(){return this.children;}
 setAttribute(k,v){this.attrs[k]=v;}
 closest(){return this.label||(this.label=new Element('label'));}
 querySelector(selector){return this.querySelectorAll(selector)[0];}
 querySelectorAll(selector){const out=[];for(const child of this.children){if(selector==='button'||selector==='[type=submit]'){if(child.tag==='button'&&(selector!=='[type=submit]'||child.type==='submit'))out.push(child);}out.push(...child.querySelectorAll(selector));}return out;}
 reportValidity(){return true;}
 focus(){this.focused=true;}
 scrollIntoView(){}
}
const fixture=()=>({policies:{revision:12,algorithm:'deny-overrides',patterns:[
 {id:'version-missing',name:'مرجع حذف‌شده',template:'role',permission:'organization.write',effect:'Deny',roleId:'gone-role',unitId:'gone-unit',status:'published',createdBy:'author',publishedBy:'reviewer',reason:'منع',startsAt:null,endsAt:null,utcStartHour:null,utcEndHour:null},
 {id:'version-draft',name:'اجازه ویرایش',template:'role',permission:'organization.write',effect:'Permit',roleId:'role-a',unitId:null,status:'draft',createdBy:'author',publishedBy:null,reason:'بررسی',startsAt:null,endsAt:null,utcStartHour:null,utcEndHour:null},
 {id:'version-own',name:'پیش‌نویس خودم',template:'member-window',permission:'organization.read',effect:'Permit',roleId:null,unitId:null,status:'draft',createdBy:'reviewer',publishedBy:null,reason:'آزمایش',startsAt:'2026-10-10T00:00:00Z',endsAt:null,utcStartHour:null,utcEndHour:null}
 ]},requests:[],members:[{keycloakSubject:'member',displayName:'عضو آزمایش',isActive:true}],chart:{revision:9,roles:[{id:'role-a',name:'مدیر واحد'}],units:[{id:'unit-a',name:'فناوری',active:true}],positions:[{id:'position-a',name:'مسئول فناوری',roleId:'role-a',unitId:'unit-a',active:true}],appointments:[{id:'appointment-a',subject:'member',positionId:'position-a',from:'2026-10-01T00:00:00Z',to:null}]},calendars:{revision:3,calendars:[{id:'office',name:'دفتر',active:true,kind:'work'},{id:'data-window',name:'پنجره داده',active:true,kind:'data'}]},liveEnforcementAvailable:false,liveEnforcedPermissions:['organization.read','organization.write'],permissions:['organization.read','organization.write']});
async function setup({admin=true,template=false,decision,simulationDelay}={}){
 const ids=new Map(),root=new Element();root.dataset={tenant:'tenant-a',subject:'reviewer',admin:String(admin),review:'true',template:String(template)};root.querySelector=()=>({value:'csrf-token'});ids.set('governance-app',root);
 for(const id of ['governance-message','governance-refresh','lifecycle-requests','access-patterns','access-decision','policy-state','attribute-summary','attribute-freshness','governance-members','request-context','pattern-preview','pattern-copy-origin','policy-status','policy-search','policy-count'])ids.set(id,new Element(id==='policy-status'?'select':'div'));
 ids.get('policy-status').value='all';
 function form(id,controls){const f=new Element('form');f.elements={};for(const [name,type,value]of controls){const c=new Element(type);c.name=name;c.value=value||'';f.elements[name]=c;f.append(c);}const button=new Element('button');button.type='submit';f.append(button);ids.set(id,f);return f;}
 form('lifecycle-form',[['operation','select','join'],['subject','input'],['displayName','input'],['positionId','select'],['previousAppointmentId','select'],['effectiveAt','input','2026-10-12T12:00'],['endsAt','input'],['reason','input']]);
 if(admin&&!template)form('pattern-form',[['name','input'],['template','select','role'],['permission','select'],['effect','select','Permit'],['roleId','select'],['unitId','select'],['startsAt','input'],['endsAt','input'],['utcStartHour','input'],['utcEndHour','input'],['calendarId','select'],['calendarMode','select','working-time'],['enforce','input'],['reason','input']]);
 form('decision-form',[['subject','input','member'],['permission','select'],['at','input','2026-10-12T12:00'],['includeDrafts','input']]);
 const state=fixture(),calls=[];
 const document={getElementById:id=>ids.get(id),createElement:tag=>new Element(tag)};
 const fetch=async(url,options)=>{calls.push({url,options,body:options?JSON.parse(options.body):undefined});return {ok:true,headers:{get:()=> 'application/json'},json:async()=>{if(url.endsWith('/simulate')){if(simulationDelay)await simulationDelay;return {simulation:true,enforced:false,result:decision||{decision:'Permit',reason:'published-policy',policies:['version-draft'],obligations:['audit','external-obligation'],advice:['external-advice']}};}return structuredClone(state);}};};
 const Option=function(text,value){const e=new Element('option');e.textContent=text;e.value=value;return e;};
 const FormData=function(form){this.get=name=>form.elements[name]?.disabled?null:form.elements[name]?.value??null;};
 vm.runInNewContext(fs.readFileSync(path.join(__dirname,'../src/Fanasa.AccessManagement.Web/wwwroot/js/governance.js'),'utf8'),{document,fetch,Option,FormData,crypto:{randomUUID:()=> 'request-id'},Date,JSON,Error,Number,String});
 await new Promise(resolve=>setImmediate(resolve));
 assert.match(ids.get('governance-message').textContent,/خوانده شد/,'initial page must render, not silently fail');
 return {ids,state,calls,root,submit:async id=>ids.get(id).onsubmit({preventDefault(){}})};
}
test('catalog exposes missing references, revision and independent publisher; filters remain functional',async()=>{
 const {ids}=await setup();
 assert.match(ids.get('policy-state').textContent,/12/);assert.match(ids.get('policy-state').textContent,/deny-overrides/);
 const catalog=ids.get('access-patterns');assert.match(catalog.textContent,/مرجع یافت نشد · gone-role/);assert.match(catalog.textContent,/مرجع یافت نشد · gone-unit/);
 const own=catalog.children.find(x=>x.textContent.includes('پیش‌نویس خودم'));
 assert.match(own.textContent,/مدیر دیگری/);assert.equal(own.querySelectorAll('button').some(x=>x.textContent==='انتشار توسط مدیر دوم'),false);
 ids.get('policy-status').value='published';ids.get('policy-status').onchange();assert.equal(catalog.children.length,1);assert.match(catalog.textContent,/version-missing/);
 ids.get('policy-search').value='unknown';ids.get('policy-search').oninput();assert.match(catalog.textContent,/فیلتر/);
});
test('copy creates a new draft command, leaves published content intact, and preserves CSRF/revision',async()=>{
 const {ids,state,calls,submit}=await setup(),catalog=ids.get('access-patterns');
 const draft=catalog.children.find(x=>x.textContent.includes('اجازه ویرایش'));
 draft.querySelectorAll('button').find(x=>x.textContent.includes('ساخت پیش‌نویس')).onclick();
 const form=ids.get('pattern-form');assert.equal(form.elements.roleId.value,'role-a');assert.match(ids.get('pattern-copy-origin').textContent,/version-draft/);assert.equal(form.elements.reason.value,'');
 form.elements.reason.value='اصلاح مستقل';await submit('pattern-form');
 const request=calls.find(x=>x.url.endsWith('/patterns'));assert.equal(request.body.expectedRevision,12);assert.equal(request.body.id,undefined);assert.equal(request.body.roleId,'role-a');assert.equal(request.options.headers['X-CSRF-TOKEN'],'csrf-token');assert.equal(state.policies.patterns[1].name,'اجازه ویرایش');
});
test('publishing uses expected revision and exact policy ID with independent reviewer',async()=>{
 const {ids,calls}=await setup(),card=ids.get('access-patterns').children.find(x=>x.textContent.includes('اجازه ویرایش'));
 const footer=card.children.find(x=>x.tag==='footer');footer.children.find(x=>x.tag==='input').value='بررسی مستقل';await footer.querySelectorAll('button').find(x=>x.textContent==='انتشار توسط مدیر دوم').onclick();
 const request=calls.find(x=>x.url.endsWith('/patterns/version-draft'));assert.equal(request.body.operation,'publish');assert.equal(request.body.expectedRevision,12);assert.equal(request.body.reason,'بررسی مستقل');
});
test('member-window prevents condition-free drafts and never sends caller role assertions',async()=>{
 const {ids,calls,submit}=await setup(),form=ids.get('pattern-form');form.elements.template.value='member-window';form.elements.template.onchange();assert.equal(form.elements.roleId.disabled,true);
 await submit('pattern-form');assert.match(ids.get('governance-message').textContent,/شرط زمانی/);assert.equal(calls.some(x=>x.url.endsWith('/patterns')),false);
 form.elements.startsAt.value='2026-10-12T12:00';await submit('pattern-form');const request=calls.find(x=>x.url.endsWith('/patterns'));assert.equal(request.body.roleId,null);assert.match(request.body.startsAt,/Z$/);
});
test('server result keeps obligations separate from advice and Permit separate from enforcement; changing input invalidates it',async()=>{
 const {ids,calls,submit}=await setup();await submit('decision-form');
 const request=calls.find(x=>x.url.endsWith('/simulate'));assert.deepEqual(Object.keys(request.body).sort(),['at','permission','subject']);
 const result=ids.get('access-decision');assert.match(result.textContent,/Permit/);assert.match(result.textContent,/enforced = false/);assert.match(result.textContent,/external-obligation/);assert.match(result.textContent,/external-advice/);assert.match(result.textContent,/اجباری/);assert.match(result.textContent,/اختیاری/);assert.match(result.textContent,/version-draft/);
 ids.get('decision-form').elements.permission.value='organization.write';ids.get('decision-form').onchange();assert.doesNotMatch(result.textContent,/Permit/);assert.match(result.textContent,/دوباره ارزیابی/);
});
test('server Indeterminate reason is shown without inventing evaluation traces or usable permission',async()=>{
 const {ids,submit}=await setup({admin:false,decision:{decision:'Indeterminate',reason:'missing-required-attribute',policies:[],obligations:[],advice:['review-policy-references']}});await submit('decision-form');
 const result=ids.get('access-decision').textContent;assert.match(result,/Indeterminate/);assert.match(result,/missing-required-attribute/);assert.match(result,/review-policy-references/);assert.match(result,/enforced = false/);assert.match(result,/مرجع گم‌شده/);assert.doesNotMatch(result,/موفقیت عملیات/);
});
test('late response cannot present a previous request as the changed request decision',async()=>{
 let release;const simulationDelay=new Promise(resolve=>release=resolve),{ids,submit}=await setup({simulationDelay});
 const pending=submit('decision-form');await new Promise(resolve=>setImmediate(resolve));
 ids.get('decision-form').elements.subject.value='another-member';ids.get('decision-form').oninput();release();await pending;
 assert.doesNotMatch(ids.get('access-decision').textContent,/Permit/);assert.match(ids.get('access-decision').textContent,/هنگام ارزیابی تغییر کرد/);
});
test('markup preserves profile scope, operational boundaries and authenticated workbench navigation',()=>{
 const page=fs.readFileSync(path.join(__dirname,'../src/Fanasa.AccessManagement.Web/Pages/Governance.cshtml'),'utf8');
 for(const id of ['policy-authoring','policy-catalog','attribute-sources','decision-workbench','access-decision','enforcement-boundary'])assert.ok(page.includes(`id="${id}"`),id);
 for(const text of ['PolicySet','deny-overrides','enforced: false','claim','تعهد اجباری','تأیید به‌تنهایی دادهٔ PIP'])assert.ok(page.includes(text),text);
 assert.match(page,/@if\(Model.IsAdmin\)\{<p class="governance-note"><a[^>]*href="\/PolicyWorkbench"/);
 assert.doesNotMatch(page,/الگو، نسخه، انتشار مستقل و بازنشستگی<\/p>/);
});
test('mandatory time guard uses saved calendar, fixed Deny, no hidden role or hours and explicit runtime flag',async()=>{
 const {ids,calls,submit}=await setup(),form=ids.get('pattern-form');form.elements.template.value='time-guard';form.elements.effect.value='Permit';form.elements.roleId.value='role-a';form.elements.utcStartHour.value='8';form.elements.utcEndHour.value='16';form.elements.calendarId.value='office';form.elements.enforce.checked=true;form.onchange();
 assert.equal(form.elements.effect.value,'Deny');assert.equal(form.elements.effect.disabled,true);assert.equal(form.elements.roleId.disabled,true);assert.equal(form.elements.calendarId.required,true);
 await submit('pattern-form');const request=calls.find(x=>x.url.endsWith('/patterns'));assert.equal(request.body.template,'time-guard');assert.equal(request.body.effect,'Deny');assert.equal(request.body.calendarId,'office');assert.equal(request.body.calendarMode,'working-time');assert.equal(request.body.enforce,true);assert.equal(request.body.roleId,null);assert.equal(request.body.utcStartHour,null);assert.equal(request.body.utcEndHour,null);assert.match(ids.get('pattern-preview').textContent,/مجوزها متوقف/);
});
test('member calendar is a valid condition, data window cannot retain non-holiday mode, and guard requires reference',async()=>{
 const {ids,calls,submit}=await setup(),form=ids.get('pattern-form');form.elements.template.value='time-guard';form.onchange();await submit('pattern-form');assert.equal(calls.some(x=>x.url.endsWith('/patterns')),false);assert.match(ids.get('governance-message').textContent,/تقویم/);
 form.elements.template.value='member-window';form.elements.calendarId.value='data-window';form.elements.calendarMode.value='non-holiday';form.onchange();assert.equal(form.elements.calendarMode.value,'working-time');await submit('pattern-form');assert.equal(calls.find(x=>x.url.endsWith('/patterns')).body.calendarId,'data-window');
});
test('draft inclusion is explicit and never changes simulated enforcement',async()=>{
 const {ids,calls,submit}=await setup();ids.get('decision-form').elements.includeDrafts.checked=true;await submit('decision-form');assert.equal(calls.find(x=>x.url.endsWith('/simulate')).body.includeDrafts,true);assert.match(ids.get('access-decision').textContent,/با پیش‌نویس/);assert.match(ids.get('access-decision').textContent,/enforced = false/);assert.match(ids.get('policy-state').textContent,/برای این سازمان غیرفعال/);
});
test('template policies are read-only while draft simulation remains available',async()=>{const {ids,calls,submit}=await setup({template:true});assert.equal(ids.has('pattern-form'),false);assert.equal(ids.get('access-patterns').querySelectorAll('button').length,0);ids.get('decision-form').elements.includeDrafts.checked=true;await submit('decision-form');assert.equal(calls.find(x=>x.url.endsWith('/simulate')).body.includeDrafts,true);});
test('decision renders calendar evaluation from server including unknown coverage and exact local time',async()=>{const {ids,submit}=await setup({decision:{decision:'Indeterminate',reason:'missing-required-attribute',policies:[],obligations:[],advice:[],calendarChecks:[{known:false,allowed:false,reason:'outside-coverage',calendarId:'office',revision:3,timeZoneId:'Asia/Tehran',localTime:'2028-01-01T10:00:00+03:30'}]}});await submit('decision-form');const text=ids.get('access-decision').textContent;assert.match(text,/داده نامعتبر یا ناکافی/);assert.match(text,/outside-coverage/);assert.match(text,/Asia\/Tehran/);assert.match(text,/2028-01-01T10:00:00/);});


