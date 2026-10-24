(() => {
  'use strict';
  const $ = selector => document.querySelector(selector);
  const $$ = selector => [...document.querySelectorAll(selector)];
  const esc = value => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
  const fa = value => Number(value ?? 0).toLocaleString('fa-IR');
  const json = value => esc(JSON.stringify(value, null, 2));
  const badge = (text, kind = 'neutral') => `<span class="badge ${kind}">${esc(text)}</span>`;
  const detail = (label, data) => `<details class="wb-data"><summary>${esc(label)}</summary><pre dir="ltr">${json(data)}</pre></details>`;
  const row = (key, value) => `<div><dt>${esc(key)}</dt><dd><bdi>${esc(value)}</bdi></dd></div>`;
  const statusLabels = {Published:'منتشرشده',Draft:'پیش‌نویس',Retired:'بازنشسته'};
  const explanations = {
    'all-conditions-satisfied':['همهٔ شرایط دسترسی برقرارند.','تعهدها را اجرا کنید؛ ارزیابی هنوز مصرف یا تغییر سازمان ایجاد نکرده است.'],
    'explicit-deny':['سیاست ذخیرهٔ واحد سازمانی را صریحاً منع می‌کند.','منع را در نسخهٔ جدید بازبینی و پس از انتشار به قرارداد متصل کنید.'],
    'membership-denied':['عضویتِ معتبر برای این درخواست برقرار نیست.','عضویت، سازمان و بازهٔ اعتبار آن را در منبع هویت بررسی کنید.'],
    'grant-denied':['مجوز مستقیم و معتبر عملیات برقرار نیست.','مجوز متعلق به همین عضو، سازمان، محصول و عملیات را بررسی کنید.'],
    'stale-evidence':['شاهد قرارداد یا سیاست منقضی یا با نسخهٔ ارزیابی ناسازگار است.','مرجع قرارداد و نسخهٔ سیاست را منطبق کنید و شاهد تازه دریافت کنید.'],
    'target-mismatch':['محصول درخواست خارج از دامنهٔ این سیاست است.','سیاست منتشرشدهٔ همان محصول را انتخاب کنید؛ این نتیجه اجازه نیست.'],
    'zone-denied':['منطقهٔ درخواست در سیاست مجاز نیست.','منطقهٔ معتبر مسیر یا سیاست مناطق مجاز را بازبینی کنید.'],
    'contract-inactive':['قرارداد فعال نیست.','وضعیت حق استفادهٔ قرارداد را بازبینی کنید.'],
    'contract-time-denied':['زمان درخواست در بازهٔ معتبر قرارداد نیست.','زمان اثر و بازهٔ حق استفادهٔ قرارداد را بررسی کنید.'],
    'identity-denied':['احراز یا مجوز client برای عملیات معتبر نیست.','هویت، audience و scope معتبر درخواست را بررسی کنید.'],
    'tenant-denied':['سازمان درخواست فعال یا منطبق با قرارداد نیست.','سازمان معتبر مصرف‌کننده و وضعیت فعال آن را بررسی کنید.'],
    'action-denied':['این عملیات حق استفادهٔ معتبر در قرارداد ندارد.','قابلیت و عملیات مجازِ قرارداد را بررسی کنید.'],
    'invalid-projection':['snapshot قرارداد با قالب معتبر سازگار نیست.','نسخه و محتوای قرارداد را در منبع معتبر اصلاح کنید.'],
    'missing-attribute':['ویژگی لازم برای تعیین محصول درخواست وجود ندارد.','مسیر معتبر محصول را در Context handler تکمیل کنید.'],
    'missing-or-invalid-attribute':['اطلاعات لازمِ درخواست ناقص یا با قالب ناسازگار است.','اطلاعات معتبرِ موضوع، منبع، عملیات و محیط را تکمیل کنید.'],
    'scope-denied':['درخواست در دامنهٔ تخصیص قرار ندارد.','محصول، محیط و گروه عملیات تخصیص را تطبیق دهید.']
  };
  const outcomes = {Permit:['مجاز','success'],Deny:['رد صریح','danger'],Indeterminate:['نامعین','warning'],NotApplicable:['خارج از دامنه','neutral']};
  if ($('meta[name="wb-csrf"]')) {
    $('.sidebar-bottom strong').textContent = 'مدیر کارگاه';
    $('.sidebar-bottom small').textContent = 'نشست SSO · دادهٔ آزمایش';
    $('.preview-pill').textContent = 'کارگاه آزمایشی سرور';
    $('.notice-tag').textContent = 'ورود حفاظت‌شده';
  }
  let state, selectedId, evaluation, evaluationBinding, execution, busy = false, stage = 'policy';
  let readiness, healthReadAt = 0, healthBusy = false, healthError = '', validation, validationKey, stateGeneration = 0;
  const fieldIds = {name:'wbName',author:'wbAuthor',reviewer:'wbReviewer',zoneConstraints:'wbZones',requestsPerSecond:'wbRate',burstCapacity:'wbBurst',monthlyQuota:'wbQuota',explicitDeny:'wbDeny'};
  const fieldErrorIds = {name:'wbNameError',author:'wbAuthorError',reviewer:'wbReviewerError',zoneConstraints:'wbZonesError',requestsPerSecond:'wbRateError',burstCapacity:'wbBurstError',monthlyQuota:'wbQuotaError'};
  const storageCheckLabels = {'sqlite-integrity-check':'ساختار داخلی SQLite','foreign-key-check':'یکپارچگی کلیدهای خارجی','normalized-column-agreement':'تطبیق ستون‌های SQL با محتوای رکورد','durable-state-checksum':'هش دادهٔ پایدار','native-projection-schema':'قالب بومی snapshot قرارداد','trusted-context-schema-and-source-references':'قالب Context و مراجع منابع معتبر','policy-lifecycle-and-binding':'چرخهٔ سیاست و مرجع پذیرفته‌شده','decision-native-replay':'تطبیق تصمیم ذخیره‌شده با موتور بومی','resource-ownership-and-save-count':'مالک رکورد و تعداد ذخیره‌ها','atomic-operation-receipt-usage':'تطبیق عملیات، رسید و مصرف تراکنشی','revision-and-audit-coherence':'تطبیق بازبینی و تاریخچه'};
  const storageCountLabels = {policies:'سیاست‌ها',drafts:'پیش‌نویس‌ها',decisions:'تصمیم‌ها',resources:'رکوردها',operations:'عملیات',receipts:'رسیدها',auditEntries:'رویدادها',admittedInStoredPeriod:'مصرف ماه ذخیره‌شده',storedPeriod:'ماه ذخیره‌شده',trustedSources:'منابع معتبر',mutationVersion:'نسخهٔ تغییر داده'};
  const checkState = value => ({pass:['تأییدشده','success'],passed:['تأییدشده','success'],ok:['تأییدشده','success'],valid:['تأییدشده','success'],warning:['نیازمند توجه','warning'],warn:['نیازمند توجه','warning'],fail:['نیازمند اصلاح','danger'],failed:['نیازمند اصلاح','danger'],error:['نیازمند اصلاح','danger'],pending:['هنوز انجام نشده','neutral']})[String(value).toLowerCase()] || [String(value || 'نامشخص'),'neutral'];
  const checkList = checks => `<ul class="wb-check-list">${(checks || []).map(c => {const [label,kind]=checkState(c.status);return `<li>${badge(label,kind)}<div><strong>${esc(c.label || c.title || c.id)}</strong><p>${esc(c.detail || '')}</p></div></li>`;}).join('')}</ul>`;

  function showStage(next) {
    stage = next;
    $$('[data-stage-panel]').forEach(el => { el.hidden = el.dataset.stagePanel !== next; });
    $$('.wb-step').forEach(el => el.setAttribute('aria-pressed', String(el.dataset.stage === next)));
  }
  function revealStage(next) {
    showStage(next);
    const panel=$(`[data-stage-panel="${next}"]`);
    panel?.scrollIntoView({behavior:matchMedia('(prefers-reduced-motion: reduce)').matches?'auto':'smooth',block:'start'});
  }
  $('[data-workbench-start]').addEventListener('click',()=>revealStage('request'));
  function message(text, error = false) {
    $('#wbError').hidden = !error;
    $('#wbError').textContent = error ? text : '';
    if (!error) $('#wbStatus').textContent = text;
  }
  async function api(path = '', body) {
    const headers = body ? {'Content-Type':'application/json'} : {};
    const csrf = document.querySelector('meta[name="wb-csrf"]')?.content;
    if (body && csrf) headers['X-CSRF-TOKEN'] = csrf;
    const response = await fetch('/api/workbench' + path, {method:body ? 'POST':'GET',headers,body:body ? JSON.stringify(body):undefined});
    const data = await response.json();
    if (!response.ok) { const error = new Error(data.message || data.error || `HTTP ${response.status}`); error.status = response.status; error.data = data; throw error; }
    return data;
  }
  async function run(fn) {
    if (busy) return;
    busy = true;
    $('#view-workbench').setAttribute('aria-busy','true');
    const buttons = $$('#view-workbench button');
    const prior = buttons.map(el => el.disabled);
    buttons.forEach(el => { el.disabled = true; });
    message('در حال پردازش روی بک‌اند کارگاه…');
    try { await fn(); }
    catch (error) {if ([401,403,503].includes(error.status)) {healthError=error.status===503?'بک‌اند سلامت داده یا ثبت اثر را تأیید نکرد؛ کنترل سلامت را دوباره دریافت کنید.':'نشست یا مجوز دسترسی تأیید نشده است؛ با حساب مجاز وارد شوید و آخرین وضعیت را بخوانید.';renderReadiness();} message(error.status === 409 ? `تعارض بازبینی: ${error.message} آخرین وضعیت را بخوانید و تغییر را دوباره بررسی کنید.` : error.message, true); }
    finally { buttons.forEach((el,i) => { if (el.isConnected) el.disabled = prior[i]; }); busy = false; $('#view-workbench').removeAttribute('aria-busy'); syncButtons(); }
  }
  function selected() { return state?.policies.find(p => p.policyId === selectedId); }
  function readyForWork() {
    return readiness?.storage?.healthy === true && readiness?.canEvaluate !== false && !healthError && Date.now()-healthReadAt < 45000
      && (readiness?.storage?.revision == null || Number(readiness.storage.revision) === Number(state?.revision));
  }
  function independentReviewer(author) {
    const reviewer = $('#wbReviewer').value.trim();
    return reviewer && reviewer.toLocaleLowerCase() !== String(author || '').trim().toLocaleLowerCase() && $('#wbReviewReason').value.trim();
  }
  function syncButtons() {
    const published = selected()?.status === 'Published';
    const blocked = busy || !readyForWork();
    $('#wbBind').disabled = blocked || !published || !$('#wbReviewer').value.trim() || !$('#wbReviewReason').value.trim();
    $('#wbRetire').disabled = blocked || !published || !independentReviewer(selected()?.author);
    const expired = evaluation?.rawDecision?.validUntil && Date.parse(evaluation.rawDecision.validUntil)<=Date.now();
    $('#wbExecute').disabled = blocked || !evaluation || evaluation.rawDecision.decision !== 'Permit' || evaluation.policySelection === 'candidate' || (expired && !execution?.accepted);
    ['wbEvaluate','wbSaveDraft','wbValidate','wbReset'].forEach(id => {$('#'+id).disabled = blocked;});
    $('#wbLoadExample').disabled = busy || !readiness?.example;
    $$('[data-publish]').forEach(el=>{const draft=state?.drafts.find(d=>d.draftId===el.dataset.publish);el.disabled=blocked || !draft || !independentReviewer(draft.author);});
  }
  function invalidate() {
    evaluation = null; evaluationBinding = null; execution = null;
    renderEvaluation(); renderExecution(); syncButtons();
    renderProgress();
  }
  function renderReadiness() {
    const differentRevision = readiness?.storage?.revision != null && state && Number(readiness.storage.revision)!==Number(state.revision);
    const usable = readyForWork();
    const failed=(readiness?.checks || []).filter(c=>['fail','failed','error'].includes(String(c.status).toLowerCase()));
    $('#wbReadiness').innerHTML = `<div class="wb-health-heading">${badge(usable?'بک‌اند و داده آمادهٔ بررسی‌اند':'کار با داده تا تأیید سلامت متوقف است',usable?'success':'warning')}${usable && readiness?.ready===false?badge('درخواست پایه هنوز قابل اجرای موفق نیست','warning'):''}<span>${esc(readiness?.checkedAt || 'زمان کنترل دریافت نشده')}</span></div>${healthError?`<p class="wb-error">${esc(healthError)}</p>`:''}${differentRevision?'<p class="wb-error">دادهٔ سرور تغییر کرده است. «خواندن آخرین وضعیت» را بزنید تا با نسخهٔ تازه ادامه دهید؛ فرم شما حفظ می‌شود.</p>':''}${failed.length?checkList(failed):''}<details class="wb-data"><summary>کنترل تعاریف و شرایط اجرا؛ ${fa(readiness?.checks?.length || 0)} بررسیِ سرور</summary>${checkList(readiness?.checks)}</details><p class="wb-hint">${esc(readiness?.recommendedNext || 'سرور اتصال، قالب داده، مرجع سیاست و امکان ثبت اثر را کنترل می‌کند. تا دریافت نتیجهٔ معتبر، تنظیمات ذخیره یا اجرا نمی‌شوند.')} کنترل خودکار هر ۱۵ ثانیه در صفحهٔ فعال انجام می‌شود. شرط ردشدهٔ دسترسی را می‌توانید ارزیابی و اصلاح کنید؛ خطای سلامت دیتابیس، کار با داده را مسدود می‌کند.</p>`;
    const storage = readiness?.storage;
    $('#wbStorage').innerHTML = storage ? `<details class="wb-data"><summary>مخزن ${esc(storage.provider || 'نامشخص')} · ${storage.healthy?'سلامت تأییدشده':'نیازمند اصلاح'}</summary><dl class="wb-summary">${row('نسخهٔ ساختار دیتابیس',storage.schemaVersion ?? 'گزارش نشده')}${row('بازبینی ذخیره‌شده',storage.revision ?? 'گزارش نشده')}${row('آخرین کنترل',storage.checkedAt || readiness.checkedAt)}${row('ورود دادهٔ قدیمی',storage.legacyImported ? 'دادهٔ پیشین وارد و اعتبارسنجی شده':'گزارش ورود دادهٔ قدیمی ندارد')}</dl>${storage.counts?`<div class="wb-storage-counts">${Object.entries(storage.counts).map(([key,count])=>`<span>${esc(storageCountLabels[key] || key)} <strong><bdi>${esc(typeof count==='number'?fa(count):typeof count==='object'?JSON.stringify(count):count)}</bdi></strong></span>`).join('')}</div>`:''}${checkList((storage.checks || []).map(c=>typeof c==='string'?{label:storageCheckLabels[c] || c,status:storage.healthy?'pass':'pending',detail:c}:c))}${(storage.errors || []).length?`<p class="wb-error">${esc(storage.errors.join('؛ '))}</p>`:''}<p class="wb-hint">این دیتابیس برای کارگاه مستقل است. دادهٔ معتبرِ نمونه، ذخیره و مصرف واقعیِ همین کارگاه را اثبات می‌کند؛ به مجوز کاربران یا مصرف تجاری محصول تبدیل نمی‌شود.</p>${detail('گزارش مخزن از بک‌اند',storage)}</details>` : '';
    syncButtons();
  }
  function renderExample() {
    const e = readiness?.example;
    if (!e) return;
    const p = e.policy || {};
    const projection = state?.projection;
    const subject = state?.attributes?.find(x=>x.id==='subject')?.sample;
    $('#wbExample').innerHTML = `<div class="wb-example-goal"><h3>${esc(e.title)}</h3><p>${esc(e.goal)}</p></div><dl class="wb-summary">${row('عملیات و نقش','organization.unit.save · ApiInvoker')}${row('سازمان مصرف‌کننده',projection?.consumerTenantId || 'در قرارداد مشخص می‌شود')}${row('هویت درخواست و audience',`${subject?.subjectId || 'هویت نمونهٔ سرور'} · ${subject?.audience || 'fanasa-organization-fabric-api'}`)}${row('دامنهٔ تخصیص',`${projection?.productId || 'محصول ساختار سازمان'} · ${(projection?.limits?.scope?.environments || ['development']).join('، ')} · ${(projection?.limits?.scope?.operationGroups || []).join('، ')}`)}${row('تنظیمات پیشنهادی',`${(p.zoneConstraints || []).join('، ')} · نرخ ${p.requestsPerSecond} در ثانیه · جهش ${p.burstCapacity} · سهمیه ${p.monthlyQuota}`)}</dl>${e.sizing?.length?`<div class="wb-sizing"><h4>چرا این اعداد؟</h4><ul>${e.sizing.map(s=>`<li>${esc(s)}</li>`).join('')}</ul></div>`:''}<p class="wb-hint">مجوز مستقیم باید متعلق به همین عضو، سازمان، محصول و عملیات باشد؛ وجود حق استفاده در قرارداد به‌تنهایی grant نمی‌سازد. نام سازمان یا نقش را از ورودی قابل ویرایش کاربر به‌عنوان شاهد معتبر قبول نکنید.</p><div class="wb-example-outcomes"><article><h4>مسیر موفق</h4><p>سناریوی «شرایط کامل» با سیاستِ مجازِ متصل: Permit → پذیرش تخصیص و تطبیق مالک → یک ذخیره و یک واحد مصرف. تکرار همان تصمیم، مصرف تازه ندارد.</p></article><article><h4>دو کنترل متفاوت</h4><p>منع صریح: Deny پیش از اجرا. سهمیهٔ صفر: Permit مشروط، سپس quota-exhausted در مجری. انتخاب ir-private-2 برای درخواست پایه: zone-denied در ارزیابی.</p></article><article><h4>آزمون مرز اعتماد</h4><p>بدون grant: Deny. شاهد قدیمی: Indeterminate. محصول دیگر: NotApplicable. رکورد سازمان دیگر: Permit ولی توقف در PEP؛ هیچ ذخیره یا مصرف تازه ثبت نمی‌شود.</p></article></div>${e.expected?detail('نتیجه و تعهدهای مورد انتظار این مثال',e.expected):''}`;
    if (e.steps?.length) $('#wbGuideSteps').innerHTML=e.steps.map((s,i)=>`<li><strong>${esc(s.title)}</strong><p>${esc(s.detail)}</p><button class="text-button" data-stage="${['sources','policy','policy','policy','request','audit'][i] || 'audit'}">رفتن به این مرحله</button></li>`).join('');
  }
  function renderProgress() {
    if (!state) return;
    const p=selected(), bound=p && state.projection?.policy?.policyId===p.policyId && state.projection.policy.policyVersion===p.policyVersion;
    const checks=[
      {label:'۱. سلامت و تعریف معتبر',status:readyForWork()?'pass':'pending',detail:readyForWork()?'بک‌اند آخرین وضعیت دیتابیس و قرارداد را تأیید کرده است.':'کنترل سلامت را دریافت کنید و خطاهای گزارش‌شده را رفع کنید.'},
      {label:'۲. اعتبارسنجی و پیش‌نویس',status:validation?.valid && validationKey===configurationKey()?'pass':'pending',detail:validation?.valid && validationKey===configurationKey()?'قالب و محدودهٔ تنظیمات با بک‌اند بررسی شده‌اند؛ پیش‌نویس را برای بازبینی ذخیره کنید.':'تنظیمات پیشنهادی را در فرم قرار دهید و پیش از ذخیرهٔ پیش‌نویس، اعتبارسنجی کنید.'},
      {label:'۳. انتشار و اتصال سیاست انتخاب‌شده',status:bound && p.status==='Published'?'pass':'pending',detail:bound && p.status==='Published'?`قرارداد نسخهٔ ${fa(state.projection.contractVersion)} سیاست نسخهٔ ${fa(p.policyVersion)} را می‌خواند.`:'انتشار، مرجع را تغییر نمی‌دهد؛ نسخهٔ منتشرشدهٔ مورد نظر را انتخاب و صریحاً متصل کنید.'},
      {label:'۴. تصمیم همین درخواست',status:evaluation?'pass':'pending',detail:evaluation?`${evaluation.rawDecision.decision} · ${evaluation.rawDecision.reasonCode} · ${evaluation.policySelection==='candidate'?'بررسی پیش از اتصال؛ اجرا ممنوع':'مرجع پذیرفته‌شده'}`:'سناریوی شرایط کامل را با مرجع پذیرفته‌شدهٔ قرارداد ارزیابی کنید.'},
      {label:'۵. اثر همین تصمیم',status:execution?.accepted?'pass':execution?'warning':'pending',detail:execution?`${execution.reasonCode} · ${execution.accepted?'شاهد اثر را در مرحلهٔ ششم بررسی کنید.':'علت توقف را رفع و تصمیم تازه بسازید.'}`:'پس از Permit معتبر، تعهدها را اجرا کنید؛ تصمیم حداکثر ۳۰ ثانیه معتبر است.'}
    ];
    $('#wbProgress').innerHTML=`<h3>اکنون کجای مسیر هستید؟</h3>${checkList(checks)}`;
  }
  function policyInput() {
    return {name:$('#wbName').value.trim(),explicitDeny:$('#wbDeny').checked,zoneConstraints:$('#wbZones').value.split(',').map(s=>s.trim()).filter(Boolean),requestsPerSecond:Number($('#wbRate').value),burstCapacity:Number($('#wbBurst').value),monthlyQuota:Number($('#wbQuota').value)};
  }
  const configurationInput=()=>({policy:policyInput(),author:$('#wbAuthor').value.trim(),reviewer:$('#wbReviewer').value.trim()});
  const configurationKey=()=>JSON.stringify(configurationInput());
  function clearFieldErrors() {
    Object.entries(fieldErrorIds).forEach(([field,id])=>{$('#'+id).textContent='';$('#'+fieldIds[field]).removeAttribute('aria-invalid');});
  }
  function showReviewerHint() {
    const reviewer=$('#wbReviewer').value.trim(),author=$('#wbAuthor').value.trim();
    let hint='';
    if (!reviewer) hint='برای انتشار، برچسب بازبین مستقل را وارد کنید.';
    else if (author && reviewer.toLocaleLowerCase()===author.toLocaleLowerCase()) hint='بازبین و نویسندهٔ فرم یکسان‌اند؛ پیش‌نویس قابل ذخیره است ولی انتشار بازبین دیگری می‌خواهد.';
    else if (state?.drafts.some(d=>reviewer.toLocaleLowerCase()===d.author.trim().toLocaleLowerCase())) hint='این بازبین نویسندهٔ یکی از پیش‌نویس‌هاست؛ برای انتشار آن پیش‌نویس، بازبین دیگری وارد کنید.';
    if (hint && !$('#wbReviewerError').textContent) {$('#wbReviewerError').textContent=hint;$('#wbReviewer').setAttribute('aria-invalid','true');}
  }
  function renderValidation() {
    clearFieldErrors();
    if (!validation) { $('#wbValidation').innerHTML='<p class="wb-hint">فرم تغییر کرده است؛ تنظیمات فعلی را با بک‌اند اعتبارسنجی کنید. بررسی قبلی، تأیید این مقادیر نیست.</p>';showReviewerHint(); renderProgress();return; }
    for (const error of validation.errors || []) {
      const field=String(error.field || '').replace(/^policy\./,'').replace(/\[.*$/,'');
      if (fieldErrorIds[field]) {$('#'+fieldErrorIds[field]).textContent=error.message;$('#'+fieldIds[field]).setAttribute('aria-invalid','true');}
    }
    showReviewerHint();
    const issues=(validation.errors || []).concat(validation.warnings || []);
    $('#wbValidation').innerHTML=`<div class="wb-validation-heading">${badge(validation.valid?'تنظیمات معتبرند':'تنظیمات نیازمند اصلاح‌اند',validation.valid?'success':'danger')}${badge(validation.canPublish?'بازبین برای انتشار قابل قبول است':'انتشار نیازمند بازبین مستقل است',validation.canPublish?'success':'warning')}</div>${issues.length?`<ul class="wb-validation-issues">${issues.map(x=>`<li><strong>${esc(x.message)}</strong><code dir="ltr">${esc(x.code)} · ${esc(x.field)}</code></li>`).join('')}</ul>`:'<p class="wb-hint">خطای قالب یا محدوده گزارش نشده است. این نتیجه، اجرای موفق درخواست یا استقلال هویت بازبین را اثبات نمی‌کند.</p>'}${checkList(validation.checks)}<small>زمان بررسی: <bdi>${esc(validation.checkedAt)}</bdi></small>`;
    renderProgress();syncButtons();
  }
  async function validateConfiguration() {
    const input=configurationInput(), key=JSON.stringify(input);
    const errors=[];
    for (const field of ['requestsPerSecond','burstCapacity','monthlyQuota']) {
      const raw=$('#'+fieldIds[field]).value.trim();
      if (!raw) errors.push({field,code:'required',message:'این مقدار لازم است؛ خالی بودن با سهمیهٔ صفر تفاوت دارد.'});
      else if (!Number.isFinite(Number(raw))) errors.push({field,code:'invalid-number',message:'عدد معتبر وارد کنید.'});
      else if (field!=='requestsPerSecond' && !Number.isInteger(Number(raw))) errors.push({field,code:'integer-required',message:'تعداد درخواست باید عدد صحیح باشد.'});
    }
    if (errors.length) {validation={valid:false,canPublish:false,checkedAt:new Date().toISOString(),errors,warnings:[],checks:[]};validationKey=key;renderValidation();return {result:validation,input};}
    const result=await api('/validate',input);
    if (configurationKey()!==key) {validation=null;validationKey=null;renderValidation();throw new Error('فرم هنگام بررسی تغییر کرده است؛ مقادیر فعلی را دوباره اعتبارسنجی کنید.');}
    validation=result;validationKey=key;renderValidation();
    return {result,input};
  }
  async function checkHealth() {
    if (healthBusy || busy || !state || document.hidden) return;
    healthBusy=true;
    const generation=stateGeneration;
    try {const result=await api('/readiness');if (generation===stateGeneration && (result.storage?.revision==null || readiness?.storage?.revision==null || Number(result.storage.revision)>=Number(readiness.storage.revision))) {readiness=result;healthReadAt=Date.now();healthError='';}}
    catch(error) {if (generation===stateGeneration) {if (error.data?.ready!==undefined) {readiness=error.data;healthReadAt=Date.now();}healthError=`کنترل سلامت در دسترس نیست: ${error.message}. اتصال را بازیابی و دوباره کنترل کنید.`;}}
    finally {healthBusy=false;renderReadiness();renderExample();renderProgress();}
  }
  function renderPolicy() {
    const p = selected();
    if (!p) { $('#wbPolicyDetail').textContent = 'سیاستی انتخاب نشده است.'; return; }
    $('#wbPolicyDetail').innerHTML = `<h3>${esc(p.name)}</h3><dl class="wb-summary">${row('شناسه',p.policyId)}${row('نسخه و وضعیت',`${p.policyVersion} · ${statusLabels[p.status] || p.status}`)}${row('اثر عملیات',p.explicitDeny ? 'منع صریح (Deny)':'اجازه با تحقق همهٔ شروط')}${row('منطقه‌های مجاز',p.zoneConstraints.join('، '))}${row('نرخ / ظرفیت لحظه‌ای',`${p.requestsPerSecond} درخواست در ثانیه / ${p.burstCapacity} درخواست`)}${row('حد ماهانه',`${p.monthlyQuota} درخواست`)}${row('نویسنده / بازبین',`${p.author} / ${p.reviewer || 'هنوز بازبینی نشده'}`)}</dl>${detail('محتوای دقیق سیاست انتخاب‌شده',p)}`;
    $$('#wbPolicies [data-policy]').forEach(el => el.setAttribute('aria-pressed',String(el.dataset.policy === selectedId)));
    syncButtons();
  }
  function renderState() {
    if (!state) return;
    stateGeneration++;
    if (!state.policies.some(p => p.policyId === selectedId)) selectedId = state.policies.find(p => p.status === 'Published')?.policyId || state.policies[0]?.policyId;
    $('#wbRevision').textContent = `بازبینی ${fa(state.revision)}`;
    $('#wbPolicies').innerHTML = state.policies.map(p => `<button class="wb-policy" data-policy="${esc(p.policyId)}" aria-pressed="${p.policyId === selectedId}"><strong>${esc(p.name)}</strong><span class="wb-tags">${badge(`نسخهٔ ${fa(p.policyVersion)}`)}${badge(statusLabels[p.status] || p.status,p.status === 'Published'?'success':'neutral')}</span><code dir="ltr">${esc(p.policyId)}</code></button>`).join('');
    const p = state.projection;
    if (state.readiness) {readiness=state.readiness;healthReadAt=Date.now();healthError='';}
    else {readiness=null;healthError='بک‌اند گزارش سلامت ارائه نکرده است؛ نسخهٔ رابط و سرور را منطبق کنید.';}
    $('#wbBinding').innerHTML = `<strong>مرجع پذیرفته‌شدهٔ قرارداد</strong><p>قرارداد نسخهٔ ${fa(p.contractVersion)} ← سیاست نسخهٔ ${fa(p.policy.policyVersion)} · تنظیمات تخصیص ${fa(p.limits.configRevision)}</p><code dir="ltr">${esc(p.policy.policyId)}</code><p>انتشار سیاست جدید، این مرجع را خودکار تغییر نمی‌دهد.</p>`;
    $('#wbDrafts').innerHTML = state.drafts.length ? state.drafts.map(d => `<article class="wb-rule"><h4>${esc(d.name)} · پیش‌نویس نسخهٔ ${fa(d.policyVersion)}</h4><p>${esc(d.reason)} · نویسنده: ${esc(d.author)}</p>${detail('بازبینی محتوا پیش از انتشار',d)}<button class="button secondary" data-publish="${esc(d.draftId)}">انتشار این پیش‌نویس توسط بازبین</button></article>`).join('') : '<p class="wb-hint">پیش‌نویس باز برای انتشار وجود ندارد.</p>';
    const currentScenario = $('#wbScenario').value;
    $('#wbScenario').innerHTML = state.scenarios.map(s => `<option value="${esc(s.id)}">${esc(s.title)}</option>`).join('');
    if (state.scenarios.some(s => s.id === currentScenario)) $('#wbScenario').value = currentScenario;
    renderScenario(); renderPolicy(); renderSources(); renderAudit(); renderReadiness();renderExample();renderProgress();
    message(`موتور اصلی متصل است · مخزن ${readiness?.storage?.provider || 'در انتظار تأیید'} · بازبینی ${fa(state.revision)}`);
  }
  function renderScenario() {
    const s = state?.scenarios.find(s => s.id === $('#wbScenario').value);
    $('#wbScenarioDescription').innerHTML = s ? `<strong>${esc(s.title)}</strong><p>${esc(s.description)}</p><p>تغییرات دادهٔ آزمایش: ${esc(s.changes.join('؛ ') || 'شرایط پایهٔ معتبر')}</p><p>انتظار در سیاست پایه: <bdi>${esc(s.expectedDecision)}</bdi> · نتیجهٔ اجرا: <bdi>${esc(s.expectedExecution)}</bdi>. سیاست متصل و حدود جاری می‌توانند این انتظار را تغییر دهند؛ خروجی واقعی را در مرحلهٔ تصمیم بخوانید.</p>` : '';
  }
  function renderSources() {
    const attributes = state?.attributes || [];
    $('#wbSources').innerHTML = attributes.map(a => `<article class="wb-source"><strong>${esc(a.label)}</strong><p>منبع: ${esc(a.source)}</p><p>${esc(a.trust)}</p><code dir="ltr">${esc(a.fields.join(', '))}</code>${detail('نمونهٔ اطلاعات این منبع',a.sample)}</article>`).join('');
    $('#wbInputs').innerHTML = evaluation ? `${detail('اطلاعات معتبر همین ارزیابی (Context)',evaluation.context)}${detail('snapshot قرارداد و سیاست همین ارزیابی',evaluation.projection)}${detail('منشأ داده در این ارزیابی',evaluation.provenance)}` : '<p class="wb-hint">برای مشاهدهٔ ورودی استفاده‌شده در موتور، ابتدا یک درخواست را ارزیابی کنید. نمونهٔ منبع بالا، شاهد ارزیابی انجام‌شده نیست.</p>';
    const c = evaluation?.context;
    $('#wbRequest').innerHTML = c ? [
      ['موضوع · Subject','هویت معتبر و عضویت',`${c.subject.subjectId} · عضویت ${c.membership.active ? 'فعال':'غیرفعال'} · نقش ${c.grant.role}`],
      ['منبع · Resource','مسیر محصول و سازمان',`${c.resource.productId} · مصرف‌کننده ${c.resource.consumerTenantId}`],
      ['عملیات · Action','عملیات شناخته‌شدهٔ endpoint',c.action],
      ['محیط · Environment','زمان و منطقهٔ آزمایش',`${c.environment.now} · ${c.environment.zone}`]
    ].map(([label,source,value])=>`<article class="wb-source"><strong>${label}</strong><p>${source}</p><code dir="ltr">${esc(value)}</code></article>`).join('')+`<article class="wb-source"><strong>محتوای عملیات ذخیره</strong><p>نام در تصمیم محفوظِ سرور: ${esc(evaluation.proposedUnitName || 'گزارش نشده')}</p><p>این نامِ پیشنهادی، هویت یا سازمان مالک رکورد را تعیین نمی‌کند.</p></article>` : `<p class="wb-hint">درخواست نمونه: عضو دارای ApiInvoker می‌خواهد نامِ یک واحد متعلق به سازمان خودش را به «${esc($('#wbProposedUnitName').value.trim())}» تغییر دهد؛ در محصول ساختار سازمان، محیط توسعه و منطقهٔ ir-private-1. ارزیابی، مقدار دقیق هر دسته و منبع آن را نشان می‌دهد. این پیش‌نویس درخواست هنوز در رکورد ذخیره نشده است.</p>`;
  }
  function renderEvaluation() {
    if (!evaluation) { $('#wbDecision').innerHTML = '<div class="wb-result"><h3>هنوز تصمیمی برای این درخواست ساخته نشده است.</h3><p>در مرحلهٔ درخواست، سناریو و مرجع سیاست را انتخاب کنید.</p><button class="button primary" data-stage="request">ساخت درخواست و ارزیابی</button></div>'; renderSources(); return; }
    const d = evaluation.rawDecision, [label,kind] = outcomes[d.decision] || [d.decision,'neutral'];
    const [reason,next] = explanations[d.reasonCode] || ['کد علتِ موتور در زیر آمده است؛ ورودی و مرجع سیاست را بررسی کنید.','پیش از اجرای عملیات، علت گزارش‌شده را رفع و دوباره ارزیابی کنید.'];
    const obligations = d.obligations.map(o => `<article class="wb-rule"><h4>${o.id.includes('admit-allocation') ? 'پذیرش تخصیص و حدود مصرف':'تطبیق سازمان مالک منبع'}</h4><code class="wb-path" dir="ltr">${esc(o.id)}</code>${detail('پارامترهای تعهد الزامی',o.parameters)}</article>`).join('');
    $('#wbDecision').innerHTML = `<div class="wb-result">${badge(`${d.decision} · ${label}`,kind)}${evaluation.policySelection === 'candidate' ? badge('بررسی پیش از اتصال؛ قابل اجرا نیست','warning'):''}<h3>${reason}</h3><p>${next}</p><dl class="wb-summary">${row('نام پیشنهادیِ محفوظ با تصمیم',evaluation.proposedUnitName || 'گزارش نشده')}${row('کد علت موتور',d.reasonCode)}${row('شناسهٔ تصمیم',evaluation.decisionId)}${row('زمان ارزیابی',d.evaluatedAt || 'ثبت نشده')}${row('اعتبار تا',d.validUntil || 'بدون اجازهٔ معتبر برای اجرا')}</dl></div><div id="wbExpiredDecision" class="wb-error" role="status" hidden><strong>مهلت همین تصمیم تمام شده است؛ اجرا مسدود است.</strong><p>شرایط در زمان ارزیابی برقرار بوده‌اند، اما Permit منقضی قابل اجرای تازه نیست. نتیجهٔ موتور بازنویسی نمی‌شود؛ تصمیم تازه بسازید.</p><button class="button secondary" data-stage="request">ارزیابی دوبارهٔ درخواست</button></div>${obligations ? `<h3>تعهدهای لازم برای Permit</h3><div class="wb-rule-list">${obligations}</div>`:'<p class="wb-hint">این نتیجه تعهد قابل اجرا و اجازهٔ عملیات ندارد.</p>'}<div class="wb-actions"><button class="button primary" data-stage="execution">بررسی تعهدها و اجرای محلی</button><button class="button secondary" data-stage="sources">بررسی ورودی و منبع اطلاعات</button></div>${detail('خروجی بدون تغییر موتور اصلی',d)}<p class="wb-hint">موتور یک تصمیم و کد علت برمی‌گرداند؛ این صفحه ادعای گزارش نتیجهٔ مستقل تک‌تک Ruleها را ندارد.</p>`;
    updateDecisionLifetime();
    renderSources(); syncButtons();renderProgress();
  }
  function renderExecution() {
    if (!execution) {const expired=evaluation?.rawDecision?.validUntil && Date.parse(evaluation.rawDecision.validUntil)<=Date.now(); $('#wbExecution').innerHTML = `<div class="wb-result"><p>${evaluation ? `تصمیم جاری: ${esc(evaluation.rawDecision.decision)} · ${esc(evaluation.decisionId)}`:'ابتدا تصمیم معتبر بسازید.'}</p><p>شکست هر تعهد، ثبت عملیات و مصرف تازه را متوقف می‌کند.</p>${expired?'<p class="wb-error">تصمیم منقضی است؛ اجرای تازه ممکن نیست. ارزیابی دوباره لازم است.</p><button class="button secondary" data-stage="request">ساخت تصمیم تازه</button>':''}</div>`; return; }
    const label = execution.accepted ? execution.idempotent ? 'همان عملیات قبلی بازگردانده شد؛ مصرف تازه ندارد.':'عملیات محلی و مصرف ثبت شدند.' : 'عملیات اجرا نشد؛ مصرف تازه ثبت نشد.';
    const remedies={'quota-exhausted':'سهمیهٔ باقی‌مانده صفر است؛ در صورت مجاز بودن، نسخهٔ تازه با سقف بالاتر منتشر و متصل کنید. مصرف قبلی باقی می‌ماند.','rate-exhausted':'ظرفیت جهش تمام شده است. مطابق نرخِ قرارداد برای بازپُرشدن منتظر بمانید و تصمیم تازه بسازید.','resource-tenant-mismatch':'مالک واقعی رکورد با سازمان مصرف‌کننده یکی نیست؛ رکورد درستِ همان سازمان را انتخاب کنید. مجوز عمومی مشکل مالکیت را رفع نمی‌کند.','expired-decision':'مهلت تصمیم گذشته است؛ ارزیابی تازه لازم است.','unknown-obligation':'مجری این الزام را نمی‌شناسد؛ تعریف و پیاده‌سازی الزام باید منطبق شوند. اجازه را بدون آن اجرا نکنید.','stale-policy':'مرجع سیاست تغییر کرده است؛ آخرین وضعیت را بخوانید و دوباره ارزیابی کنید.','binding-changed':'مرجع پذیرفته‌شدهٔ قرارداد تغییر کرده است؛ آخرین وضعیت را بخوانید و دوباره ارزیابی کنید.','sources-changed':'منابع معتبر دیتابیس پس از تصمیم تغییر کرده‌اند، یا تصمیم قدیمی شاهد منابع ندارد؛ با دادهٔ فعلی دوباره ارزیابی کنید. اجازهٔ قبلی قابل اجرای تازه نیست.','retired-policy':'سیاست متصل بازنشسته شده است؛ نسخهٔ منتشرشدهٔ معتبر را پس از بازبینی متصل و دوباره ارزیابی کنید.'};
    $('#wbExecution').innerHTML = `<div class="wb-result">${badge(execution.accepted?'اجرای پذیرفته‌شده':'اجرا مسدود',execution.accepted?'success':'warning')}<h3>${label}</h3><code dir="ltr">${esc(execution.reasonCode)}</code>${remedies[execution.reasonCode]?`<p>${esc(remedies[execution.reasonCode])}</p>`:''}</div><div class="wb-rule-list">${execution.obligations.map(o=>`<article class="wb-rule"><h4>${esc(o.id)} · ${esc(({fulfilled:'محقق شد',refused:'رد شد',blocked:'متوقف شد'})[o.status] || o.status)}</h4><p>${esc(o.detail)}</p></article>`).join('')}</div>${execution.operation ? detail('عملیات ثبت‌شده در دیتابیس',execution.operation):''}${detail('پاسخ اجرای همین تصمیم',execution)}<button class="button secondary" data-stage="audit">مشاهدهٔ تغییر و مصرف ثبت‌شده</button>`;
    renderProgress();
  }
  function updateDecisionLifetime() {
    const el=$('#wbExpiredDecision');
    if (el) el.hidden=!(evaluation?.rawDecision?.decision==='Permit' && evaluation.rawDecision.validUntil && Date.parse(evaluation.rawDecision.validUntil)<=Date.now() && !execution?.accepted);
    syncButtons();
  }
  function renderAudit() {
    const u = state.usage;
    $('#wbUsage').innerHTML = `<article><small>مصرف ثبت‌شدهٔ محلی</small><strong>${fa(u.admitted)}</strong><span>درخواست</span></article><article><small>باقی‌ماندهٔ سقف ماهانه</small><strong>${fa(u.remaining)}</strong><span><bdi dir="ltr">${esc(u.periodId)}</bdi></span></article><article><small>ظرفیت لحظه‌ای موجود</small><strong>${fa(u.tokens)}</strong><span>تنظیمات ${fa(u.configRevision)}</span></article><article><small>عملیات ثبت‌شده</small><strong>${fa(state.operations.length)}</strong><span>بدون اثر مالی واقعی</span></article>`;
    $('#wbOperations').innerHTML = state.operations.length ? `<h3>عملیات موفق</h3>${state.operations.slice().reverse().map(o=>`<article class="wb-rule"><h4>ذخیرهٔ واحد سازمانی</h4><code dir="ltr">${esc(o.operationId)}</code><p>زمان <bdi>${esc(o.executedAt)}</bdi> · یک واحد مصرف</p>${detail('مرجع تصمیم و سازمانِ عملیات',o)}</article>`).join('')}`:'<p class="wb-hint">عملیات موفقی ثبت نشده است. Permit به‌تنهایی این فهرست را تغییر نمی‌دهد.</p>';
    $('#wbResources').innerHTML = (state.resources || []).map(r=>`<article class="wb-source"><strong>${esc(r.name)}</strong><p>تعداد ذخیرهٔ موفق: ${fa(r.saveCount)} · آخرین ذخیره: <bdi>${esc(r.lastSavedAt || 'هنوز ذخیره نشده')}</bdi></p><code dir="ltr">${esc(r.resourceId)} · ${esc(r.tenantId)}</code></article>`).join('');
    const types = {'policy-drafted':'ایجاد پیش‌نویس','policy-published':'انتشار سیاست','projection-accepted':'اتصال سیاست به قرارداد','policy-retired':'بازنشستگی سیاست','decision-evaluated':'ارزیابی درخواست','operation-executed':'اجرای درخواست','execution-refused':'اجرای متوقف‌شده','storage-migrated':'انتقال دادهٔ معتبر به SQLite',initialized:'ساخت محیط آزمایش',reset:'بازنشانی','execution-replayed':'بازگرداندن نتیجهٔ اجرای قبلی'};
    $('#wbHistory').innerHTML = '<h3>تاریخچهٔ کارگاه</h3>' + state.history.slice().reverse().map(h=>`<article class="wb-rule"><strong>${esc(types[h.type] || h.type)}</strong><p>${esc(h.reason)} · ${esc(h.actor)}</p><small><bdi>${esc(h.at)}</bdi> · بازبینی ${fa(h.revision)}</small>${detail('جزئیات رویداد',h.details)}</article>`).join('');
  }
  const review = () => ({expectedRevision:state.revision,reviewer:$('#wbReviewer').value.trim(),reason:$('#wbReviewReason').value.trim()});
  async function refresh() {
    state = await api();
    let invalidated = false;
    if (evaluation) {
      const p = evaluation.projection, current = state.projection;
      const available = state.policies.some(x=>x.policyId===p.policy.policyId && x.policyVersion===p.policy.policyVersion && x.status==='Published');
      const bound = evaluation.policySelection==='candidate' || (evaluationBinding && evaluationBinding.contractVersion===current.contractVersion && evaluationBinding.policy.policyId===current.policy.policyId && evaluationBinding.policy.policyVersion===current.policy.policyVersion && evaluationBinding.limits.configRevision===current.limits.configRevision);
      if (!available || !bound) { invalidate(); invalidated = true; }
    }
    renderState();
    if (invalidated) message('مرجع سیاست یا قرارداد تغییر کرده است؛ درخواست را با آخرین اطلاعات دوباره ارزیابی کنید.',true);
  }
  $('#view-workbench').addEventListener('click',event=>{
    const target = event.target.closest('button');
    if (!target || target.disabled) return;
    if (target.dataset.stage) {if (target.closest('#wbGuideSteps')) revealStage(target.dataset.stage);else showStage(target.dataset.stage);}
    if (target.dataset.policy) { selectedId = target.dataset.policy; invalidate(); renderPolicy(); }
    if (target.dataset.publish) run(async()=>{ const draft=state.drafts.find(d=>d.draftId===target.dataset.publish);state = await api('/publish',{...review(),draftId:target.dataset.publish});selectedId=draft?.policyId || selectedId;invalidate(); renderState();message('نسخه منتشر شد و اکنون انتخاب شده است؛ برای استفادهٔ قرارداد، اتصال صریح را انجام دهید.'); });
  });
  $('#wbDraftForm').addEventListener('submit',event=>{
    event.preventDefault();
    if (!state) return;
    run(async()=>{const {result,input}=await validateConfiguration();if (!result.valid) {message('تنظیمات ذخیره نشدند؛ خطاهای کنار فیلدها را اصلاح کنید.',true);return;}state = await api('/draft',{expectedRevision:state.revision,author:input.author,reason:$('#wbDraftReason').value.trim(),policy:result.normalizedPolicy || input.policy});invalidate(); renderState();message('پیش‌نویس معتبر در دیتابیس ذخیره شد؛ محتوای آن را بازبین مستقل بررسی و منتشر کند.');});
  });
  $('#wbValidate').addEventListener('click',()=>run(async()=>{const {result}=await validateConfiguration();message(result.valid?'قالب تنظیمات معتبر است؛ هشدارها و استقلال بازبین را پیش از انتشار بررسی کنید.':'خطاهای تنظیمات را اصلاح کنید.',!result.valid);}));
  $('#wbLoadExample').addEventListener('click',()=>{
    const e=readiness?.example;if (!e) return;
    const p=e.policy;
    $('#wbProposedUnitName').value=e.proposedUnitName || 'واحد فروش';invalidate();
    $('#wbName').value=p.name;$('#wbDeny').checked=!!p.explicitDeny;$('#wbZones').value=p.zoneConstraints.join(', ');$('#wbRate').value=p.requestsPerSecond;$('#wbBurst').value=p.burstCapacity;$('#wbQuota').value=p.monthlyQuota;$('#wbAuthor').value=e.author;$('#wbReviewer').value=e.reviewer;$('#wbDraftReason').value=e.reason;$('#wbReviewReason').value='بازبینی هدف، منطقه، نرخ و سهمیهٔ مثال';validation=null;validationKey=null;renderValidation();showStage('policy');$('#wbDraftForm').scrollIntoView({behavior:matchMedia('(prefers-reduced-motion: reduce)').matches?'auto':'smooth',block:'start'});$('#wbName').focus({preventScroll:true});message('تنظیمات پیشنهادی در فرم قرار گرفت؛ هنوز ذخیره، منتشر یا متصل نشده است.');
  });
  ['wbDraftForm','wbReviewer'].forEach(id=>$('#'+id).addEventListener('input',()=>{validation=null;validationKey=null;renderValidation();syncButtons();}));
  $('#wbReviewReason').addEventListener('input',syncButtons);
  $('#wbHealthRefresh').addEventListener('click',checkHealth);
  $('#wbRefresh').addEventListener('click',()=>run(refresh));
  $('#wbAuditRefresh').addEventListener('click',()=>run(refresh));
  $('#wbBind').addEventListener('click',()=>run(async()=>{state = await api('/bind',{...review(),policyId:selectedId}); invalidate(); renderState();}));
  $('#wbRetire').addEventListener('click',()=>run(async()=>{state = await api('/retire',{...review(),policyId:selectedId}); invalidate(); renderState();}));
  $('#wbScenario').addEventListener('change',()=>{invalidate();renderScenario();});
  $('#wbEvaluationMode').addEventListener('change',invalidate);
  $('#wbProposedUnitName').addEventListener('input',()=>{$('#wbProposedUnitName').setCustomValidity('');invalidate();});
  $('#wbEvaluate').addEventListener('click',()=>run(async()=>{
    const requestedName=$('#wbProposedUnitName').value.trim();
    $('#wbProposedUnitName').setCustomValidity(requestedName.length<2 || requestedName.length>120?'نام واحد باید بین ۲ و ۱۲۰ نویسهٔ معتبر باشد.':'');
    if (!$('#wbProposedUnitName').reportValidity()) return;
    invalidate();
    const body = {scenarioId:$('#wbScenario').value,proposedUnitName:requestedName};
    const requestedMode=$('#wbEvaluationMode').value,requestedPolicy=selectedId;
    if ($('#wbEvaluationMode').value === 'candidate') { if (!selectedId) throw new Error('ابتدا سیاست منتشرشده را انتخاب کنید.'); body.policyId = selectedId; }
    const acceptedBinding = structuredClone(state.projection);
    const result = await api('/evaluate',body);
    if (body.scenarioId!==$('#wbScenario').value || requestedName!==$('#wbProposedUnitName').value.trim() || requestedMode!==$('#wbEvaluationMode').value || requestedPolicy!==selectedId) {invalidate();await refresh();showStage('request');message('درخواست هنگام ارزیابی تغییر کرده است؛ نتیجهٔ قبلی برای اجرای درخواست فعلی استفاده نمی‌شود. دوباره ارزیابی کنید.',true);return;}
    evaluation = result; evaluationBinding = acceptedBinding; execution = null; renderEvaluation();renderExecution(); await refresh(); showStage('decision');
  }));
  $('#wbExecute').addEventListener('click',()=>run(async()=>{
    execution = await api('/execute',{decisionId:evaluation.decisionId,scenarioId:evaluation.scenarioId,failObligation:$('#wbFailure').value || undefined});
    renderExecution(); await refresh();showStage('execution');
  }));
  $('#wbReset').addEventListener('click',()=>run(async()=>{state = await api('/reset',{expectedRevision:state.revision});selectedId=null;invalidate();renderState();showStage('policy');}));
  document.addEventListener('visibilitychange',()=>{if (!document.hidden) checkHealth();});
  setInterval(()=>{if (!document.hidden) {if (Date.now()-healthReadAt>=45000) {renderReadiness();renderProgress();}checkHealth();}},15000);
  setInterval(()=>{if (!document.hidden && evaluation) updateDecisionLifetime();},1000);
  showStage(stage);renderEvaluation();renderExecution();run(refresh);
})();
