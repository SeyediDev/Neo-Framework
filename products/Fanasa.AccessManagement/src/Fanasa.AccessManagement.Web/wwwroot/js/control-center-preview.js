(() => {
  'use strict';
  const one = selector => document.querySelector(selector);
  const all = selector => [...document.querySelectorAll(selector)];
  const number = new Intl.NumberFormat('fa-IR');
  const defaults = { state: 'active', tps: 40, quota: 12000, role: 'ApiInvoker' };
  const orgDefaults = { member: 'active', appointment: 'active', binding: 'bound', contract: 'active', scope: 'same-unit' };
  const views = ['overview', 'impact', 'organization', 'workstreams', 'readiness'];
  const stages = {
    contract: { title: 'قرارداد، مرجع اختیار', description: 'مالک محصول و سازمان مصرف‌کننده، دوره اعتبار، نقش مجاز و سقف مصرف را در قرارداد تعیین می‌کنند. تغییر قرارداد به نسخه تازه سیاست و محدودیت‌ها تبدیل می‌شود.', status: 'منشأ تجاری پایلوت: در انتظار پذیرش' },
    policy: { title: 'پروفایل محدود XACML از OASIS', description: 'PDP با قرارداد، عضویت، نقش و وضعیت جاری تصمیم می‌گیرد. Permit همراه با تعهدات کنترل سازمان و پذیرش سهمیه صادر می‌شود؛ هر پارامتر الزامی باید اجرا شود. این صفحه رفتار نمونه را نمایش می‌دهد.', status: '۱۳۴ بررسی محلی PCC ثبت شده است' },
    gateway: { title: 'Gateway، نقطه اعمال تصمیم', description: 'درگاه هویت و تصمیم را بررسی می‌کند و پذیرش TPS، burst و سهمیه را می‌گیرد. سپس context متصل به همان درخواست را برای کنترل دوباره در محصول ارسال می‌کند.', status: '۱۲ بررسی native محدود؛ پذیرش کامل باز است' },
    usage: { title: 'مصرف از عملیات متعهدشده', description: 'پس از commit موفق، رخداد مصرف، snapshot نرخ، رسید و دفتر مالی در ذخیره‌ساز موجود ثبت می‌شوند. retry دقیق عملیات، ثبت مالی دوم نمی‌سازد.', status: '۷۸ بررسی محلی receiver؛ تحویل بیرونی باز است' }
  };
  const workstreams = {
    gateway: { title: 'API Gateway', description: 'اعمال سیاست، محدودیت مشترک و رساندن درخواست معتبر به محصول؛ همان مسیر APISIX و Redis اختصاصی limiter.', phase: 'شاهد اجرای محدود موجود', next: 'پذیرش چند replica، دوام سهمیه و مشاهده معتبر rollout', href: '/Architecture#system-gateway' },
    contract: { title: 'قرارداد و XACML', description: 'تبدیل پارامترهای قرارداد به entitlement، قاعده، نقش مجاز و محدودیت نسخه‌دار؛ تصمیم در PCC موجود باقی می‌ماند.', phase: 'کنترل محلی و انتشار پیاده شده', next: 'اتصال منشأ قرارداد و نرخ معتبر همین محصول', href: '/Architecture#system-policy' },
    identity: { title: 'هویت و دسترسی', description: 'SSO مرکزی، ثبت برنامه، عضویت سازمان و grant صریح؛ نمایش برنامه در کاتالوگ به‌تنهایی اجازه استفاده نمی‌دهد.', phase: 'زیرساخت مشترک موجود', next: 'پذیرش یک محصول برای مصرف‌کننده متفاوت از مالک', href: '/Tenancy' },
    usage: { title: 'مصرف و حسابداری', description: 'ثبت رخداد، snapshot قیمت و دفتر مالی در همان تراکنش، همراه با outbox و retry؛ پذیرش سهمیه با اختیار مالی شناسه یکسان ندارد.', phase: 'مسیر محلی و outbox پیاده شده', next: 'پذیرش مقصد بیرونی و تطبیق یک مصرف واقعی', href: '/Accounting' },
    operations: { title: 'توسعه و عملیات', description: 'تحویل نسخه مشخص، آزمون محدود، پایش و بازیابی؛ محصول، رجیستری و ذخیره‌ساز موجود بازاستفاده می‌شوند.', phase: 'معماری و برنامه رشد آماده', next: 'عبور از سه گیت پذیرش پیش از توسعه دامنه', href: '/Architecture#architecture-document' }
  };
  const gates = [
    { title: 'اجرای Gateway و دوام سهمیه', badge: 'پذیرش کامل باز', status: 'partial', note: '۱۲ بررسی محدود native موفق ثبت شده؛ ظرفیت، چند replica و بازیابی هنوز به شاهد مستقل نیاز دارند.' },
    { title: 'کنترل محصول و commit', badge: 'شاهد محلی موجود', status: 'partial', note: '۱۳۴ بررسی PCC و ۷۸ بررسی receiver با HTTPS/mTLS ثبت شده‌اند. ورودی‌ها مصنوعی‌اند؛ Lua/APISIX/Redis در این suite اجرا نشده‌اند و smoke قبلی receiver تازه را شامل نمی‌شد.' },
    { title: 'یک محصول با منشأ تجاری معتبر', badge: 'در انتظار ورودی', status: 'open', note: 'قرارداد امضاشده، نرخ مصوب، client و grant معتبر و allocation همین محصول باید تحویل و بررسی شوند.' }
  ];
  let selectedStage = 'contract';
  let currentView = 'overview';
  const text = (selector, value) => { const element = one(selector); if (element) element.textContent = value; };
  function scenario() {
    return { state: one('#contract-state').value, tps: Number(one('#contract-tps').value), quota: Number(one('#contract-quota').value), role: one('#contract-role').value };
  }
  function decision(value) {
    if (value.state === 'suspended') return { permit: false, reason: 'قرارداد تعلیق شده است' };
    if (value.state === 'expired') return { permit: false, reason: 'دوره قرارداد پایان یافته است' };
    if (value.role !== 'ApiInvoker') return { permit: false, reason: 'نقش انتخاب‌شده اجازه فراخوانی API ندارد' };
    return { permit: true, reason: 'قرارداد فعال و نقش فراخوانی API انتخاب شده است' };
  }
  function organizationScenario() {
    return {
      member: one('#org-member-state').value, appointment: one('#org-appointment-state').value,
      binding: one('#org-binding-state').value, contract: one('#org-contract-state').value,
      scope: one('#org-resource-scope').value
    };
  }
  function organizationDecision(value) {
    const reasons = [];
    if (value.member !== 'active') reasons.push('عضویت سازمان غیرفعال است؛ ورود موفق به‌تنهایی اختیار سازمان نمی‌دهد.');
    if (value.appointment !== 'active') reasons.push('انتصاب معتبر فعلی وجود ندارد؛ اختیار مشتق از انتصاب پایان می‌یابد.');
    if (value.binding !== 'bound') reasons.push('اتصال مصوب نقش سازمانی به نقش محصول وجود ندارد.');
    if (value.contract !== 'active') reasons.push('قرارداد فعال و معتبر برای این محصول وجود ندارد.');
    if (value.scope !== 'same-unit') reasons.push('منبع خارج از محدوده واحد مصوب است؛ چارت دسترسی زیرشاخه‌ای ضمنی ایجاد نمی‌کند.');
    return { permit: reasons.length === 0, reasons };
  }
  function updateOrganization() {
    const result = organizationDecision(organizationScenario());
    const output = one('#org-access-result');
    output.dataset.result = result.permit ? 'permit' : 'deny';
    output.textContent = result.permit ? 'Permit نمایشی · همه شروط این سناریو برقرارند' : 'Deny نمایشی · دسترسی به این منبع مجاز نیست';
    const reasons = one('#org-access-reasons'); reasons.replaceChildren();
    (result.permit ? ['عضویت، انتصاب و اتصال مصوب معتبرند؛ مجوز فقط در محدوده واحد انتخاب‌شده است.'] : result.reasons).forEach(reason => {
      const item = document.createElement('li'); item.textContent = reason; reasons.append(item);
    });
  }
  function updateStage(key) {
    selectedStage = key;
    const stage = stages[key];
    text('#stage-title', stage.title); text('#stage-description', stage.description); text('#stage-status', stage.status);
    all('[data-stage]').forEach(button => {
      const selected = button.dataset.stage === key;
      button.setAttribute('aria-pressed', String(selected)); button.dataset.selected = String(selected);
    });
  }
  function updateWorkstream(key) {
    const stream = workstreams[key];
    text('#workstream-title', stream.title); text('#workstream-description', stream.description);
    text('#workstream-phase', stream.phase); text('#workstream-next', stream.next);
    const link = one('#workstream-link'); if (link) link.href = stream.href;
    all('[data-workstream]').forEach(button => {
      const selected = button.dataset.workstream === key;
      button.setAttribute('aria-pressed', String(selected)); button.dataset.selected = String(selected);
    });
  }
  function updateImpact() {
    const value = scenario(); const result = decision(value);
    text('#impact-tps-value', number.format(value.tps));
    text('#impact-quota-value', number.format(value.quota));
    text('#impact-role', value.role === 'ApiInvoker' ? 'فراخوانی API · ApiInvoker' : 'فقط مشاهده · Viewer');
    text('#impact-rule', result.permit ? 'Permit · شرایط نمونه برقرار است' : 'Deny · ' + result.reason);
    text('#impact-summary', result.permit ? 'درخواست نمونه می‌تواند به مرحله پذیرش سهمیه برسد.' : 'درخواست نمونه پیش از اجرای محصول رد می‌شود.');
    one('#impact-summary').dataset.result = result.permit ? 'permit' : 'deny';
    text('#impact-cost', result.permit ? 'پس از commit، یک رویداد مصرف نمایشی ثبت می‌شود.' : 'درخواست ردشده مصرف قابل‌صورتحساب ندارد.');
    const output = one('#request-result');
    output.dataset.result = 'idle';
    output.textContent = 'برای دیدن مسیر نتیجه، درخواست نمونه را اجرا کن.';
    all('[data-view="impact"]').forEach(button => { button.dataset.decision = result.permit ? 'permit' : 'deny'; });
  }
  function showView(view, changeHash = true) {
    if (!views.includes(view)) return;
    currentView = view;
    all('[data-panel]').forEach(panel => { panel.hidden = panel.dataset.panel !== view; });
    all('[data-view]').forEach(button => {
      const selected = button.dataset.view === view;
      if (selected) button.setAttribute('aria-current', 'page'); else button.removeAttribute('aria-current');
    });
    if (changeHash) history.replaceState(null, '', '#' + view);
  }
  all('[data-view]').forEach(button => button.addEventListener('click', () => showView(button.dataset.view)));
  all('[data-stage]').forEach(button => button.addEventListener('click', () => updateStage(button.dataset.stage)));
  all('[data-workstream]').forEach(button => button.addEventListener('click', () => updateWorkstream(button.dataset.workstream)));
  ['#contract-state', '#contract-tps', '#contract-quota', '#contract-role'].forEach(selector => one(selector).addEventListener('input', updateImpact));
  ['#org-member-state', '#org-appointment-state', '#org-binding-state', '#org-contract-state', '#org-resource-scope'].forEach(selector => one(selector).addEventListener('input', updateOrganization));
  one('#run-demo-request').addEventListener('click', () => {
    const value = scenario(); const result = decision(value); const output = one('#request-result');
    output.dataset.result = result.permit ? 'permit' : 'deny';
    output.textContent = result.permit
      ? 'نتیجه نمایشی: Permit → پذیرش نمونه سهمیه → commit نمونه → یک رخداد مصرف. هیچ درخواست واقعی ارسال نشد.'
      : 'نتیجه نمایشی: Deny؛ ' + result.reason + '. عملیات محصول و ثبت مصرف انجام نمی‌شوند.';
  });
  one('#reset-demo').addEventListener('click', () => {
    one('#contract-state').value = defaults.state; one('#contract-tps').value = String(defaults.tps);
    one('#contract-quota').value = String(defaults.quota); one('#contract-role').value = defaults.role;
    one('#org-member-state').value = orgDefaults.member; one('#org-appointment-state').value = orgDefaults.appointment;
    one('#org-binding-state').value = orgDefaults.binding; one('#org-contract-state').value = orgDefaults.contract;
    one('#org-resource-scope').value = orgDefaults.scope;
    updateOrganization();
    updateImpact(); updateStage('contract'); updateWorkstream('gateway');
    showView('impact');
  });
  one('#export-view').addEventListener('click', () => {
    const payload = { schemaVersion: 'fanasa.control-center-ui-preview.v1', dataMode: 'demonstration-only', backendRequestSent: false, view: currentView, selectedStage, scenario: scenario(), decision: decision(scenario()), organizationScenario: organizationScenario(), organizationDecision: organizationDecision(organizationScenario()), productionAcceptance: false };
    const url = URL.createObjectURL(new Blob([JSON.stringify(payload, null, 2)], { type: 'application/json' }));
    const link = document.createElement('a'); link.href = url; link.download = 'fanasa-ui-scenario.json'; link.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  });
  const list = one('#readiness-list');
  gates.forEach(gate => {
    const item = document.createElement('li'); item.className = 'cc-gate'; item.dataset.status = gate.status;
    const header = document.createElement('div'); header.className = 'cc-gate-header';
    const title = document.createElement('h3'); title.textContent = gate.title;
    const badge = document.createElement('span'); badge.className = 'cc-badge'; badge.textContent = gate.badge;
    const note = document.createElement('p'); note.className = 'cc-gate-note'; note.textContent = gate.note;
    header.append(title, badge); item.append(header, note); list.append(item);
  });
  updateStage(selectedStage); updateWorkstream('gateway'); updateImpact(); updateOrganization();
  const initialView = location.hash.slice(1);
  showView(views.includes(initialView) ? initialView : 'overview', false);
  window.addEventListener('hashchange', () => {
    const view = location.hash.slice(1);
    if (views.includes(view)) showView(view, false);
  });
})();
