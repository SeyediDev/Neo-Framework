const { test } = require('node:test');
const assert = require('node:assert/strict');
const vm = require('node:vm');
const fs = require('node:fs');
const path = require('node:path');
const source = fs.readFileSync(path.join(__dirname, '../../src/Neo.AgentOrchestration.Web/wwwroot/kanban.js'), 'utf8');
function fixture() {
    const listeners = {}, frames = [];
    const classes = () => { const set = new Set(); return { add: (...xs) => xs.forEach(x => set.add(x)), remove: (...xs) => xs.forEach(x => set.delete(x)), contains: x => set.has(x) }; };
    const columns = ['Backlog','Ready','InProgress','Done'].map(state => ({dataset:{state},classList:classes(),closest(){return this;}}));
    const message = {}, menu = {open:false}, claim = {};
    const form = {elements:{Destination:{value:'', focus(){this.focused=true;},dispatchEvent(){listeners.change({target:this});},matches:()=>true},ClaimRoleId:{required:false}},querySelector:()=>claim};
    form.elements.Destination.form=form;
    const card = {isConnected:true,classList:classes(),querySelector:s=>s==='.move-menu'?menu:form,
        querySelectorAll:()=>[{value:''},{value:'Ready'},{value:'InProgress'}],scrollIntoView(){}};
    const handle = {hidden:true,closest:()=>card,setPointerCapture(){this.captured=true;},hasPointerCapture(){return this.captured;},releasePointerCapture(){this.captured=false;}};
    const region = {querySelectorAll:()=>columns,getBoundingClientRect:()=>({left:0,right:600,top:0,bottom:600}),scrollBy(x){this.scrolled=x;}};
    let hit = columns[1];
    const document = {addEventListener:(name,fn)=>listeners[name]=fn,
        getElementById:()=>message,querySelector:()=>region,elementFromPoint:()=>hit,
        querySelectorAll:s=>s==='.move-handle'?[handle]:s==='.move-form'?[form]:s==='.drop-target'?columns.filter(x=>x.classList.contains('drop-target')):columns};
    const window = {NeoWorkbench:{busy:false}};
    vm.runInNewContext(source,{document,window,innerWidth:600,Date,Event:class {},requestAnimationFrame:fn=>{frames.push(fn);return frames.length;},cancelAnimationFrame(){}});
    const pointer=(overrides={})=>({target:{closest:()=>handle},button:0,isPrimary:true,pointerId:1,clientX:100,clientY:100,preventDefault(){},...overrides});
    return {listeners,columns,message,menu,claim,form,card,handle,window,region,frames,pointer,hit:state=>hit=columns.find(x=>x.dataset.state===state)};
}
for(const type of ['mouse','touch','pen']) test(`${type} drop selects destination for explicit confirmation, never submits`,()=>{
    const f=fixture();
    f.listeners.pointerdown(f.pointer({pointerType:type}));
    f.listeners.pointermove(f.pointer({clientX:200,pointerType:type}));
    assert.equal(f.columns[1].classList.contains('drop-allowed'),true);
    assert.equal(f.columns[3].classList.contains('drop-denied'),true);
    f.listeners.pointerup(f.pointer({clientX:200,pointerType:type}));
    assert.equal(f.form.elements.Destination.value,'Ready');
    assert.equal(f.menu.open,true);
    assert.equal(f.handle.captured,false);
    assert.equal(f.card.classList.contains('moving-card'),false);
    assert.match(f.message.textContent,/کارت هنوز جابه‌جا نشده/);
});
test('invalid drop leaves the card unchanged and cancellation releases capture',()=>{
    const f=fixture();f.hit('Done');
    f.listeners.pointerdown(f.pointer());f.listeners.pointermove(f.pointer({clientX:200}));f.listeners.pointerup(f.pointer({clientX:200}));
    assert.equal(f.menu.open,false);assert.equal(f.form.elements.Destination.value,'');
    f.listeners.pointerdown(f.pointer());f.listeners.pointermove(f.pointer({clientX:200}));f.listeners.pointercancel();
    assert.equal(f.handle.captured,false);assert.equal(f.columns.some(x=>x.classList.contains('drop-allowed')),false);
});
test('keyboard-equivalent handle activation opens menu and claim requires a role',()=>{
    const f=fixture();f.listeners.click(f.pointer());assert.equal(f.menu.open,true);
    f.form.elements.Destination.value='InProgress';f.listeners.change({target:f.form.elements.Destination});
    assert.equal(f.claim.hidden,false);assert.equal(f.form.elements.ClaimRoleId.required,true);
    f.form.elements.Destination.value='Ready';f.listeners.change({target:f.form.elements.Destination});
    assert.equal(f.claim.hidden,true);assert.equal(f.form.elements.ClaimRoleId.required,false);
});
test('busy SPA rejects drag; Escape cancels; pointer edge scrolls board',()=>{
    const f=fixture();f.window.NeoWorkbench.busy=true;f.listeners.pointerdown(f.pointer());assert.equal(f.handle.captured,undefined);
    f.window.NeoWorkbench.busy=false;f.listeners.pointerdown(f.pointer());f.listeners.pointermove(f.pointer({clientX:5}));
    f.frames[0]();assert.equal(f.region.scrolled,-12);
    f.listeners.keydown({key:'Escape'});assert.equal(f.handle.captured,false);
});
