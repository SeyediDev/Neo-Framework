(() => {
  'use strict';

  // Every record and decision below is a local illustration, with no server or storage.
  const $ = (selector, root = document) => root.querySelector(selector);
  const $$ = (selector, root = document) => [...root.querySelectorAll(selector)];
  const escapeHtml = value => String(value).replace(/[&<>"']/g, character => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[character]));
  const fa = value => Number(value).toLocaleString('fa-IR');
  const date = value => new Intl.DateTimeFormat('fa-IR', { year: 'numeric', month: 'short', day: 'numeric', timeZone: 'UTC' }).format(new Date(`${value}T00:00:00Z`));
  const demoDate = '2026-10-10';
  const icon = name => `<svg class="icon" aria-hidden="true"><use href="#i-${name}"/></svg>`;
  const statuses = { Active: ['فعال', 'success'], Suspended: ['معلق', 'warning'], Ended: ['پایان‌یافته', 'neutral'] };
  const observations = { pending: ['در انتظار مشاهده', 'warning'], acknowledged: ['مشاهدهٔ منطبق', 'success'], superseded: ['جایگزین شده', 'neutral'] };
  const initialContracts = [
    { id: 'FCP-0101', name: 'همکاری سازمانی آذر', tenant: 'گروه آذر', initials: 'آ', status: 'Active', contractVersion: 7, policyVersion: 3, configRevision: 1, managementRevision: 2, gateway: 'acknowledged', policyStatus: 'Published', start: '2026-03-21', end: '2027-03-20', rate: 120, monthly: 50000, seats: 80, product: 'ساختار سازمان', role: 'ویرایشگر ساختار سازمان', reason: 'تخصیص حق استفاده به سازمان مصرف‌کننده', history: ['snapshot قرارداد نسخهٔ ۷ ثبت شد.', 'سیاست نسخهٔ ۳ در نمونه منتشر شد.', 'تخصیص تنظیمات ۱ با مشاهدهٔ منطبق ثبت شد.'] },
    { id: 'FCP-0102', name: 'طرح توسعهٔ سپهر', tenant: 'صنایع سپهر', initials: 'س', status: 'Active', contractVersion: 4, policyVersion: 2, configRevision: 3, managementRevision: 5, gateway: 'pending', policyStatus: 'Published', start: '2026-05-22', end: '2027-05-21', rate: 60, monthly: 20000, seats: 40, product: 'ساختار سازمان', role: 'ویرایشگر ساختار سازمان', reason: 'بازبینی حدود قراردادی', history: ['سیاست نسخهٔ ۲ در نمونه منتشر شد.', 'تنظیمات تخصیص ۳ ثبت شد؛ مشاهدهٔ Gateway در انتظار است.'] },
    { id: 'FCP-0103', name: 'خدمات پیشگام', tenant: 'پیشگام تدبیر', initials: 'پ', status: 'Suspended', contractVersion: 5, policyVersion: 1, configRevision: 2, managementRevision: 3, gateway: 'acknowledged', policyStatus: 'Published', start: '2026-01-21', end: '2027-01-20', rate: 30, monthly: 12000, seats: 25, product: 'ساختار سازمان', role: 'ویرایشگر ساختار سازمان', reason: 'تعلیق ثبت‌شدهٔ قرارداد', history: ['تخصیص تنظیمات ۲ مشاهده شد.', 'قرارداد با تصمیم مدیریتی معلق شد؛ نسخه‌ها مستقل ماندند.'] },
    { id: 'FCP-0104', name: 'همکاری آینده‌سازان', tenant: 'آینده‌سازان', initials: 'آ', status: 'Ended', contractVersion: 3, policyVersion: 2, configRevision: 1, managementRevision: 4, gateway: 'superseded', policyStatus: 'Retired', start: '2025-03-21', end: '2026-03-20', rate: 30, monthly: 10000, seats: 20, product: 'ساختار سازمان', role: 'ویرایشگر ساختار سازمان', reason: 'پایان ثبت‌شدهٔ همکاری', history: ['پایان همکاری به‌صورت صریح ثبت شد.', 'سیاست در نمونه بازنشسته شد.', 'مشاهدهٔ تخصیص پیشین جایگزین شده است.'] },
    { id: 'FCP-0105', name: 'دورهٔ نخست آذر', tenant: 'گروه آذر', initials: 'آ', status: 'Active', contractVersion: 6, policyVersion: 1, configRevision: 2, managementRevision: 2, gateway: 'acknowledged', policyStatus: 'Published', start: '2025-10-01', end: '2026-09-30', rate: 45, monthly: 15000, seats: 30, product: 'ساختار سازمان', role: 'ویرایشگر ساختار سازمان', reason: 'نمونهٔ بازهٔ سپری‌شده با وضعیت فعال', history: ['وضعیت ذخیره‌شدهٔ قرارداد فعال است.', 'بازهٔ حق استفاده سپری شد؛ وضعیت قرارداد خودکار تغییر نکرد.'] },
    { id: 'FCP-0106', name: 'دورهٔ بعدی سپهر', tenant: 'صنایع سپهر', initials: 'س', status: 'Active', contractVersion: 2, policyVersion: 1, configRevision: 1, managementRevision: 1, gateway: 'pending', policyStatus: 'Published', start: '2026-11-01', end: '2027-10-31', rate: 90, monthly: 30000, seats: 60, product: 'ساختار سازمان', role: 'ویرایشگر ساختار سازمان', reason: 'نمونهٔ بازهٔ آینده', history: ['قرارداد با وضعیت فعال ثبت شد.', 'شروع بازهٔ حق استفاده در آینده است؛ مشاهدهٔ تخصیص در انتظار است.'] }
  ];
  // Immutable contract snapshots retain the policy reference accepted with that snapshot.
  initialContracts.forEach(contract => { contract.acceptedPolicyVersion = contract.policyVersion; });
  const initialHistory = [
    { title: 'تخصیص آذر مشاهده شد', description: 'مشاهدهٔ منطبق برای تنظیمات ۱ ثبت شده است؛ این رویداد شاهد اجرای عملیات نیست.', contractId: 'FCP-0101', time: 'امروز · ۱۰:۲۴', kind: 'success' },
    { title: 'سیاست سپهر منتشر شد', description: 'سیاست نسخهٔ ۲ منتشر شده؛ وضعیت مشاهدهٔ Gateway همچنان در انتظار است.', contractId: 'FCP-0102', time: 'امروز · ۰۹:۴۰', kind: 'info' },
    { title: 'قرارداد پیشگام معلق شد', description: 'وضعیت قراردادی با تصمیم صریح به معلق تغییر یافته است.', contractId: 'FCP-0103', time: 'دیروز · ۱۵:۱۰', kind: 'warning' },
    { title: 'بازهٔ دورهٔ نخست آذر سپری شد', description: 'انقضای بازه، وضعیت ذخیره‌شدهٔ فعال را به پایان‌یافته تبدیل نکرد.', contractId: 'FCP-0105', time: '۹ مهر · نمونه', kind: 'neutral' }
  ];
  const views = {
    workbench: ['کارگاه اجرای XACML', 'سیاست، اطلاعات معتبر، تصمیم و اجرای تعهد را در یک مسیر عملی دنبال کنید.', 'کارگاه XACML'],
    contracts: ['قرارداد و سیاست دسترسی', 'حق استفاده، سیاست و وضعیت انتشار را در کنار هم ببینید.', 'قرارداد و سیاست'],
    decision: ['آزمایشگاه تصمیم', 'شرایط نمونه را تغییر دهید و تفاوت چهار نتیجهٔ تصمیم را ببینید.', 'آزمایشگاه تصمیم'],
    architecture: ['نقشهٔ اجرای دسترسی', 'مرز مسئولیت هر بخش از ورود درخواست تا ثبت عملیات.', 'نقشهٔ اجرای دسترسی'],
    history: ['رویدادهای نمونه', 'تغییرات همین پیش‌نمایش را در کنار روایت اولیه مرور کنید.', 'رویدادهای نمونه']
  };
  const architecture = {
    gateway: { title: 'Gateway؛ بررسی ورودی و پذیرش تخصیص', tag: 'مرز ورود درخواست', description: 'هویت معتبر مسیر و تخصیص پذیرفته‌شده در مرز ورود بررسی می‌شوند. انتشار سیاست، به‌تنهایی مشاهده یا پذیرش Gateway را ثابت نمی‌کند.', items: ['هویت سازمان و محصول از زمینهٔ معتبر مسیر می‌آید.', 'مشاهدهٔ تخصیص، وضعیتی مستقل از وضعیت قرارداد و انتشار سیاست دارد.', 'تصمیم مجاز باید همراه با الزامات لازم برای اجرا ارزیابی شود.'], note: 'در این پیش‌نمایش، وضعیت مشاهده دادهٔ نمونه است؛ هیچ مشاهدهٔ زنده‌ای دریافت نمی‌شود.' },
    pcc: { title: 'PCC؛ ارزیابی حق استفاده و سیاست', tag: 'تصمیم بدون اثر جانبی', description: 'قرارداد واجد شرایط بودن را مشخص می‌کند. عضویت سازمان، انتساب صریح نقش و مجوز مستقیم عملیات نیز مستقل بررسی می‌شوند.', items: ['اطلاعات معتبر از سمت سرور خوانده می‌شود؛ ادعای caller جای آن را نمی‌گیرد.', 'نتیجه می‌تواند Permit، Deny، Indeterminate یا NotApplicable باشد.', 'نسخه‌ها مستقل‌اند؛ مرجع سیاست در حق استفاده باید با نسخهٔ سیاست منتشرشده منطبق باشد.'], note: 'ارزیابی تصمیم، عملیات را اجرا نمی‌کند و مصرف یا بدهکارسازی ایجاد نمی‌کند.' },
    backend: { title: 'محصول؛ کنترل سازمان منبع تا ثبت تغییر', tag: 'مرز دامنه و تراکنش', description: 'محصول باید سازمان مالک منبع را با سازمان معتبر درخواست تطبیق دهد و منطق دامنه را پیش از ثبت تغییر بررسی کند.', items: ['واجد شرایط بودن قرارداد، انتساب نقش یا grant ایجاد نمی‌کند.', 'مجاز بودن تصمیم، کنترل منبع و الزامات اجرای محصول را حذف نمی‌کند.', 'کنترل مرز سازمان تا نقطهٔ ثبت تغییر معتبر باقی می‌ماند.'], note: 'آزمایشگاه صرفاً شرایط سناریوی محلی را نمایش می‌دهد؛ گزارش گام‌های endpoint واقعی نیست.' },
    accounting: { title: 'مصرف و حسابداری؛ پس از ثبت موفق عملیات', tag: 'اثر تراکنش معتبر', description: 'مصرف و حسابداری به عملیات ثبت‌شده مربوط‌اند. یک نتیجهٔ Permit یا مشاهدهٔ Gateway، سند انجام عملیات یا ایجاد هزینه نیست.', items: ['اثر مالی و مصرف برای عملیات معتبر و ثبت‌شده محاسبه می‌شود.', 'حد نرخ، سقف ماهانه و ظرفیت، حدود قراردادی‌اند؛ موجودی شمارنده نیستند.', 'تکرار ارزیابی در این صفحه هیچ اثر عملیاتی یا مالی ندارد.'], note: 'تمام مقادیر و رویدادهای این صفحه نمایشی‌اند و به حسابداری واقعی متصل نیستند.' }
  };

  const state = { contracts: structuredClone(initialContracts), history: structuredClone(initialHistory), status: 'all', view: 'contracts', selectedId: null, detail: 'rights', node: 'gateway', policyMode: 'publish', expectedRevision: null, menuOpen: false };
  const drawer = $('#contractDrawer');
  const policyDialog = $('#policyDialog');
  const focusReturn = new WeakMap();
  let toastTimer;
  const currentContract = () => state.contracts.find(contract => contract.id === state.selectedId);
  const isInWindow = contract => contract.start <= demoDate && demoDate <= contract.end;
  const windowText = contract => demoDate < contract.start ? 'بازه هنوز آغاز نشده' : demoDate > contract.end ? 'بازه سپری شده' : 'در بازهٔ معتبر';
  const badge = (label, kind = 'neutral') => `<span class="badge ${kind}">${escapeHtml(label)}</span>`;

  function toast(message) {
    window.clearTimeout(toastTimer);
    $('#toast').textContent = message;
    $('#toast').hidden = false;
    toastTimer = window.setTimeout(() => { $('#toast').hidden = true; }, 4500);
  }

  function renderStats() {
    const active = state.contracts.filter(contract => contract.status === 'Active').length;
    const pending = state.contracts.filter(contract => contract.gateway === 'pending').length;
    const metrics = [
      ['قراردادهای نمونه', state.contracts.length, 'در ۴ سازمان مصرف‌کننده', 'file', 'blue'],
      ['وضعیت ذخیره‌شدهٔ فعال', active, 'بازهٔ معتبر مستقل بررسی می‌شود', 'shield', 'teal'],
      ['در انتظار مشاهده', pending, 'مستقل از انتشار سیاست', 'clock', 'amber'],
      ['چهار نتیجهٔ تصمیم', 4, 'آزمایشگاه بدون اثر عملیاتی', 'flow', 'purple']
    ];
    $('#stats').innerHTML = metrics.map(([label, value, note, glyph, color]) => `<article class="stat-card ${color}"><div class="stat-top"><span>${label}</span><span class="stat-icon">${icon(glyph)}</span></div><strong>${fa(value)}</strong><small>${note}</small></article>`).join('');
    $('.nav-count').textContent = fa(state.contracts.length);
  }

  function filteredContracts() {
    const search = $('#search').value.trim().toLocaleLowerCase('fa-IR');
    return state.contracts.filter(contract => (state.status === 'all' || contract.status === state.status)
      && ($('#tenantFilter').value === 'all' || contract.tenant === $('#tenantFilter').value)
      && ($('#gatewayFilter').value === 'all' || contract.gateway === $('#gatewayFilter').value)
      && (!search || `${contract.name} ${contract.tenant} ${contract.id}`.toLocaleLowerCase('fa-IR').includes(search)));
  }

  function renderContracts() {
    const contracts = filteredContracts();
    $('#contractRows').innerHTML = contracts.map(contract => {
      const [statusLabel, statusKind] = statuses[contract.status];
      const [gatewayLabel, gatewayKind] = observations[contract.gateway];
      return `<tr><td><button class="contract-link" data-open-contract="${contract.id}" aria-label="جزئیات ${escapeHtml(contract.name)}"><span class="tenant-avatar">${contract.initials}</span><span class="contract-name"><strong>${escapeHtml(contract.name)}</strong><small>${escapeHtml(contract.tenant)} <span class="row-code" dir="ltr">${contract.id}</span></small></span></button></td><td>${badge(statusLabel, statusKind)}${!isInWindow(contract) ? `<small class="cell-note">${windowText(contract)}</small>` : ''}</td><td><strong class="cell-title">نسخهٔ ${fa(contract.policyVersion)}</strong><small class="cell-note">${contract.policyStatus === 'Published' ? 'منتشرشده' : 'بازنشسته'}</small></td><td><strong class="cell-title">${fa(contract.rate)} درخواست / دقیقه</strong><small class="cell-note">سقف ماهانه ${fa(contract.monthly)}</small></td><td>${badge(gatewayLabel, gatewayKind)}<small class="cell-note">تنظیمات ${fa(contract.configRevision)}</small></td><td><button class="icon-button row-action" data-open-contract="${contract.id}" aria-label="باز کردن قرارداد ${contract.id}">${icon('chevron')}</button></td></tr>`;
    }).join('');
    $('#emptyState').hidden = contracts.length > 0;
    $('.table-wrap').hidden = contracts.length === 0;
    $('#resultCount').textContent = `${fa(contracts.length)} قرارداد نمایشی${contracts.length < state.contracts.length ? ` از ${fa(state.contracts.length)}` : ''}`;
    $('#tableRange').textContent = contracts.length ? `نمایش ${fa(1)} تا ${fa(contracts.length)}` : 'بدون نتیجه';
    $$('[data-status]').forEach(button => {
      const active = button.dataset.status === state.status;
      button.classList.toggle('active', active);
      button.setAttribute('aria-pressed', String(active));
    });
  }

  function clearFilters() {
    $('#search').value = '';
    $('#tenantFilter').value = 'all';
    $('#gatewayFilter').value = 'all';
    state.status = 'all';
    renderContracts();
  }

  function closeMenu(restoreFocus = false) {
    state.menuOpen = false;
    $('#sidebar').classList.remove('mobile-open');
    $('#mobileBackdrop').hidden = true;
    $('#mobileMenu').setAttribute('aria-expanded', 'false');
    document.body.classList.remove('menu-open');
    if (restoreFocus) $('#mobileMenu').focus();
  }

  function setView(view, shouldFocus = false) {
    if (!views[view]) view = 'contracts';
    state.view = view;
    $$('.view').forEach(section => { section.hidden = section.id !== `view-${view}`; });
    $$('.nav-item').forEach(button => {
      const active = button.dataset.view === view;
      button.classList.toggle('active', active);
      if (active) button.setAttribute('aria-current', 'page'); else button.removeAttribute('aria-current');
    });
    const [title, description, breadcrumb] = views[view];
    $('#pageTitle').textContent = title;
    $('#pageDescription').textContent = description;
    $('#breadcrumbTitle').textContent = breadcrumb;
    if (location.hash !== `#${view}`) history.replaceState(null, '', `#${view}`);
    closeMenu();
    if (shouldFocus) $('#main').focus({ preventScroll: true });
    window.scrollTo({ top: 0, behavior: 'instant' });
  }

  function showModal(dialog, trigger = document.activeElement) {
    focusReturn.set(dialog, trigger);
    dialog.showModal();
  }

  function setDetail(detail, focus = false) {
    if (!['rights', 'limits', 'policy', 'history'].includes(detail)) detail = 'rights';
    state.detail = detail;
    $$('[data-detail]').forEach(button => {
      const active = button.dataset.detail === detail;
      button.classList.toggle('active', active);
      button.setAttribute('aria-selected', String(active));
      button.tabIndex = active ? 0 : -1;
      if (active && focus) button.focus();
    });
    $('#drawerBody').setAttribute('aria-labelledby', `tab-${detail}`);
    renderDrawer();
  }

  function renderDrawer() {
    const contract = currentContract();
    if (!contract) return;
    const [statusLabel, statusKind] = statuses[contract.status];
    const [gatewayLabel, gatewayKind] = observations[contract.gateway];
    $('#drawerTitle').textContent = contract.name;
    $('#drawerCode').textContent = `${contract.id} · ${contract.tenant}`;
    $('#drawerSummary').innerHTML = `<div class="drawer-summary"><span class="tenant-avatar large">${contract.initials}</span><div><strong>${escapeHtml(contract.tenant)}</strong><small>${escapeHtml(contract.product)}</small></div>${badge(statusLabel, statusKind)}</div>`;
    const fields = entries => `<dl class="detail-grid">${entries.map(([label, value]) => `<div><dt>${escapeHtml(label)}</dt><dd>${value}</dd></div>`).join('')}</dl>`;
    if (state.detail === 'rights') {
      $('#drawerBody').innerHTML = `<div class="detail-section"><div class="section-title"><h3>حق استفادهٔ قراردادی</h3>${badge('نمونهٔ محلی')}</div>${fields([['مصرف‌کننده', escapeHtml(contract.tenant)], ['محصول', escapeHtml(contract.product)], ['وضعیت ذخیره‌شده', badge(statusLabel, statusKind)], ['نسخهٔ قرارداد', fa(contract.contractVersion)], ['آغاز بازه', date(contract.start)], ['پایان بازه', date(contract.end)]])}<div class="detail-callout ${isInWindow(contract) ? 'info' : 'warning'}"><strong>${windowText(contract)}</strong><p>ارزیابی نمونه بر مبنای ${date(demoDate)} است. سپری‌شدن یا آغازنشدن بازه، وضعیت ذخیره‌شدهٔ قرارداد را خودکار تغییر نمی‌دهد.</p></div><div class="detail-callout info"><strong>واجد شرایط بودن، انتساب نقش نیست.</strong><p>قرارداد به‌تنهایی عضویت، نقش محصول یا مجوز مستقیم عملیات را ایجاد نمی‌کند.</p></div></div>`;
    } else if (state.detail === 'limits') {
      $('#drawerBody').innerHTML = `<div class="detail-section"><h3>حدود تخصیص نمونه</h3><div class="limit-card"><span>حد نرخ</span><strong>${fa(contract.rate)} <small>درخواست در دقیقه</small></strong><p>ظرفیت مجاز قراردادی</p></div><div class="limit-card"><span>سقف ماهانه</span><strong>${fa(contract.monthly)} <small>عملیات</small></strong><p>این مقدار موجودی باقی‌مانده نیست.</p></div><div class="limit-card"><span>ظرفیت قراردادی</span><strong>${fa(contract.seats)} <small>عضو</small></strong><p>هیچ شمارندهٔ مصرف زنده‌ای در نمونه وجود ندارد.</p></div>${fields([['revision تنظیمات تخصیص', fa(contract.configRevision)], ['مشاهدهٔ Gateway', badge(gatewayLabel, gatewayKind)]])}<div class="detail-callout info"><strong>مشاهدهٔ تخصیص با انتشار سیاست فرق دارد.</strong><p>تغییر سیاست در این پیش‌نمایش، وضعیت مشاهدهٔ Gateway یا حدود قراردادی را تغییر نمی‌دهد.</p></div></div>`;
    } else if (state.detail === 'policy') {
      $('#drawerBody').innerHTML = `<div class="detail-section"><div class="section-title"><h3>سیاست دسترسی</h3>${badge(contract.policyStatus === 'Published' ? 'منتشرشده' : 'بازنشسته', contract.policyStatus === 'Published' ? 'success' : 'neutral')}</div>${fields([['آخرین نسخهٔ سیاست', fa(contract.policyVersion)], ['مرجع سیاست در قرارداد', fa(contract.acceptedPolicyVersion)], ['بازبینی مدیریتی', fa(contract.managementRevision)], ['نقش لازم در سناریو', escapeHtml(contract.role)], ['مجوز مستقیم لازم', 'تغییر ساختار سازمان']])}<div class="detail-callout ${contract.acceptedPolicyVersion === contract.policyVersion ? 'info' : 'warning'}"><strong>${contract.acceptedPolicyVersion === contract.policyVersion ? 'مرجع سیاست در حق استفاده منطبق است.' : 'مرجع سیاست در حق استفاده منطبق نیست.'}</strong><p>انتشار نسخهٔ بعد، snapshot حق استفاده را تغییر نمی‌دهد. مرجع پذیرفته‌شده باید با سیاست منتشرشده تطبیق داشته باشد.</p></div><div class="detail-callout info"><strong>چهار نسخه با مسئولیت مستقل</strong><p>قرارداد ${fa(contract.contractVersion)} · سیاست ${fa(contract.policyVersion)} · تنظیمات ${fa(contract.configRevision)} · بازبینی ${fa(contract.managementRevision)}</p></div><div class="policy-rule"><span class="rule-dot"></span><div><strong>عضویت، نقش و grant مستقل‌اند.</strong><p>این سیاست نمایشی هیچ نقش یا مجوز واقعی ایجاد نمی‌کند.</p></div></div><div class="detail-actions"><button class="button primary" data-policy-action="publish">${contract.policyStatus === 'Retired' ? 'انتشار دوباره در نمونه' : 'پیش‌نمایش انتشار نسخهٔ بعد'}</button>${contract.policyStatus === 'Published' ? '<button class="button secondary" data-policy-action="retire">بازنشستگی در نمونه</button>' : ''}</div><p class="muted small">دلیل آخرین تغییر: ${escapeHtml(contract.reason)}</p></div>`;
    } else {
      $('#drawerBody').innerHTML = `<div class="detail-section"><h3>تاریخچهٔ همین قرارداد نمونه</h3><div class="timeline">${contract.history.map((entry, index) => `<div class="timeline-item"><span class="timeline-dot"></span><div><strong>${escapeHtml(entry)}</strong><small>رویداد نمونهٔ ${fa(index + 1)} · بدون audit عملیاتی</small></div></div>`).join('')}</div><div class="detail-callout info"><p>انتشار محلی و مشاهدهٔ Gateway دو رویداد مستقل‌اند. تاریخچهٔ نمونه شاهد اجرای عملیات نیست.</p></div></div>`;
    }
  }

  function openContract(id, trigger) {
    if (!state.contracts.some(contract => contract.id === id)) return;
    state.selectedId = id;
    setDetail('rights');
    showModal(drawer, trigger);
    $('#closeDrawer').focus();
  }

  function renderScenarioOptions() {
    $('#scenarioContract').innerHTML = state.contracts.map(contract => `<option value="${contract.id}">${escapeHtml(contract.name)} — ${statuses[contract.status][0]}</option>`).join('');
  }

  function applyPreset() {
    const preset = $('#scenarioPreset').value;
    $('#conditionMember').checked = true;
    $('#conditionRole').checked = preset !== 'no-role';
    $('#conditionGrant').checked = preset !== 'no-grant';
    $('#conditionEvidence').checked = preset !== 'missing';
    evaluateScenario();
  }

  function evaluateScenario(logEvent = false) {
    const contract = state.contracts.find(record => record.id === $('#scenarioContract').value) || state.contracts[0];
    const member = $('#conditionMember').checked;
    const role = $('#conditionRole').checked;
    const grant = $('#conditionGrant').checked;
    const evidence = $('#conditionEvidence').checked;
    let decision = 'Permit';
    let title = 'شرایط نمونه برقرار است';
    let reason = 'قرارداد در بازهٔ معتبر است و عضویت، نقش، مجوز مستقیم و اطلاعات تازه در این سناریو برقرارند.';
    if ($('#scenarioPreset').value === 'outside') {
      decision = 'NotApplicable'; title = 'محصول در دامنهٔ این سیاست نیست'; reason = 'محصول انتخاب‌شدهٔ نمونه با محصول هدف این سیاست منطبق نیست. این نتیجه اجازهٔ اجرا ایجاد نمی‌کند. عملیات غیرمجاز در همان محصول، نتیجهٔ Deny دارد.';
    } else if (!evidence) {
      decision = 'Indeterminate'; title = 'اطلاعات معتبر برای تصمیم کافی نیست'; reason = 'تازگی یا کفایت اطلاعات معتبر در سناریو برقرار نیست. ناتوانی در تعیین نتیجه، با رد صریح شرایط تفاوت دارد.';
    } else if (contract.status !== 'Active') {
      decision = 'Deny'; title = 'وضعیت قرارداد مانع دسترسی است'; reason = `وضعیت ذخیره‌شدهٔ قرارداد «${statuses[contract.status][0]}» است. انتشار سیاست یا مشاهدهٔ Gateway این شرط را جبران نمی‌کند.`;
    } else if (!isInWindow(contract)) {
      decision = 'Deny'; title = 'حق استفاده در بازهٔ معتبر نیست'; reason = `${windowText(contract)}؛ وضعیت ذخیره‌شدهٔ قرارداد همچنان فعال است. زمان ارزیابی نمونه: ${date(demoDate)}.`;
    } else if (contract.policyStatus !== 'Published') {
      decision = 'Deny'; title = 'سیاست منتشرشده در نمونه موجود نیست'; reason = 'سیاست این قرارداد در نمونه بازنشسته شده است؛ شرایط نقش یا مجوز به‌تنهایی اجازهٔ اجرا نمی‌دهند.';
    } else if (contract.acceptedPolicyVersion !== contract.policyVersion) {
      decision = 'Indeterminate'; title = 'مرجع سیاست در حق استفاده منطبق نیست'; reason = `snapshot قرارداد به سیاست ${fa(contract.acceptedPolicyVersion)} اشاره می‌کند، ولی سیاست ${fa(contract.policyVersion)} منتشر شده است؛ اطلاعات معتبر همسو نیست. انتشار محلی، مرجع پذیرفته‌شده یا نسخهٔ قرارداد را خودکار تغییر نمی‌دهد.`;
    } else if (!member || !role || !grant) {
      decision = 'Deny'; title = 'یکی از شرایط مستقل برقرار نیست';
      reason = !member ? 'عضویت فعال در سازمان مصرف‌کننده برقرار نیست.' : !role ? 'انتساب صریح نقش محصول برقرار نیست. قرارداد واجد شرایط بودن را مشخص می‌کند و نقش را خودکار ایجاد نمی‌کند.' : 'مجوز مستقیم تغییر ساختار سازمان برقرار نیست. داشتن نقش به‌تنهایی این مجوز را ایجاد نمی‌کند.';
    }
    const kind = { Permit: 'success', Deny: 'danger', Indeterminate: 'warning', NotApplicable: 'neutral' }[decision];
    const labels = [['قرارداد فعال و در بازه', contract.status === 'Active' && isInWindow(contract)], ['سیاست منتشرشده', contract.policyStatus === 'Published'], ['مرجع سیاست در حق استفاده', contract.acceptedPolicyVersion === contract.policyVersion], ['عضویت سازمان', member], ['انتساب نقش', role], ['مجوز مستقیم', grant], ['اطلاعات معتبر و تازه', evidence]];
    $('#decisionResult').innerHTML = `<div class="decision-heading"><span class="eyebrow">نتیجهٔ ارزیابی محلی</span>${badge('بدون اثر عملیاتی')}</div><div class="decision-status ${kind}"><span class="decision-symbol">${icon(decision === 'Permit' ? 'shield' : 'info')}</span><span><strong dir="ltr">${decision}</strong><small>${title}</small></span></div><p class="decision-reason">${escapeHtml(reason)}</p><div class="decision-context"><strong>${escapeHtml(contract.name)}</strong><small>قرارداد ${fa(contract.contractVersion)} · سیاست ${fa(contract.policyVersion)} · تنظیمات ${fa(contract.configRevision)}</small></div><h3 class="result-subtitle">شرایط همین سناریو</h3><div class="condition-checks">${labels.map(([label, valid]) => `<div class="condition-check ${valid ? 'met' : 'unmet'}"><span class="condition-dot"></span><span>${label}</span><small>${valid ? 'برقرار در نمونه' : 'برقرار نیست'}</small></div>`).join('')}</div><p class="muted small">این فهرست، گزارش گام‌های endpoint واقعی نیست.</p>${decision === 'Permit' ? `<div class="obligation-box"><strong>لازم برای اجرا</strong><ul><li>پذیرش تخصیص در Gateway</li><li>تطبیق سازمان مالک منبع در محصول</li><li>رعایت حدود قراردادی و ثبت معتبر عملیات</li></ul><p>این الزامات در پیش‌نمایش اجرا یا تکمیل نشده‌اند.</p></div>` : '<div class="detail-callout info"><p>این نتیجه در نمونه مجوز اجرا یا مصرف ایجاد نمی‌کند.</p></div>'}<div class="decision-footnote">مشاهدهٔ Gateway: ${badge(observations[contract.gateway][0], observations[contract.gateway][1])}<span>مشاهده، شاهد اجرای عملیات نیست.</span></div>`;
    $('#decisionResult').dataset.decision = decision;
    if (logEvent) {
      addHistory(`سناریوی ${decision} بررسی شد`, `${contract.name}؛ صرفاً ارزیابی محلی شرایط، بدون اجرا یا مصرف.`, contract.id, kind);
      toast(`نتیجهٔ نمونه: ${decision} — بدون اثر عملیاتی`);
    }
  }

  function renderArchitecture() {
    const node = architecture[state.node];
    $$('[data-node]').forEach(button => {
      const active = button.dataset.node === state.node;
      button.classList.toggle('active', active);
      button.setAttribute('aria-pressed', String(active));
    });
    $('#architectureDetail').innerHTML = `<div class="architecture-detail-heading"><div><span class="eyebrow">${node.tag}</span><h3>${node.title}</h3></div>${badge('مرز مسئولیت', 'info')}</div><p>${node.description}</p><ul class="architecture-points">${node.items.map(item => `<li><span class="rule-dot"></span>${item}</li>`).join('')}</ul><div class="detail-callout info">${node.note}</div>`;
  }

  function renderHistory() {
    $('#historyList').innerHTML = state.history.map(entry => `<article class="timeline-item"><span class="timeline-dot ${entry.kind}"></span><div><div class="timeline-heading"><strong>${escapeHtml(entry.title)}</strong><small>${escapeHtml(entry.time)}</small></div><p>${escapeHtml(entry.description)}</p><button class="text-button" data-open-contract="${entry.contractId}"><span dir="ltr">${entry.contractId}</span> · جزئیات قرارداد ${icon('arrow')}</button></div></article>`).join('');
  }

  function addHistory(title, description, contractId, kind = 'info') {
    state.history.unshift({ title, description, contractId, kind, time: 'اکنون · تغییر محلی نمونه' });
    renderHistory();
  }

  function renderPolicyDiff() {
    const contract = currentContract();
    const nextPolicyVersion = state.policyMode === 'publish' ? contract.policyVersion + 1 : contract.policyVersion;
    $('#policyDiff').innerHTML = `<div><span>نسخهٔ سیاست</span><strong>${fa(contract.policyVersion)} ${state.policyMode === 'publish' ? `← ${fa(nextPolicyVersion)}` : '· ثابت'}</strong></div><div><span>بازبینی مدیریتی</span><strong>${fa(state.expectedRevision)} ← ${fa(state.expectedRevision + 1)}</strong></div><p>${state.policyMode === 'publish' ? 'سیاست بعدی فقط در نمونه منتشر می‌شود. مرجع پذیرفته‌شده در حق استفاده همچنان نسخهٔ ' + fa(contract.acceptedPolicyVersion) + ' است؛ انتشار نسخهٔ بعد به‌تنهایی دسترسی ایجاد نمی‌کند.' : 'سیاست فقط در نمونه بازنشسته می‌شود.'} نسخهٔ قرارداد ${fa(contract.contractVersion)}، تنظیمات ${fa(contract.configRevision)} و مشاهدهٔ Gateway ثابت می‌مانند.</p>`;
  }

  function openPolicy(mode, trigger) {
    const contract = currentContract();
    if (!contract) return;
    state.policyMode = mode;
    state.expectedRevision = contract.managementRevision;
    $('#policyDialogTitle').textContent = mode === 'publish' ? 'پیش‌نمایش انتشار سیاست' : 'پیش‌نمایش بازنشستگی سیاست';
    $('#policyForm').reset();
    $('#policyError').hidden = true;
    renderPolicyDiff();
    showModal(policyDialog, trigger);
    $('#policyReason').focus();
  }

  function submitPolicy(event) {
    event.preventDefault();
    const contract = currentContract();
    const reason = $('#policyReason').value.trim();
    if (reason.length < 5) {
      $('#policyError').textContent = 'دلیل تغییر را با دست‌کم ۵ نویسه وارد کنید.';
      $('#policyError').hidden = false;
      $('#policyReason').focus();
      return;
    }
    // The checkbox illustrates a concurrent management revision, without changing policy content.
    if ($('#simulateConflict').checked) {
      contract.managementRevision += 1;
      $('#simulateConflict').checked = false;
      contract.history.push(`تعارض بازبینی ${fa(contract.managementRevision)} شبیه‌سازی شد؛ محتوای سیاست تغییر نکرد.`);
      addHistory('تعارض بازبینی شبیه‌سازی شد', `بازبینی ${fa(contract.managementRevision)} در نمونه ثبت شد؛ انتشار یا بازنشستگی انجام نشد.`, contract.id, 'warning');
    }
    if (contract.managementRevision !== state.expectedRevision) {
      const previous = state.expectedRevision;
      state.expectedRevision = contract.managementRevision;
      $('#policyError').textContent = `تعارض بازبینی: فرم بر پایهٔ ${fa(previous)} بود و نمونه به ${fa(contract.managementRevision)} رسیده است. سیاست اعمال نشد. تغییرات بر پایهٔ بازبینی تازه نمایش داده شده؛ برای تلاش دوباره، فرم را ارسال کنید.`;
      $('#policyError').hidden = false;
      renderPolicyDiff();
      renderDrawer();
      return;
    }
    const gatewayBefore = contract.gateway;
    if (state.policyMode === 'publish') {
      contract.policyVersion += 1;
      contract.policyStatus = 'Published';
    } else {
      contract.policyStatus = 'Retired';
    }
    contract.managementRevision += 1;
    contract.reason = reason;
    const action = state.policyMode === 'publish' ? 'منتشر' : 'بازنشسته';
    contract.history.push(`سیاست نسخهٔ ${fa(contract.policyVersion)} در نمونه ${action} شد؛ بازبینی ${fa(contract.managementRevision)}. دلیل: ${reason}`);
    addHistory(`سیاست در نمونه ${action} شد`, `نسخهٔ ${fa(contract.policyVersion)} · بازبینی ${fa(contract.managementRevision)}؛ مشاهدهٔ Gateway «${observations[gatewayBefore][0]}» باقی ماند. دلیل: ${reason}`, contract.id);
    policyDialog.close();
    renderContracts();
    renderDrawer();
    $('[data-policy-action="publish"]', drawer)?.focus();
    evaluateScenario();
    toast(`سیاست فقط در این پیش‌نمایش ${action} شد؛ مشاهدهٔ Gateway تغییر نکرد.`);
  }

  function exportDemo() {
    const sample = { description: 'دادهٔ نمایشی پیش‌نمایش قرارداد و سیاست فن‌آسا؛ بدون اتصال عملیاتی', isDemo: true, evaluatedAt: demoDate, contracts: state.contracts, localEvents: state.history };
    const url = URL.createObjectURL(new Blob([JSON.stringify(sample, null, 2)], { type: 'application/json;charset=utf-8' }));
    const link = document.createElement('a');
    link.href = url;
    link.download = 'fanasa-contract-policy-demo.json';
    document.body.append(link);
    link.click();
    link.remove();
    window.setTimeout(() => URL.revokeObjectURL(url), 1000);
    toast('خروجی JSON داده‌های نمونه آماده شد.');
  }

  function resetDemo() {
    if (policyDialog.open) policyDialog.close();
    if (drawer.open) drawer.close();
    state.contracts = structuredClone(initialContracts);
    state.history = structuredClone(initialHistory);
    state.selectedId = null;
    state.detail = 'rights';
    state.node = 'gateway';
    state.expectedRevision = null;
    clearFilters();
    renderStats();
    renderScenarioOptions();
    $('#scenarioPreset').value = 'permit';
    applyPreset();
    renderArchitecture();
    renderHistory();
    setView('contracts');
    toast('داده‌ها و شرایط نمونه به حالت اولیه برگشتند.');
  }

  document.addEventListener('click', event => {
    const navigation = event.target.closest('[data-view]');
    if (navigation) { setView(navigation.dataset.view, true); return; }
    const openButton = event.target.closest('[data-open-contract]');
    if (openButton) { openContract(openButton.dataset.openContract, openButton); return; }
    const statusButton = event.target.closest('[data-status]');
    if (statusButton) { state.status = statusButton.dataset.status; renderContracts(); return; }
    const detailButton = event.target.closest('[data-detail]');
    if (detailButton) { setDetail(detailButton.dataset.detail); return; }
    const nodeButton = event.target.closest('[data-node]');
    if (nodeButton) { state.node = nodeButton.dataset.node; renderArchitecture(); return; }
    const policyButton = event.target.closest('[data-policy-action]');
    if (policyButton) openPolicy(policyButton.dataset.policyAction, policyButton);
  });
  $('#search').addEventListener('input', renderContracts);
  $('#tenantFilter').addEventListener('change', renderContracts);
  $('#gatewayFilter').addEventListener('change', renderContracts);
  $('#clearFilters').addEventListener('click', clearFilters);
  $('#closeDrawer').addEventListener('click', () => drawer.close());
  $('#closePolicy').addEventListener('click', () => policyDialog.close());
  $('#cancelPolicy').addEventListener('click', () => policyDialog.close());
  $('#policyForm').addEventListener('submit', submitPolicy);
  $('#scenarioPreset').addEventListener('change', applyPreset);
  $('#scenarioContract').addEventListener('change', () => evaluateScenario());
  ['#conditionMember', '#conditionRole', '#conditionGrant', '#conditionEvidence'].forEach(selector => $(selector).addEventListener('change', () => evaluateScenario()));
  $('#evaluateScenario').addEventListener('click', () => evaluateScenario(true));
  $('#inspectDecision').addEventListener('click', () => {
    $('#scenarioContract').value = state.selectedId;
    drawer.close();
    setView('decision');
    evaluateScenario();
    $('#scenarioContract').focus();
  });
  $('#exportDemo').addEventListener('click', exportDemo);
  $('#resetDemo').addEventListener('click', resetDemo);
  $('#mobileMenu').addEventListener('click', () => {
    if (state.menuOpen) { closeMenu(true); return; }
    state.menuOpen = true;
    $('#sidebar').classList.add('mobile-open');
    $('#mobileBackdrop').hidden = false;
    $('#mobileMenu').setAttribute('aria-expanded', 'true');
    document.body.classList.add('menu-open');
    $('.nav-item.active').focus();
  });
  $('#mobileBackdrop').addEventListener('click', () => closeMenu(true));
  window.addEventListener('hashchange', () => setView(location.hash.slice(1), true));
  window.addEventListener('resize', () => { if (window.innerWidth > 900) closeMenu(); });
  $('.drawer-tabs').addEventListener('keydown', event => {
    if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
    const tabs = $$('[data-detail]');
    let index = tabs.findIndex(button => button.dataset.detail === state.detail);
    if (event.key === 'Home') index = 0;
    else if (event.key === 'End') index = tabs.length - 1;
    else index = (index + (event.key === 'ArrowLeft' ? 1 : -1) + tabs.length) % tabs.length;
    event.preventDefault();
    setDetail(tabs[index].dataset.detail, true);
  });
  document.addEventListener('keydown', event => {
    if (!state.menuOpen) return;
    if (event.key === 'Escape') { event.preventDefault(); closeMenu(true); return; }
    if (event.key === 'Tab') {
      const targets = $$('a[href], button:not([disabled])', $('#sidebar'));
      const first = targets[0];
      const last = targets.at(-1);
      if (event.shiftKey && document.activeElement === first) { event.preventDefault(); last.focus(); }
      else if (!event.shiftKey && document.activeElement === last) { event.preventDefault(); first.focus(); }
    }
  });
  [drawer, policyDialog].forEach(dialog => {
    dialog.addEventListener('click', event => {
      if (event.target !== dialog) return;
      const rect = dialog.getBoundingClientRect();
      if (event.clientX < rect.left || event.clientX > rect.right || event.clientY < rect.top || event.clientY > rect.bottom) dialog.close();
    });
    dialog.addEventListener('close', () => {
      const target = focusReturn.get(dialog);
      if (target?.isConnected && target.getClientRects().length && !target.closest('[hidden]')) {
        target.focus({ preventScroll: true });
      } else if (dialog === policyDialog && drawer.open) {
        ($('[data-policy-action="publish"]', drawer) || $('#closeDrawer')).focus({ preventScroll: true });
      } else {
        const replacement = $$('[data-open-contract]').find(button => button.dataset.openContract === state.selectedId && button.getClientRects().length && !button.closest('[hidden]'));
        (replacement || $('#main')).focus({ preventScroll: true });
      }
    });
  });

  renderStats();
  renderContracts();
  renderScenarioOptions();
  applyPreset();
  renderArchitecture();
  renderHistory();
  setView(location.hash.slice(1) || 'workbench');
})();
