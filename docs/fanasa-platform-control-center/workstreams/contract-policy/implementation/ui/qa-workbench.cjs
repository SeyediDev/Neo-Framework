'use strict';
module.exports = async ({check,click,value:setValue,evaluate,until,screenshot,cdp,delay,expectHttpStatus}) => {
  const value = async (selector,text) => {await setValue(selector,text,'input');await setValue(selector,text,'change');};
  const ready = () => until("!document.querySelector('#view-workbench').hasAttribute('aria-busy')");
  const read = () => evaluate("fetch('/api/workbench').then(r=>r.json())");
  const scenario = async id => {
    await evaluate("document.querySelector('[data-stage=request]').click()");
    await value('#wbScenario',id);
    await click('#wbEvaluate');await ready();
  };
  const execute = async failure => {
    await evaluate("document.querySelector('[data-stage=execution]').click()");
    await value('#wbFailure',failure || '');await click('#wbExecute');await ready();
  };
  await ready();
  await check('Native workbench loads policies and ten server-owned scenarios',"document.querySelectorAll('#wbPolicies [data-policy]').length>0 && document.querySelector('#wbScenario').options.length===10 && document.querySelector('#wbError').hidden");
  await evaluate("document.querySelector('[data-stage=audit]').click();document.querySelector('#wbReset').closest('details').open=true");
  await click('#wbReset');await ready();
  await check('Reset initializes isolated state and independent references',"fetch('/api/workbench').then(r=>r.json()).then(s=>s.usage.admitted===0 && s.operations.length===0 && s.projection.contractVersion===7 && s.projection.policy.policyVersion===3)");
  await evaluate("document.querySelector('[data-stage=policy]').click()");
  await check('Database validity and backend readiness are visible',"document.querySelector('#wbStorage').textContent.includes('SQLite') && document.querySelector('#wbReadiness').textContent.includes('عضویت') && fetch('/api/workbench/readiness').then(r=>r.json()).then(s=>s.storage.healthy && s.ready && s.canEvaluate)");
  await check('Guided example includes capacity reasoning and six concrete steps',"document.querySelector('#wbExample').textContent.includes('۱۰۰۰') && document.querySelector('#wbGuideSteps').textContent.includes('قرارداد') && document.querySelector('#wbProgress').textContent.includes('پیش‌نویس')");
  await click('#wbLoadExample');
  await check('Explicit example action fills supported settings',"document.querySelector('#wbRate').value==='2' && document.querySelector('#wbBurst').value==='4' && document.querySelector('#wbQuota').value==='1000' && document.querySelector('#wbAuthor').value!==document.querySelector('#wbReviewer').value");
  await value('#wbRate','0');await click('#wbValidate');await ready();
  await check('Server validation explains field errors without saving',"document.querySelector('#wbRate').getAttribute('aria-invalid')==='true' && document.querySelector('#wbRateError').textContent.length>0 && fetch('/api/workbench').then(r=>r.json()).then(s=>s.drafts.length===0)");
  await value('#wbRate','2');await value('#wbQuota','0');await click('#wbValidate');await ready();
  await check('Valid zero quota explains why Permit cannot execute',"document.querySelector('#wbValidation').textContent.includes('صفر') && fetch('/api/workbench').then(r=>r.json()).then(s=>s.projection.limits.quota.requests===1000 && s.drafts.length===0)");
  await value('#wbQuota','1000');await click('#wbValidate');await ready();
  await check('Valid guided settings receive authoritative approval',"!document.querySelector('#wbSaveDraft').disabled && document.querySelector('#wbValidation').textContent.includes('معتبر')");
  await value('#wbAuthor','policy-author');
  await check('Policy purpose and rules are concrete',"document.querySelector('#wbPolicyDetail').textContent.includes('ApiInvoker') || document.querySelector('#view-workbench').textContent.includes('ApiInvoker')");
  await check('Desktop workbench has no overflow','document.documentElement.scrollWidth<=innerWidth');
  await evaluate("window.scrollTo({top:0,behavior:'instant'})");
  await screenshot('workbench-desktop-policy');
  await scenario('permit');
  await check('Actual native Permit has obligations and no operation effect',"document.querySelector('#wbDecision').textContent.includes('Permit') && document.querySelector('#wbDecision').textContent.includes('urn:fanasa:obligation:admit-allocation:1') && fetch('/api/workbench').then(r=>r.json()).then(s=>s.usage.admitted===0 && s.operations.length===0)");
  await screenshot('workbench-desktop-decision');
  await execute('admit-allocation');
  await check('Failed mandatory admission blocks commit and metering',"document.querySelector('#wbExecution').textContent.includes('عملیات اجرا نشد') && fetch('/api/workbench').then(r=>r.json()).then(s=>s.usage.admitted===0 && s.operations.length===0)");
  await execute('');
  await check('Successful obligations commit one operation and one meter unit',"document.querySelector('#wbExecution').textContent.includes('ثبت شدند') && fetch('/api/workbench').then(r=>r.json()).then(s=>s.usage.admitted===1 && s.operations.length===1)");
  await check('Retained unit content is saved with the same receipt and metering',"fetch('/api/workbench').then(r=>r.json()).then(s=>s.resources.find(r=>r.resourceId==='unit-primary').name==='واحد فروش' && s.operations[0].savedName==='واحد فروش')");
  await execute('');
  await check('Replaying same decision is idempotent',"document.querySelector('#wbExecution').textContent.includes('مصرف تازه ندارد') && fetch('/api/workbench').then(r=>r.json()).then(s=>s.usage.admitted===1 && s.operations.length===1)");
  await screenshot('workbench-desktop-execution');
  for (const [id,decision] of [['explicit-deny','Deny'],['no-grant','Deny'],['inactive-membership','Deny'],['stale-evidence','Indeterminate'],['wrong-product','NotApplicable']]) {
    await scenario(id);
    await check(`Server scenario ${id} returns ${decision} and cannot execute`,`document.querySelector('#wbDecision').textContent.includes('${decision}') && document.querySelector('#wbExecute').disabled`);
  }
  for (const id of ['cross-tenant-resource','quota-exhaustion','unknown-obligation']) {
    await scenario(id);const before = await read();await execute('');
    await check(`${id} blocks native PEP without operation or metering`,`document.querySelector('#wbExecution').textContent.includes('عملیات اجرا نشد') && fetch('/api/workbench').then(r=>r.json()).then(s=>s.usage.admitted===${before.usage.admitted} && s.operations.length===${before.operations.length})`);
  }
  await scenario('expired-decision');
  await check('Expired Permit is explained and fresh evaluation required before execution',"document.querySelector('#wbExecute').disabled && !document.querySelector('#wbExpiredDecision').hidden && document.querySelector('#wbExpiredDecision').textContent.includes('دوباره')");
  await evaluate("document.querySelector('[data-stage=policy]').click()");
  await value('#wbName','نسخهٔ بازبینی <img src=x onerror=alert(1)>');
  await value('#wbQuota','900');await click('#wbDeny');
  await click('#wbDraftForm button[type=submit]');await ready();
  await check('Draft is persisted and user text escaped',"document.querySelector('#wbDrafts').textContent.includes('<img src=x') && !document.querySelector('#wbDrafts img')");
  await value('#wbReviewer','policy-author');
  await check('Self publication is prevented with independent review guidance',"document.querySelector('#wbDrafts [data-publish]').disabled && document.querySelector('#wbReviewerError').textContent.length>0 && document.querySelectorAll('#wbDrafts [data-publish]').length===1");
  await value('#wbReviewer','independent-reviewer');
  await click('#wbDrafts [data-publish]');await ready();
  const afterPublish = await read();
  const newest = afterPublish.policies.filter(p=>p.status==='Published').sort((a,b)=>b.policyVersion-a.policyVersion)[0];
  await check('Publication creates immutable policy and leaves contract binding unchanged',`fetch('/api/workbench').then(r=>r.json()).then(s=>s.drafts.length===0 && s.projection.policy.policyVersion===3 && s.policies.some(p=>p.policyVersion>3))`);
  await click(`[data-policy="${newest.policyId}"]`);
  await evaluate("document.querySelector('[data-stage=request]').click()");
  await value('#wbEvaluationMode','candidate');await value('#wbScenario','permit');
  await click('#wbEvaluate');await ready();
  await check('Candidate policy evaluation is labelled and cannot execute',"document.querySelector('#wbDecision').textContent.includes('پیش از اتصال') && document.querySelector('#wbExecute').disabled");
  await evaluate("document.querySelector('[data-stage=policy]').click()");
  await click('#wbBind');await ready();
  await check('Binding advances contract and only changed limits revision',`fetch('/api/workbench').then(r=>r.json()).then(s=>s.projection.contractVersion===8 && s.projection.policy.policyVersion===${newest.policyVersion} && s.projection.limits.configRevision===2 && s.usage.admitted===1)`);
  await value('#wbEvaluationMode','accepted');await scenario('permit');
  await check('New bound explicit Deny affects next real evaluation',"document.querySelector('#wbDecision').textContent.includes('Deny') && document.querySelector('#wbDecision').textContent.includes('explicit-deny')");
  await evaluate("document.querySelector('[data-stage=audit]').click()");
  await screenshot('workbench-desktop-audit');
  await cdp.send('Emulation.setDeviceMetricsOverride',{width:390,height:844,deviceScaleFactor:1,mobile:false});
  for (const stage of ['policy','request','sources','decision','execution','audit']) {
    await evaluate(`document.querySelector('[data-stage=${stage}]').click();window.scrollTo({top:0,behavior:'instant'})`);await delay(100);
    await check(`Mobile ${stage} has no horizontal page overflow`,'document.documentElement.scrollWidth<=innerWidth');
    if (stage==='request' || stage==='policy') {
      if (stage==='policy') await screenshot('workbench-mobile-guide');
      await evaluate(`document.querySelector('${stage==='policy'?'#wbDraftForm':'#wbProposedUnitName'}').scrollIntoView({block:'start',behavior:'instant'})`);
      await screenshot('workbench-mobile-'+stage);
    }
  }
  await evaluate("document.querySelector('[data-stage=audit]').click();document.querySelector('#wbReset').closest('details').open=true");
  await click('#wbReset');await ready();
  await check('Final browser state is clean and engine-connected',"fetch('/api/workbench').then(r=>r.json()).then(s=>s.operations.length===0 && s.native.engine==='ContractPolicyModule.Evaluator')");
};
