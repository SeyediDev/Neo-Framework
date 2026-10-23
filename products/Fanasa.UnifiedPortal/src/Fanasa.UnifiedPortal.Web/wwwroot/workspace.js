(function () {
    'use strict';

    const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
    const subjectPattern = /^[0-9a-f]{64}$/;
    const centers = ['abrgan', 'karavan', 'kavan', 'peyvand', 'dana', 'bonyan', 'fanasa-cloud', 'asman', 'kerane', 'rayan', 'dejan', 'ayin', 'didban', 'tarazu',
        'ابرگان', 'کاروان', 'کاوان', 'پیوند', 'دانا', 'بنیان', 'ابر فن‌آسا', 'آسمان', 'کرانه', 'رایان', 'دژان', 'آیین', 'دیده‌بان', 'ترازو'];
    const clone = value => JSON.parse(JSON.stringify(value));
    const isOrganization = value => typeof value === 'string' && uuidPattern.test(value) && value !== '00000000-0000-0000-0000-000000000000';
    function textInput(value, label, min, max) {
        const text = typeof value === 'string' ? value.trim() : '';
        if (text.length < min || text.length > max || /[\u0000-\u0008\u000b\u000c\u000e-\u001f\u007f]/.test(text))
            throw new Error(label + ' باید بین ' + min + ' و ' + max + ' نویسه باشد.');
        return text;
    }
    function draftIdentity(value) {
        const id = textInput(value, 'شناسهٔ درخواست', 1, 128);
        if (!/^[a-zA-Z0-9][a-zA-Z0-9._:-]*$/.test(id)) throw new Error('شناسهٔ درخواست معتبر نیست.');
        return id;
    }
    function parseBootstrapJson(raw) {
        if (typeof raw !== 'string') throw new Error('دادهٔ فضای کار معتبر نیست.');
        let output = '', i = 0;
        while (i < raw.length) {
            if (raw[i] !== '"') { output += raw[i++]; continue; }
            const start = i++;
            while (i < raw.length) { if (raw[i] === '\\') { i += 2; continue; } if (raw[i++] === '"') break; }
            const literal = raw.slice(start, i); output += literal;
            if (!['amount', 'totalAmount', 'quantity'].includes(JSON.parse(literal))) continue;
            let next = i; while (next < raw.length && /\s/.test(raw[next])) next++;
            if (raw[next] !== ':') continue;
            next++; while (next < raw.length && /\s/.test(raw[next])) next++;
            const number = /^-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?/.exec(raw.slice(next));
            if (!number) continue;
            output += raw.slice(i, next) + JSON.stringify(number[0]); i = next + number[0].length;
        }
        return JSON.parse(output);
    }
    function decimalText(value) {
        if (typeof value === 'number') {
            if (!Number.isSafeInteger(value) || value < 0) throw new Error('مقدار دقیق در دسترس نیست.');
            value = String(value);
        }
        if (typeof value !== 'string' || value.length > 200 || !/^(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?$/.test(value))
            throw new Error('مقدار اعشاری معتبر نیست.');
        const parts = value.toLowerCase().split('e'); if (parts.length === 1) return value;
        const exponent = Number(parts[1]);
        if (!Number.isInteger(exponent) || Math.abs(exponent) > 100) throw new Error('مقدار خارج از محدوده است.');
        const [whole, fraction = ''] = parts[0].split('.'), digits = whole + fraction, point = whole.length + exponent;
        if (point <= 0) return '0.' + '0'.repeat(-point) + digits;
        const result = point >= digits.length ? digits + '0'.repeat(point - digits.length) : digits.slice(0, point) + '.' + digits.slice(point);
        return result.replace(/^0+(?=\d)/, '');
    }
    function formatDecimal(value) {
        const [whole, fraction] = decimalText(value).split('.');
        return whole.replace(/\B(?=(\d{3})+(?!\d))/g, ',') + (fraction === undefined ? '' : '.' + fraction);
    }
    function safeServiceUrl(value) {
        if (typeof value !== 'string' || value.length > 2048 || value !== value.trim()) return null;
        try {
            const url = new URL(value);
            return ['https:', 'http:'].includes(url.protocol) && !url.username && !url.password ? url.href : null;
        } catch { return null; }
    }
    function storageScope(bootstrapOrSubject, organizationId) {
        const subjectKey = typeof bootstrapOrSubject === 'object' && bootstrapOrSubject !== null ? bootstrapOrSubject.subjectKey : bootstrapOrSubject;
        const consumer = typeof bootstrapOrSubject === 'object' && bootstrapOrSubject !== null ? bootstrapOrSubject.organizationId : organizationId;
        if (!subjectPattern.test(subjectKey || '') || !isOrganization(consumer)) throw new Error('سازمان خود را انتخاب کنید.');
        return 'fanasa.workspace.preview.v1.' + subjectKey + '.' + consumer.toLowerCase();
    }
    function validateRequestDraft(bootstrap, draft) {
        if (!draft) throw new Error('درخواست خدمت معتبر نیست.');
        const service = (bootstrap.catalog || []).find(item => item.id === draft.serviceId);
        const plan = service?.plans?.find(item => item.id === draft.planId);
        if (!service || !plan || service.status !== 'available') throw new Error('خدمت یا طرح انتخاب‌شده در فهرست جاری موجود نیست.');
        return Object.freeze({ id: draftIdentity(draft.id), serviceId: service.id, planId: plan.id, reason: textInput(draft.reason, 'دلیل درخواست', 1, 1000) });
    }
    function validatePublicationDraft(bootstrap, draft) {
        if (!draft) throw new Error('پیشنهاد انتشار معتبر نیست.');
        const center = textInput(draft.center, 'مرکز قابلیت', 1, 80);
        const officialCenters = Array.isArray(bootstrap.centers) ? bootstrap.centers.map(item => item.id) : centers;
        if (!officialCenters.includes(center) && !(bootstrap.catalog || []).some(item => item.center === center)) throw new Error('مرکز قابلیت را از فهرست انتخاب کنید.');
        const url = safeServiceUrl(textInput(draft.url, 'نشانی خدمت', 1, 2048));
        if (!url) throw new Error('نشانی کامل و معتبر خدمت را با http یا https وارد کنید.');
        const result = { id: draftIdentity(draft.id), name: textInput(draft.name || draft.displayName, 'نام خدمت', 2, 120), center,
            description: textInput(draft.description, 'توضیح خدمت', 10, 2000), url };
        if (draft.key) {
            result.key = textInput(draft.key, 'کلید خدمت', 2, 80);
            if (!/^[a-z0-9][a-z0-9._-]+$/.test(result.key)) throw new Error('کلید خدمت فقط باید شامل حروف کوچک لاتین، عدد، نقطه، خط تیره یا زیرخط باشد.');
        }
        return Object.freeze(result);
    }
    function selectCatalog(catalog, query, center) {
        const normalize = value => String(value || '').normalize('NFKC').toLocaleLowerCase('fa')
            .replace(/[يى]/g, 'ی').replace(/ك/g, 'ک').replace(/[\u0640\u064b-\u065f\u0670\u06d6-\u06ed]/g, '')
            .replace(/[\u200c\u200d]/g, ' ').replace(/\s+/g, ' ').trim();
        const terms = normalize(query).slice(0, 200).split(' ').filter(Boolean);
        return (catalog || []).filter(item => (!center || center === 'all' || item.center === center)
            && terms.every(term => normalize([item.name || item.displayName, item.key, item.center, item.description].join(' ')).includes(term)));
    }
    function publishedActions(service) {
        const actions = [], details = safeServiceUrl(service.detailsUrl);
        if (details) actions.push({ label: 'معرفی و جزئیات', url: details, kind: 'details' });
        const launch = service.accessState === 'allowed' ? safeServiceUrl(service.launchUrl) : null;
        const request = service.accessState !== 'allowed' ? safeServiceUrl(service.requestAccessUrl) : null;
        if (launch) actions.push({ label: 'ورود به سامانه', url: launch, kind: 'launch' });
        else if (request) actions.push({ label: 'درخواست دسترسی / خرید', url: request, kind: 'request' });
        return actions;
    }
    function createPreviewSession(bootstrap, options) {
        options = options || {};
        const base = clone(bootstrap), storage = options.storage || null;
        const nextId = options.idFactory || (() => globalThis.crypto.randomUUID());
        const now = options.now || (() => new Date().toISOString());
        let state = { requests: clone(base.requests || []), publications: clone(base.publications || []), drafts: [] }, warning = '';
        const preview = base.mode === 'synthetic-preview';
        let scope = null;
        if (preview) try { scope = storageScope(base); } catch { warning = 'سازمان خود را انتخاب کنید.'; }
        function ensurePreview() {
            if (!preview) throw new Error('این اقدام در اتصال واقعی فعال نیست؛ هیچ درخواست یا دسترسی واقعی تغییر نمی‌کند.');
            if (base.catalogStatus !== 'ready') throw new Error('فضای کار سازمان دریافت نشده است. پیش از اقدام، سازمان را انتخاب و صفحه را تازه کنید.');
            storageScope(base);
        }
        function ownEntity(item) { return item && item.organizationId === base.organizationId; }
        if (preview && scope) {
            state.requests = state.requests.filter(ownEntity); state.publications = state.publications.filter(ownEntity);
            try {
                const raw = storage?.getItem(scope);
                if (raw) {
                    if (raw.length > 600000) throw new Error('حجم دادهٔ پیش‌نمایش بیش از حد است.');
                    const saved = parseBootstrapJson(raw);
                    if (saved.subjectKey !== base.subjectKey || saved.organizationId !== base.organizationId || saved.version !== 1
                        || !Array.isArray(saved.requests) || !Array.isArray(saved.publications) || !Array.isArray(saved.drafts)
                        || saved.requests.length > 100 || saved.publications.length > 100 || saved.drafts.length > 200
                        || [...saved.requests, ...saved.publications].some(item => !ownEntity(item) || !['pending', 'approved', 'rejected'].includes(item.status)
                            || typeof item.id !== 'string' || !Array.isArray(item.history) || item.history.length > 10))
                        throw new Error('دادهٔ محفوظ پیش‌نمایش معتبر نیست.');
                    state = { requests: saved.requests, publications: saved.publications, drafts: saved.drafts };
                }
            } catch { warning = 'اطلاعات ذخیره‌شده خوانده نشد.'; }
        }
        function persist() {
            try { storage?.setItem(scope, JSON.stringify({ version: 1, subjectKey: base.subjectKey, organizationId: base.organizationId, ...state })); }
            catch { warning = 'مرورگر تغییرات را ذخیره نکرد.'; }
        }
        function snapshot() {
            const activeServices = clone(base.activeServices || []);
            if (preview && scope && base.catalogStatus === 'ready') for (const request of state.requests.filter(item => ownEntity(item) && item.status === 'approved')) {
                const service = (base.catalog || []).find(item => item.id === request.serviceId);
                if (service && !activeServices.some(item => item.key === service.key)) activeServices.push({ id: service.id, key: service.key,
                    name: service.name, center: service.center, url: null, simulated: true });
            }
            return clone({ ...base, requests: state.requests, publications: state.publications, activeServices, storageWarning: warning });
        }
        function existingDraft(kind, draft) {
            const fingerprint = JSON.stringify(draft), previous = state.drafts.find(item => item.id === draft.id);
            if (!previous) return { fingerprint, entity: null };
            if (previous.kind !== kind || previous.fingerprint !== fingerprint) throw new Error('شناسهٔ این پیش‌نویس قبلاً با اطلاعات دیگری ثبت شده است. پیش‌نویس تازه باز کنید.');
            const entity = (kind === 'request' ? state.requests : state.publications).find(item => item.id === previous.entityId);
            if (!entity) throw new Error('نتیجهٔ پیش‌نویس محفوظ پیدا نشد.');
            return { fingerprint, entity };
        }
        function review(kind, id, decision) {
            ensurePreview(); if (base.isAdmin !== true) throw new Error('اجازهٔ بررسی درخواست ندارید.');
            if (!['approved', 'rejected'].includes(decision)) throw new Error('نتیجهٔ بررسی معتبر نیست.');
            const entity = (kind === 'request' ? state.requests : state.publications).find(item => item.id === id && ownEntity(item));
            if (!entity) throw new Error('درخواست در سازمان جاری پیدا نشد.');
            if (entity.status === decision) return clone(entity);
            if (entity.status !== 'pending') throw new Error('نتیجهٔ بررسی قبلاً ثبت شده است.');
            entity.status = decision; entity.history.push({ status: decision, label: decision === 'approved' ? 'درخواست تأیید شد' : 'درخواست رد شد', at: now() });
            persist(); return clone(entity);
        }
        return {
            snapshot,
            request(input) {
                ensurePreview(); const draft = validateRequestDraft(base, { ...input, id: input?.id || nextId() });
                const previous = existingDraft('request', draft); if (previous.entity) return clone(previous.entity);
                if (state.requests.length >= 100 || state.drafts.length >= 200) throw new Error('ظرفیت پیش‌نمایش تکمیل است.');
                if (state.requests.some(item => ownEntity(item) && item.serviceId === draft.serviceId && ['pending', 'approved'].includes(item.status)))
                    throw new Error('برای این خدمت درخواست فعال وجود دارد. وضعیت آن را در «درخواست‌های من» پیگیری کنید.');
                const service = base.catalog.find(item => item.id === draft.serviceId), plan = service.plans.find(item => item.id === draft.planId), at = now();
                const entity = { id: draft.id, serviceId: service.id, serviceName: service.name, planId: plan.id, planName: plan.name,
                    organizationId: base.organizationId, status: 'pending', createdAt: at, reason: draft.reason,
                    history: [{ status: 'pending', label: 'درخواست ثبت شد', at }] };
                state.requests.push(entity); state.drafts.push({ id: draft.id, kind: 'request', fingerprint: previous.fingerprint, entityId: entity.id });
                persist(); return clone(entity);
            },
            publish(input) {
                ensurePreview(); const draft = validatePublicationDraft(base, { ...input, id: input?.id || nextId() });
                const previous = existingDraft('publication', draft); if (previous.entity) return clone(previous.entity);
                if (state.publications.length >= 100 || state.drafts.length >= 200) throw new Error('ظرفیت پیش‌نمایش تکمیل است.');
                const at = now(), entity = { ...draft, organizationId: base.organizationId, status: 'pending', createdAt: at,
                    history: [{ status: 'pending', label: 'درخواست عرضه ثبت شد', at }] };
                state.publications.push(entity); state.drafts.push({ id: draft.id, kind: 'publication', fingerprint: previous.fingerprint, entityId: entity.id });
                persist(); return clone(entity);
            },
            reviewRequest(id, decision) { return review('request', id, decision); },
            reviewPublication(id, decision) { return review('publication', id, decision); },
            reset() {
                ensurePreview(); state = { requests: clone(base.requests || []).filter(ownEntity), publications: clone(base.publications || []).filter(ownEntity), drafts: [] };
                warning = ''; try { storage?.removeItem(scope); } catch { warning = 'پاک‌کردن دادهٔ محفوظ پیش‌نمایش انجام نشد.'; }
                return snapshot();
            }
        };
    }
    const exported = { parseBootstrapJson, decimalText, formatDecimal, safeServiceUrl, storageScope,
        validateRequestDraft, validatePublicationDraft, selectCatalog, publishedActions, createPreviewSession };
    if (typeof module !== 'undefined' && module.exports) module.exports = exported;
    if (typeof document === 'undefined') return;
    const root = document.getElementById('service-workspace'); if (!root) return;
    root.setAttribute('aria-busy', 'true');

    const get = id => document.getElementById(id);
    const ids = ['workspace-message', 'workspace-refresh', 'workspace-services-count', 'workspace-requests-count',
        'workspace-consumption-amount', 'workspace-consumption-caption', 'workspace-publish-count', 'catalog-search', 'catalog-center',
        'catalog-grid', 'catalog-empty', 'request-status', 'request-list', 'requests-empty', 'active-services', 'services-empty',
        'usage-period', 'usage-summary', 'usage-rows', 'usage-empty', 'usage-table-container', 'usage-table-note', 'publish-form',
        'publish-center', 'publish-submit', 'publish-message', 'publication-list', 'publications-empty', 'review-status', 'review-list',
        'review-empty', 'service-request-dialog', 'request-dialog-title', 'request-dialog-description', 'request-dialog-summary',
        'service-request-form', 'request-form-plan', 'request-form-reason', 'request-dialog-submit', 'request-dialog-message',
        'request-detail-dialog', 'request-detail-title', 'request-detail-body'];
    const ui = Object.fromEntries(ids.map(id => [id, get(id)]));
    const tabs = Array.from(root.querySelectorAll('.workspace-tabs [role=tab]'));
    const panels = Array.from(root.querySelectorAll('[data-workspace-panel]'));
    let bootstrap, session, preview = false, writesAllowed = false, requestDraft = null, requestSubmitted = false;
    let publicationDraftId = null, publicationSubmitted = false, submitting = false, currentTab = 'catalog';
    const dialogOpeners = new Map();
    const countFormat = new Intl.NumberFormat('fa-IR');
    const dateFormat = new Intl.DateTimeFormat('fa-IR', { dateStyle: 'medium', timeStyle: 'short' });
    function node(tag, className, value) {
        const result = document.createElement(tag); if (className) result.className = className;
        if (value !== undefined) result.textContent = String(value); return result;
    }
    function message(value, type, target) {
        const area = target || ui['workspace-message'];
        area.textContent = value; area.classList.remove('is-error', 'is-success');
        if (type) area.classList.add('is-' + type);
    }
    function resultMessage(value, target) {
        const warning = session.snapshot().storageWarning;
        message(warning ? value + ' ' + warning : value, warning ? 'error' : 'success', target);
    }
    function displayDate(value) { return value && Number.isFinite(Date.parse(value)) ? dateFormat.format(new Date(value)) : 'زمان ثبت نشده است'; }
    function amount(value) { try { return formatDecimal(value); } catch { return 'مقدار دقیق در دسترس نیست'; } }
    function currency(value) { return value === 'IRR' ? 'ریال' : String(value || ''); }
    function newDraftId() {
        if (typeof globalThis.crypto?.randomUUID !== 'function') throw new Error('مرورگر شناسهٔ امن پیش‌نویس را پشتیبانی نمی‌کند.');
        return globalThis.crypto.randomUUID();
    }
    function setFormEnabled(form, enabled) { for (const control of form.querySelectorAll('input, select, textarea, button[type=submit]')) control.disabled = !enabled; }
    function showEmpty(id, hidden, title, description) {
        const area = ui[id]; area.hidden = hidden;
        if (title) area.querySelector('h3').textContent = title;
        if (description) area.querySelector('p').textContent = description;
    }
    function activateTab(name, focus) {
        const active = tabs.find(tab => tab.dataset.workspaceTab === name && !tab.hidden); if (!active) return;
        currentTab = name;
        for (const tab of tabs) { const selected = tab === active; tab.setAttribute('aria-selected', String(selected)); tab.tabIndex = selected ? 0 : -1; }
        for (const panel of panels) panel.hidden = panel.dataset.workspacePanel !== name;
        history.replaceState(null, '', '#' + name); if (focus) active.focus();
    }
    root.addEventListener('click', event => {
        const button = event.target.closest?.('[data-workspace-tab]');
        if (button) activateTab(button.dataset.workspaceTab, button.getAttribute('role') !== 'tab');
        const close = event.target.closest?.('[data-close-dialog]'); if (close) close.closest('dialog').close();
    });
    for (const tab of tabs) tab.addEventListener('keydown', event => {
        const visible = tabs.filter(item => !item.hidden), index = visible.indexOf(tab);
        const next = event.key === 'ArrowLeft' ? (index + 1) % visible.length : event.key === 'ArrowRight' ? (index - 1 + visible.length) % visible.length
            : event.key === 'Home' ? 0 : event.key === 'End' ? visible.length - 1 : -1;
        if (next < 0) return; event.preventDefault(); activateTab(visible[next].dataset.workspaceTab, true);
    });
    for (const dialog of [ui['service-request-dialog'], ui['request-detail-dialog']]) dialog.addEventListener('close', () => {
        const opener = dialogOpeners.get(dialog);
        const fallback = tabs.find(tab => tab.dataset.workspaceTab === currentTab);
        (opener?.isConnected && !opener.disabled ? opener : fallback)?.focus();
    });
    for (const dialog of [ui['service-request-dialog'], ui['request-detail-dialog']]) dialog.addEventListener('keydown', event => {
        if (event.key !== 'Tab' || !dialog.open) return;
        const controls = Array.from(dialog.querySelectorAll('a[href], button, input, select, textarea, [tabindex]'))
            .filter(control => !control.disabled && control.tabIndex >= 0 && control.getClientRects().length > 0);
        if (!controls.length) { event.preventDefault(); return; }
        const index = controls.indexOf(document.activeElement);
        if (event.shiftKey && index <= 0) { event.preventDefault(); controls[controls.length - 1].focus(); }
        else if (!event.shiftKey && (index < 0 || index === controls.length - 1)) { event.preventDefault(); controls[0].focus(); }
    });
    function openDialog(dialog, opener) { dialogOpeners.set(dialog, opener || document.activeElement); if (!dialog.open) dialog.showModal(); }
    function centerName(value) { return bootstrap.centers?.find(center => center.id === value)?.name || value; }
    function serviceMark(center) {
        const mark = node('span', 'workspace-service-mark'), official = bootstrap.centers?.find(item => item.id === center);
        if (official && typeof official.logo === 'string' && /^\/brand\/center-[a-z-]+-mark-color\.svg$/.test(official.logo)) {
            const image = node('img'); image.src = official.logo; image.alt = 'نشان ' + official.name; image.width = 28; image.height = 28; mark.append(image);
        } else mark.append(node('span', '', '◇'));
        return mark;
    }
    function button(label, className) { const control = node('button', className || 'button button-secondary', label); control.type = 'button'; return control; }
    function fact(container, label, value) { const part = node('div'); part.append(node('span', '', label), node('strong', '', value)); container.append(part); }
    function badge(status) {
        const names = { pending: 'در انتظار بررسی', approved: 'تأییدشده', rejected: 'ردشده' };
        return node('span', 'workspace-status-pill is-' + status, names[status] || 'وضعیت نامشخص');
    }
    function centerOptions() {
        const definitions = Array.isArray(bootstrap.centers) ? bootstrap.centers : Array.from(new Set(bootstrap.catalog.map(item => item.center))).map(id => ({ id, name: id }));
        for (const select of [ui['catalog-center'], ui['publish-center']]) {
            const selected = select.value;
            while (select.options.length > 1) select.remove(1);
            for (const center of definitions) { const option = node('option', '', center.name); option.value = center.id; select.append(option); }
            if (definitions.some(center => center.id === selected)) select.value = selected;
        }
    }
    function renderCatalog() {
        const data = session.snapshot(); ui['catalog-grid'].replaceChildren();
        if (!preview) { renderPublishedCatalog(); return; }
        const available = bootstrap.catalogStatus === 'ready' ? selectCatalog(data.catalog, ui['catalog-search'].value, ui['catalog-center'].value) : [];
        for (const service of available) {
            const card = node('article', 'workspace-service-card'), top = node('div', 'workspace-card-top');
            top.append(serviceMark(service.center), node('span', 'workspace-chip', centerName(service.center)));
            card.append(top, node('h3', '', service.name), node('p', 'workspace-service-description', service.description));
            const facts = node('div', 'workspace-service-facts'), first = service.plans[0];
            fact(facts, 'مرکز قابلیت', centerName(service.center));
            fact(facts, first ? first.name : 'طرح خدمت', first ? amount(first.amount) + ' ' + currency(first.currency) + ' / ' + first.interval : 'طرح در دسترس نیست');
            const footer = node('div', 'workspace-card-footer'), action = button('انتخاب طرح', 'button button-secondary');
            action.dataset.offerId = service.id; action.disabled = !writesAllowed || !first;
            footer.append(action); card.append(facts, footer);
            ui['catalog-grid'].append(card);
        }
        showEmpty('catalog-empty', available.length > 0, data.catalog.length && bootstrap.catalogStatus === 'ready' ? 'خدمتی با این جست‌وجو پیدا نشد' : 'کاتالوگ خدمات آمادهٔ نمایش نیست',
            data.catalog.length && bootstrap.catalogStatus === 'ready' ? 'عبارت دیگری را امتحان کنید یا مرکز قابلیت را تغییر دهید.' : 'خدمات مجاز فعلی را در بخش «سرویس‌های فعال» ببینید.');
    }
    function renderPublishedCatalog() {
        const catalog = bootstrap.publishedCatalog, ready = catalog?.status === 'Ready';
        const services = ready ? selectCatalog(catalog.services, ui['catalog-search'].value, ui['catalog-center'].value) : [];
        const notice = get('catalog-connection-notice');
        notice.hidden = ready;
        notice.querySelector('p').textContent = catalog?.message || 'کاتالوگ خدمات دریافت نشده است.';
        for (const service of services) {
            const card = node('article', 'workspace-service-card'), top = node('div', 'workspace-card-top');
            top.append(serviceMark(service.center), node('span', 'workspace-chip', centerName(service.center)));
            card.append(top, node('h3', '', service.displayName), node('p', 'workspace-service-description', service.description));
            const labels = { allowed: 'دسترسی فعال', requestRequired: 'نیازمند درخواست دسترسی',
                selectOrganization: 'سازمان خود را انتخاب کنید', signInRequired: 'برای بررسی دسترسی وارد شوید', unverified: 'دسترسی ورود تأیید نشده است' };
            card.append(node('p', 'workspace-table-note', labels[service.accessState] || labels.unverified));
            const footer = node('div', 'workspace-card-footer'), actions = publishedActions(service);
            for (const action of actions) {
                const link = node('a', action.kind === 'details' ? 'workspace-inline-link' : 'button button-secondary', action.label + ' ↗');
                link.href = action.url; link.target = '_blank'; link.rel = 'noopener noreferrer';
                link.setAttribute('aria-label', action.label + ' ' + service.displayName);
                footer.append(link);
            }
            if (!actions.some(action => action.kind !== 'details')) footer.append(node('small', '', service.accessState === 'allowed'
                ? 'نشانی ورود هنوز آماده نیست.' : 'مسیر درخواست دسترسی هنوز منتشر نشده است.'));
            card.append(footer); ui['catalog-grid'].append(card);
        }
        showEmpty('catalog-empty', services.length > 0,
            !ready ? 'کاتالوگ خدمات دریافت نشد' : catalog.services.length ? 'خدمتی با این فیلترها پیدا نشد' : 'هنوز خدمتی منتشر نشده است',
            !ready ? 'تازه‌سازی کنید یا سرویس‌های فعال خود را ببینید.' : catalog.services.length
                ? 'عبارت جست‌وجو یا مرکز قابلیت را تغییر دهید.' : 'خدمات پس از انتشار در مرکز راهبری در اینجا نمایش داده می‌شوند.');
    }
    function trackingCard(item, kind, review) {
        const card = node('article', 'workspace-request-card'), main = node('div', 'workspace-request-main'), title = node('div', 'workspace-request-title');
        title.append(node('h3', '', kind === 'request' ? item.serviceName : item.name), badge(item.status));
        main.append(title, node('p', '', kind === 'request' ? 'طرح ' + item.planName + ' · ' + item.reason : centerName(item.center) + ' · ' + item.description));
        const meta = node('div', 'workspace-request-meta'); meta.append(node('span', '', displayDate(item.createdAt)), node('span', '', kind === 'request' ? 'درخواست استفاده' : 'پیشنهاد عرضه'));
        main.append(meta); const actions = node('div', 'workspace-request-action'), details = button('پیگیری درخواست');
        details.dataset.requestId = item.id; details.dataset.requestKind = kind; actions.append(details);
        if (review && item.status === 'pending') for (const decision of ['approved', 'rejected']) {
            const control = button(decision === 'approved' ? 'تأیید' : 'رد', 'button button-secondary' + (decision === 'rejected' ? ' workspace-reject' : ''));
            control.dataset.reviewId = item.id; control.dataset.reviewKind = kind; control.dataset.reviewAction = decision;
            control.disabled = !writesAllowed || bootstrap.isAdmin !== true; actions.append(control);
        }
        card.append(main, actions); return card;
    }
    function renderRequests() {
        const data = session.snapshot(), filter = ui['request-status'].value;
        const requests = data.requests.filter(item => item.organizationId === bootstrap.organizationId && (!filter || item.status === filter));
        ui['request-list'].replaceChildren(); for (const item of requests.slice().reverse()) ui['request-list'].append(trackingCard(item, 'request', false));
        showEmpty('requests-empty', requests.length > 0, filter ? 'درخواستی با این وضعیت وجود ندارد' : 'درخواستی برای نمایش وجود ندارد', 'درخواست‌های همین سازمان و نتیجهٔ بررسی آن‌ها در این بخش نمایش داده می‌شود.');
        const publications = data.publications.filter(item => item.organizationId === bootstrap.organizationId);
        ui['publication-list'].replaceChildren(); for (const item of publications.slice().reverse()) ui['publication-list'].append(trackingCard(item, 'publication', false));
        ui['publications-empty'].hidden = publications.length > 0;
        if (ui['review-list']) {
            const reviewFilter = ui['review-status'].value, reviewItems = [...data.requests.map(item => ({ ...item, kind: 'request' })), ...data.publications.map(item => ({ ...item, kind: 'publication' }))]
                .filter(item => item.organizationId === bootstrap.organizationId && (!reviewFilter || item.status === reviewFilter));
            ui['review-list'].replaceChildren(); if (bootstrap.isAdmin === true) for (const item of reviewItems.slice().reverse()) ui['review-list'].append(trackingCard(item, item.kind, true));
            ui['review-empty'].hidden = bootstrap.isAdmin === true && reviewItems.length > 0;
        }
    }
    function renderServices() {
        const data = session.snapshot(); ui['active-services'].replaceChildren();
        for (const service of data.activeServices) {
            const card = node('article', 'workspace-service-card'), top = node('div', 'workspace-card-top');
            top.append(serviceMark(service.center), node('span', 'workspace-status-pill is-active', 'فعال'));
            card.append(top, node('h3', '', service.name), node('p', 'workspace-service-description', centerName(service.center)));
            const footer = node('div', 'workspace-card-footer'), url = !preview && !service.simulated ? safeServiceUrl(service.url) : null;
            if (url) { const link = node('a', 'button button-secondary', 'ورود به سامانه ↗'); link.href = url; link.target = '_blank'; link.rel = 'noopener noreferrer'; footer.append(link); }
            else footer.append(node('small', '', 'ورود به سرویس در دسترس نیست.'));
            card.append(footer); ui['active-services'].append(card);
        }
        ui['services-empty'].hidden = data.activeServices.length > 0;
    }
    function renderUsage() {
        const usage = bootstrap.usage, ready = usage?.status === 'ready';
        ui['usage-summary'].replaceChildren(); ui['usage-rows'].replaceChildren();
        ui['usage-table-container'].hidden = !ready || usage.items.length === 0; ui['usage-empty'].hidden = ready && usage.items.length > 0;
        ui['usage-period'].disabled = !ready;
        const previous = ui['usage-period'].querySelector('option[value=previous]'); if (previous) { previous.disabled = true; previous.textContent = 'ماه قبل · گزارش در دسترس نیست'; }
        if (!ready) {
            showEmpty('usage-empty', false, 'گزارش مصرف در دسترس نیست', 'دوباره تلاش کنید.');
            ui['usage-table-note'].textContent = ''; return;
        }
        ui['usage-period'].options[0].textContent = usage.periodLabel;
        for (const [label, value, description] of [['هزینهٔ ثبت‌شده', amount(usage.totalAmount) + ' ' + currency(usage.currency), usage.periodLabel],
            ['سرویس‌های دارای مصرف', countFormat.format(usage.items.length), 'در سازمان ' + bootstrap.organizationName], ['آخرین گزارش', displayDate(usage.asOf), usage.periodLabel]]) {
            const stat = node('div', 'workspace-usage-stat'); stat.append(node('span', '', label), node('strong', '', value), node('small', '', description)); ui['usage-summary'].append(stat);
        }
        for (const item of usage.items) {
            const row = node('tr'), quantity = node('td', 'workspace-amount', amount(item.quantity));
            quantity.append(node('small', 'workspace-table-secondary', 'سقف طرح: ' + item.quota));
            row.append(node('td', '', item.name), node('td', '', item.unit), quantity, node('td', 'workspace-amount', amount(item.amount) + ' ' + currency(usage.currency)), node('td', '', usage.periodLabel));
            ui['usage-rows'].append(row);
        }
        ui['usage-table-note'].textContent = '';
    }
    function renderMetrics() {
        const data = session.snapshot(), usage = data.usage, scoped = bootstrap.catalogStatus === 'ready' && isOrganization(bootstrap.organizationId);
        ui['workspace-services-count'].textContent = scoped ? countFormat.format(data.activeServices.length) : '—';
        ui['workspace-requests-count'].textContent = preview && scoped ? countFormat.format(data.requests.filter(item => item.organizationId === bootstrap.organizationId && item.status === 'pending').length) : '—';
        ui['workspace-publish-count'].textContent = preview && scoped ? countFormat.format(data.publications.filter(item => item.organizationId === bootstrap.organizationId).length) : '—';
        ui['workspace-consumption-amount'].textContent = usage?.status === 'ready' ? amount(usage.totalAmount) : '—';
        ui['workspace-consumption-caption'].textContent = usage?.status === 'ready' ? currency(usage.currency) + ' · ' + usage.periodLabel
            : 'گزارش مصرف در دسترس نیست';
    }
    function renderAll() {
        renderCatalog(); renderRequests(); renderServices(); renderUsage(); renderMetrics();
        const link = get('central-accounting-link'), note = get('central-accounting-note');
        const url = !preview ? safeServiceUrl(bootstrap.accountingUrl) : null;
        if (link) { link.hidden = !url; if (url) link.href = url; else link.removeAttribute('href'); }
        if (note) note.hidden = !url;
    }
    function summaryPart(container, label, value) { const part = node('div'); part.append(node('dt', '', label), node('dd', '', value)); container.append(part); }
    function renderPlanSummary() {
        const service = bootstrap.catalog.find(item => item.id === requestDraft?.serviceId), plan = service?.plans.find(item => item.id === ui['request-form-plan'].value);
        ui['request-dialog-summary'].replaceChildren(); if (!service || !plan) return;
        summaryPart(ui['request-dialog-summary'], 'سازمان', bootstrap.organizationName); summaryPart(ui['request-dialog-summary'], 'مرکز قابلیت', centerName(service.center));
        summaryPart(ui['request-dialog-summary'], 'هزینهٔ طرح ' + plan.interval, amount(plan.amount) + ' ' + currency(plan.currency)); summaryPart(ui['request-dialog-summary'], 'سقف طرح', plan.quota);
    }
    function openRequest(serviceId, opener) {
        if (!writesAllowed) { message('ثبت درخواست در اتصال واقعی هنوز فعال نیست.', 'error'); return; }
        const service = bootstrap.catalog.find(item => item.id === serviceId); if (!service || !service.plans.length) return;
        try { requestDraft = { id: newDraftId(), serviceId }; } catch (error) { message(error.message, 'error'); return; }
        requestSubmitted = false; ui['service-request-form'].reset(); setFormEnabled(ui['service-request-form'], true);
        ui['request-dialog-title'].textContent = 'درخواست ' + service.name; ui['request-dialog-description'].textContent = service.description;
        ui['request-form-plan'].replaceChildren(); for (const plan of service.plans) { const option = node('option', '', plan.name + ' · ' + amount(plan.amount) + ' ' + currency(plan.currency)); option.value = plan.id; ui['request-form-plan'].append(option); }
        message('', null, ui['request-dialog-message']); renderPlanSummary(); openDialog(ui['service-request-dialog'], opener);
    }
    function openTracking(id, kind, opener) {
        const data = session.snapshot(), item = (kind === 'publication' ? data.publications : data.requests).find(entry => entry.id === id && entry.organizationId === bootstrap.organizationId);
        if (!item) { message('درخواست در سازمان جاری پیدا نشد.', 'error'); return; }
        const body = ui['request-detail-body']; body.replaceChildren(); ui['request-detail-title'].textContent = kind === 'publication' ? 'عرضهٔ ' + item.name : item.serviceName;
        const details = node('dl', 'workspace-dialog-summary'); summaryPart(details, 'شناسهٔ پیگیری', item.id); summaryPart(details, 'سازمان', bootstrap.organizationName);
        summaryPart(details, 'زمان درخواست', displayDate(item.createdAt)); summaryPart(details, kind === 'publication' ? 'مرکز قابلیت' : 'طرح انتخابی', kind === 'publication' ? centerName(item.center) : item.planName);
        body.append(badge(item.status), node('p', '', kind === 'publication' ? item.description : item.reason), details);
        if (kind === 'publication') body.append(node('p', 'workspace-table-secondary', item.url));
        const timeline = node('ol', 'workspace-timeline');
        for (const [index, step] of item.history.entries()) { const stage = node('li', index === item.history.length - 1 ? 'is-current' : 'is-complete'); stage.append(node('strong', '', step.label), node('small', '', displayDate(step.at))); timeline.append(stage); }
        body.append(timeline);
        if (item.status === 'rejected') body.append(node('p', 'workspace-dialog-note', 'برای ارسال مجدد، اطلاعات را بازبینی کنید و درخواست تازه‌ای ثبت کنید.'));
        openDialog(ui['request-detail-dialog'], opener);
    }
    root.addEventListener('click', event => {
        if (!session) return;
        const offer = event.target.closest?.('[data-offer-id]'); if (offer && !offer.disabled) openRequest(offer.dataset.offerId, offer);
        const detail = event.target.closest?.('[data-request-id]'); if (detail) openTracking(detail.dataset.requestId, detail.dataset.requestKind, detail);
        const review = event.target.closest?.('[data-review-id]'); if (review && !review.disabled) {
            review.disabled = true;
            try {
                if (review.dataset.reviewKind === 'publication') session.reviewPublication(review.dataset.reviewId, review.dataset.reviewAction);
                else session.reviewRequest(review.dataset.reviewId, review.dataset.reviewAction);
                renderAll(); resultMessage('نتیجهٔ بررسی ثبت شد.');
                (ui['review-list'].querySelector('[data-review-id]') || tabs.find(tab => tab.dataset.workspaceTab === currentTab))?.focus();
            } catch (error) { review.disabled = !writesAllowed; message(error.message, 'error'); }
        }
    });
    ui['service-request-form'].addEventListener('submit', event => {
        event.preventDefault(); if (submitting || requestSubmitted) return;
        if (!writesAllowed || !requestDraft) { message('ثبت درخواست فعلاً در دسترس نیست.', 'error', ui['request-dialog-message']); return; }
        submitting = true; ui['request-dialog-submit'].disabled = true;
        try {
            session.request({ ...requestDraft, planId: ui['request-form-plan'].value, reason: ui['request-form-reason'].value });
            requestSubmitted = true; setFormEnabled(ui['service-request-form'], false); renderAll();
            ui['service-request-dialog'].close(); activateTab('requests', true); resultMessage('درخواست ثبت شد.');
        } catch (error) { message(error.message, 'error', ui['request-dialog-message']); ui['request-dialog-submit'].disabled = false; }
        finally { submitting = false; }
    });
    const newPublication = button('معرفی سرویس دیگر', 'button button-secondary'); newPublication.hidden = true;
    ui['publish-submit'].parentElement.append(newPublication);
    newPublication.addEventListener('click', () => {
        if (!writesAllowed) return; publicationDraftId = null; publicationSubmitted = false; ui['publish-form'].reset();
        setFormEnabled(ui['publish-form'], true); newPublication.hidden = true; message('', null, ui['publish-message']); get('publish-name').focus();
    });
    ui['publish-form'].addEventListener('submit', event => {
        event.preventDefault(); if (submitting || publicationSubmitted) return;
        if (!writesAllowed) { message('ارسال درخواست عرضه فعلاً در دسترس نیست.', 'error', ui['publish-message']); return; }
        submitting = true; ui['publish-submit'].disabled = true;
        try {
            publicationDraftId = publicationDraftId || newDraftId(); const fields = Object.fromEntries(new FormData(ui['publish-form']).entries());
            session.publish({ ...fields, id: publicationDraftId }); publicationSubmitted = true; setFormEnabled(ui['publish-form'], false);
            newPublication.hidden = false; renderAll(); resultMessage('درخواست عرضه ثبت شد.', ui['publish-message']);
        } catch (error) { message(error.message, 'error', ui['publish-message']); ui['publish-submit'].disabled = false; }
        finally { submitting = false; }
    });
    ui['request-form-plan'].addEventListener('change', renderPlanSummary);
    ui['catalog-search'].addEventListener('input', () => session && renderCatalog()); ui['catalog-center'].addEventListener('change', () => session && renderCatalog());
    ui['request-status'].addEventListener('change', () => session && renderRequests()); ui['review-status']?.addEventListener('change', () => session && renderRequests());
    function validateBootstrap(value) {
        if (!value || !['synthetic-preview', 'connected'].includes(value.mode) || !Array.isArray(value.catalog)
            || !Array.isArray(value.organizations) || !Array.isArray(value.activeServices) || !Array.isArray(value.requests)
            || !Array.isArray(value.publications) || value.catalog.some(service => !service || !Array.isArray(service.plans))
            || !value.usage || !Array.isArray(value.usage.items) || value.publishedCatalog && !Array.isArray(value.publishedCatalog.services))
            throw new Error('ساختار دادهٔ فضای کار معتبر نیست. دوباره تلاش کنید.');
    }
    function installSnapshot(data) {
        bootstrap = data; preview = data.mode === 'synthetic-preview';
        let storage = null; if (preview) try { storage = window.sessionStorage; } catch { /* Preview stays in memory. */ }
        session = createPreviewSession(bootstrap, { storage });
        try { writesAllowed = preview && bootstrap.catalogStatus === 'ready' && !!storageScope(bootstrap); } catch { writesAllowed = false; }
        setFormEnabled(ui['publish-form'], writesAllowed); setFormEnabled(ui['service-request-form'], false);
        centerOptions(); renderAll();
    }
    const organization = get('workspace-tenant'), organizationForm = get('workspace-organization-form'), organizationSubmit = get('workspace-organization-submit');
    const loader = globalThis.FanasaWorkspaceTransport?.createLoader({
        url: location.href, parse: parseBootstrapJson, validate: validateBootstrap,
        onStart() {
            root.setAttribute('aria-busy', 'true');
            ui['workspace-refresh'].disabled = organization.disabled = organizationSubmit.disabled = true;
            for (const dialog of [ui['service-request-dialog'], ui['request-detail-dialog']]) if (dialog.open) dialog.close();
            requestDraft = null; publicationDraftId = null; requestSubmitted = publicationSubmitted = false;
            ui['publish-form'].reset();
            installSnapshot({ ...bootstrap, catalogStatus: 'unavailable', organizationId: null, organizationName: '',
                catalog: [], activeServices: [], requests: [], publications: [], accountingUrl: null,
                usage: { status: 'unavailable', items: [], totalAmount: '' }, publishedCatalog: { status: 'Unavailable', services: [], message: 'در حال دریافت کاتالوگ…' } });
            message('در حال دریافت اطلاعات سازمان و خدمات…');
        },
        onData(data) {
            installSnapshot(data);
            organization.replaceChildren(new Option('سازمان خود را انتخاب کنید', ''));
            for (const item of data.organizations) organization.add(new Option(item.name, item.id, false, item.id === data.organizationId));
            const url = new URL(location.href);
            if (data.organizationId) url.searchParams.set('tenantId', data.organizationId); else url.searchParams.delete('tenantId');
            history.replaceState(null, '', url);
            message(data.message);
        },
        onError(timedOut) { message(timedOut ? 'دریافت اطلاعات بیش از حد طول کشید. دوباره تلاش کنید.' : 'اطلاعات دریافت نشد. دوباره تلاش کنید یا وارد حساب شوید.', 'error'); },
        onEnd() {
            root.setAttribute('aria-busy', 'false'); ui['workspace-refresh'].disabled = false;
            organization.disabled = organization.options.length <= 1; organizationSubmit.disabled = organization.disabled;
        }
    });
    const refreshWorkspace = () => loader?.load(organization.value);
    ui['workspace-refresh'].addEventListener('click', refreshWorkspace);
    organizationForm?.addEventListener('submit', event => { if (loader) { event.preventDefault(); refreshWorkspace(); } });
    organization?.addEventListener('change', refreshWorkspace);
    window.addEventListener('hashchange', () => activateTab(location.hash.slice(1) || 'catalog', false));
    try {
        bootstrap = parseBootstrapJson(get('workspace-bootstrap').textContent);
        validateBootstrap(bootstrap);
        installSnapshot(bootstrap);
        if (bootstrap.isAdmin !== true) { const reviewTab = tabs.find(tab => tab.dataset.workspaceTab === 'review'); if (reviewTab) reviewTab.hidden = true; }
        const warning = session.snapshot().storageWarning; message(warning || bootstrap.message || ('فضای کار ' + bootstrap.organizationName), warning ? 'error' : null);
        const initialTab = location.hash.slice(1), availableTab = tabs.some(tab => tab.dataset.workspaceTab === initialTab && !tab.hidden);
        activateTab(availableTab ? initialTab : bootstrap.catalog.length || bootstrap.publishedCatalog?.services.length ? 'catalog' : 'services', false);
    } catch (error) {
        writesAllowed = false; setFormEnabled(ui['publish-form'], false); setFormEnabled(ui['service-request-form'], false);
        message(error.message || 'دادهٔ فضای کار دریافت نشد. صفحه را تازه کنید.', 'error'); activateTab('catalog', false);
    } finally {
        root.dataset.ready = 'true'; root.setAttribute('aria-busy', 'false');
    }
})();
