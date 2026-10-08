/* Server-driven SPA. No credentials, HTML, or drafts in persistent browser storage.
   Razor handlers and the scoped API remain authoritative for every mutation. */
(() => {
    'use strict';
    const main = document.getElementById('content');
    if (!main || !window.fetch || !window.DOMParser) return;
    const feedback = document.getElementById('spa-feedback');
    const message = document.getElementById('spa-message');
    const refresh = document.getElementById('spa-refresh');
    const login = document.getElementById('spa-login');
    const drafts = new Map();
    let dirtyForms = new Set();
    let busy = false, writing = false, dirty = false, active = location.href;
    let pollTimer = 0;
    let loginWindow = null;
    let index = Number(history.state?.neoIndex ?? 0);
    const initialDocument = crypto.randomUUID();
    document.documentElement.dataset.spaDocument = initialDocument;
    history.replaceState({ neoIndex: index }, '', active);
    history.scrollRestoration = 'manual';

    const route = value => {
        const url = new URL(value, location.href);
        return url.origin === location.origin && !url.username && !url.password &&
            (/^\/work\//i.test(url.pathname) || ['/', '/workspace', '/product'].includes(url.pathname.toLowerCase()));
    };
    const forms = () => [...main.querySelectorAll('form')];
    const formKey = (form, number) => `${form.getAttribute('data-draft-key') || number}:${new URL(form.getAttribute('action') || active, active).pathname}:${new URL(form.getAttribute('action') || active, active).search}`;
    const editable = field => field.name && !['hidden', 'password', 'file', 'submit', 'button'].includes(field.type);
    function snapshot() {
        const values = new Map();
        forms().forEach((form, i) => {
            if (!dirtyForms.has(formKey(form, i))) return;
            values.set(formKey(form, i), [...form.elements].filter(editable).map(field => ({
                name: field.name, value: field.value, checked: field.checked, type: field.type
            })));
        });
        return { values, dirty, x: scrollX, y: scrollY,
            details: [...main.querySelectorAll('details')].map((x, i) => ({
                key: x.querySelector('form')?.getAttribute('data-draft-key') || `detail:${i}`, open: x.open })),
            horizontal: [...main.querySelectorAll('.kanban,.table-scroll')].map(x => x.scrollLeft) };
    }
    function remember() {
        drafts.set(active, snapshot());
        while (drafts.size > 30) drafts.delete(drafts.keys().next().value);
    }
    function restore(saved, omitForm = null) {
        dirtyForms = new Set();
        if (!saved) { dirty = false; return; }
        forms().forEach((form, i) => {
            const key = formKey(form, i);
            if (key === omitForm) return;
            const fields = saved.values.get(key);
            if (!fields) return;
            dirtyForms.add(key);
            // Do not restore hidden versions, antiforgery tokens or request IDs.
            [...form.elements].filter(editable).forEach(field => {
                const prior = fields.find(x => x.name === field.name && x.type === field.type &&
                    (!['checkbox', 'radio'].includes(field.type) || x.value === field.value));
                if (!prior) return;
                if (['checkbox', 'radio'].includes(field.type)) field.checked = prior.checked;
                else field.value = prior.value;
            });
        });
        [...main.querySelectorAll('details')].forEach((x, i) => {
            const key = x.querySelector('form')?.getAttribute('data-draft-key') || `detail:${i}`;
            x.open = saved.details.find(entry => entry.key === key)?.open ?? false;
        });
        [...main.querySelectorAll('.kanban,.table-scroll')].forEach((x, i) => x.scrollLeft = saved.horizontal[i] ?? 0);
        // Read-only filter drafts do not warrant an unsaved-write warning.
        dirty = forms().some((form, i) => form.method.toLowerCase() === 'post' && dirtyForms.has(formKey(form, i)));
    }
    function tell(text, error = false, session = false) {
        feedback.hidden = !text;
        message.textContent = text;
        feedback.dataset.error = String(error);
        refresh.hidden = !error || session;
        login.hidden = !session;
    }
    function setBusy(value, mutation = false) {
        busy = value; writing = value && mutation;
        main.setAttribute('aria-busy', String(value));
        main.inert = value;
        document.documentElement.classList.toggle('spa-busy', value);
    }
    function boardSnapshot() {
        return [...main.querySelectorAll('.kanban-card-compact[data-item-id]')].reduce((map, card) => {
            map.set(card.dataset.itemId, { state: card.dataset.state, agent: card.dataset.agentOwned === 'true' });
            return map;
        }, new Map());
    }
    function scheduleBoardPoll() {
        clearTimeout(pollTimer);
        pollTimer = 0;
        if (!main.querySelector?.('.kanban')) return;
        pollTimer = setTimeout(async () => {
            // A poll must not reset a GET filter or interrupt an edited form.
            const editing = document.activeElement?.closest?.('form');
            if (!busy && !dirtyForms.size && !editing && !main.querySelector('.moving-card') && document.visibilityState === 'visible')
                await show(active, { preserve: true, silent: true, poll: true });
            scheduleBoardPoll();
        }, 6000);
    }
    async function show(url, options = {}) {
        if (busy) { tell('درخواست قبلی هنوز در حال انجام است.'); return false; }
        if (!route(url)) return false;
        remember();
        const from = active, saved = drafts.get(from), beforeBoard = boardSnapshot();
        const method = options.body ? 'POST' : 'GET';
        const oldFocus = document.activeElement;
        const focusName = oldFocus?.getAttribute('name');
        const focusedForm = oldFocus?.closest('form');
        const focusFormIndex = focusedForm ? forms().indexOf(focusedForm) : -1;
        setBusy(true, method === 'POST');
        if (!options.silent) tell(method === 'POST' ? 'در حال ثبت…' : 'در حال دریافت…');
        const controller = new AbortController();
        const timeout = setTimeout(() => controller.abort(), 45000);
        try {
            const response = await fetch(url, { method, body: options.body, credentials: 'same-origin',
                signal: controller.signal, mode: 'same-origin', cache: 'no-store', headers: { 'X-Neo-Navigation': '1', 'Accept': 'text/html' } });
            if (response.status === 401) { tell('نشست معتبر نیست؛ نوشته‌های شما در این صفحه حفظ شده‌اند. دوباره وارد شوید.', true, true); return false; }
            if (!route(response.url)) { tell('این پاسخ نیاز به ورود یا بررسی دسترسی دارد.', true, true); return false; }
            const html = response.headers.get('content-type')?.includes('text/html') ? await response.text() : '';
            const page = new DOMParser().parseFromString(html, 'text/html');
            const next = page.getElementById('content');
            if (!response.ok || !next) {
                const detail = next?.querySelector('.alert')?.textContent?.trim();
                const reason = response.status === 409 ? 'رکورد تغییر کرده است؛ آخرین وضعیت را بگیرید و دوباره بررسی کنید.' :
                    response.status === 403 ? 'اجازهٔ این عملیات را ندارید.' : 'درخواست انجام نشد؛ ورودی و ارتباط را بررسی کنید.';
                tell(detail || reason, true); return false;
            }
            const previousActor = main.getAttribute?.('data-session-actor') || '';
            const nextActor = next.getAttribute('data-session-actor') || '';
            if (previousActor && nextActor !== previousActor) {
                tell('حساب واردشده تغییر کرده است؛ برای حفظ محرمانگی نوشته‌ها، با حساب قبلی وارد شوید یا این صفحه را خودتان ببندید.', true, true);
                return false;
            }
            // Server markup is encoded by Razor. Never execute a script from a fetched document.
            next.querySelectorAll('script,base,iframe,object,embed').forEach(node => node.remove());
            next.querySelectorAll('*').forEach(node => [...node.attributes].forEach(attribute => {
                if (/^on/i.test(attribute.name)) node.removeAttribute(attribute.name);
            }));
            main.replaceChildren(...[...next.childNodes].map(node => document.importNode(node, true)));
            main.setAttribute('data-session-actor', nextActor);
            document.dispatchEvent(new CustomEvent('neo:board-updated', { detail: { before: beforeBoard, poll: !!options.poll } }));
            main.inert = false;
            document.title = page.title;
            const target = new URL(response.url);
            target.hash = new URL(url, location.href).hash;
            target.searchParams.delete('handler'); // POST handlers are actions, not navigable pages.
            active = target.href;
            const samePage = new URL(from).pathname === target.pathname;
            if (!options.pop) {
                if (active !== from) history.pushState({ neoIndex: ++index }, '', active);
                else history.replaceState({ neoIndex: index }, '', active);
            }
            if (method === 'POST') {
                drafts.delete(from);
                restore(samePage ? saved : null, options.formKey);
                // Other edited forms remain in memory; submitted form is now authoritative.
            } else restore(options.preserve ? saved : drafts.get(active));
            const position = options.preserve || (method === 'POST' && samePage) ? saved : drafts.get(active);
            scrollTo(position?.x ?? 0, position?.y ?? 0);
            if (target.hash) {
                let hash = target.hash.slice(1);
                try { hash = decodeURIComponent(hash); } catch { /* literal malformed fragment */ }
                document.getElementById(hash)?.scrollIntoView();
            }
            if (samePage && focusName && focusFormIndex >= 0) {
                const field = [...(forms()[focusFormIndex]?.elements ?? [])].find(x => x.name === focusName);
                field?.focus({ preventScroll: true });
            } else main.focus({ preventScroll: true });
            if (!options.silent) tell(method === 'POST' ? 'عملیات ثبت شد.' : '');
            document.dispatchEvent(new CustomEvent('neo:navigated', { detail: { url: active } }));
            return true;
        } catch {
            tell(method === 'POST' ? 'پاسخ دریافت نشد؛ ممکن است عملیات ثبت شده باشد. پیش از ارسال مجدد، آخرین وضعیت را بررسی کنید. ورودی‌ها حفظ شدند.' :
                'ارتباط برقرار نشد. صفحه و ورودی‌ها حفظ شدند؛ دوباره آخرین وضعیت را دریافت کنید.', true);
            return false;
        } finally {
            clearTimeout(timeout);
            setBusy(false);
            if (oldFocus?.isConnected && main.contains(oldFocus)) oldFocus.focus({ preventScroll: true });
        }
    }
    document.addEventListener('click', event => {
        const anchor = event.target.closest?.('a[href]');
        if (!anchor || event.defaultPrevented || event.button !== 0 || event.metaKey || event.ctrlKey || event.shiftKey || event.altKey ||
            anchor.target || anchor.hasAttribute('download') || anchor.hasAttribute('data-native')) return;
        const url = new URL(anchor.href);
        if (!route(url)) return;
        if (url.pathname === location.pathname && url.search === location.search && url.hash) return;
        event.preventDefault();
        void show(url.href);
    });
    document.addEventListener('submit', event => {
        const form = event.target;
        const submitter = event.submitter;
        const action = submitter?.hasAttribute('formaction') ? submitter.formAction : form.action;
        if (!main.contains(form) || !route(action) || form.hasAttribute('data-native')) return;
        event.preventDefault();
        if (busy || !form.reportValidity()) return;
        const data = new FormData(form, event.submitter);
        const method = submitter?.hasAttribute('formmethod') ? submitter.formMethod : form.method;
        if (method.toLowerCase() === 'get') {
            const url = new URL(action);
            url.search = new URLSearchParams([...data.entries()].filter(([, value]) => typeof value === 'string')).toString();
            void show(url.href);
        } else void show(action, { body: data, formKey: formKey(form, forms().indexOf(form)) });
    });
    function trackDraft(event) {
        const form = event.target.closest('form');
        if (form && main.contains(form)) {
            dirtyForms.add(formKey(form, forms().indexOf(form)));
            if (form.method.toLowerCase() === 'post') dirty = true;
        }
    }
    main.addEventListener('input', trackDraft);
    main.addEventListener('change', trackDraft);
    window.addEventListener('beforeunload', event => { if (dirty || writing) { event.preventDefault(); event.returnValue = ''; } });
    window.addEventListener('popstate', event => {
        const destination = location.href;
        const nextIndex = Number(event.state?.neoIndex ?? index);
        if (busy) { history.go(index - nextIndex); return; }
        const oldIndex = index;
        index = nextIndex;
        void show(destination, { pop: true }).then(ok => {
            if (!ok) { index = oldIndex; history.replaceState({ neoIndex: index }, '', active); }
        });
    });
    refresh.addEventListener('click', () => { void show(active, { preserve: true }); });
    login.addEventListener('click', event => {
        // Reauthenticate separately so in-memory drafts never need persistent
        // storage and an expired POST is never automatically replayed.
        const popup = window.open('/Login?returnUrl=%2FSessionRestored', 'fanasa-work-reauth');
        if (popup) {
            event.preventDefault(); loginWindow = popup;
            tell('ورود را در پنجره بازشده تکمیل کنید؛ نوشته‌های این صفحه حفظ می‌شوند.', true, true);
        } else {
            event.preventDefault();
            tell('مرورگر پنجره ورود را مسدود کرد. اجازه بازشدن آن را بدهید و دوباره ورود مجدد را بزنید؛ نوشته‌ها حفظ شده‌اند.', true, true);
        }
    });
    window.addEventListener('message', event => {
        if (event.origin !== location.origin || !loginWindow || event.source !== loginWindow ||
            event.data?.type !== 'fanasa:session-restored') return;
        loginWindow = null;
        // GET only, preserving current route/filter/drafts; the user decides
        // whether to resubmit a command after inspecting the current record.
        void show(active, { preserve: true });
    });
    document.getElementById('spa-dismiss').addEventListener('click', () => { feedback.hidden = true; });
    document.addEventListener('neo:navigated', scheduleBoardPoll);
    document.addEventListener('visibilitychange', scheduleBoardPoll);
    scheduleBoardPoll();
    window.NeoWorkbench = Object.freeze({ navigate: show, reload: () => show(active, { preserve: true }),
        get busy() { return busy; }, documentId: initialDocument });
})();
