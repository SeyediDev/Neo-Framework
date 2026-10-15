(() => {
    const cards = [...document.querySelectorAll('.center-card')];
    const search = document.getElementById('center-search');
    const count = document.getElementById('visible-count');
    const noResults = document.getElementById('no-results');
    const clearSearch = document.getElementById('center-search-clear');
    const resetFilters = document.getElementById('center-filter-reset');
    const summary = document.getElementById('center-filter-summary');
    const filterButtons = [...document.querySelectorAll('[data-filter]')];
    let filter = 'all';
    const normalize = text => String(text ?? '').normalize('NFKC')
        .replace(/[يى]/g, 'ی').replace(/ك/g, 'ک')
        .replace(/[\u0640\u064b-\u065f\u0670\u06d6-\u06ed]/g, '')
        .replace(/[\u200c\u200d]/g, ' ').replace(/\s+/g, ' ').trim().toLocaleLowerCase('fa-IR');
    const filterCenters = () => {
        const query = normalize(search?.value);
        const terms = query ? query.split(' ') : [];
        let shown = 0;
        cards.forEach(card => {
            const text = normalize(card.dataset.search);
            const matches = (filter === 'all' || card.dataset.state === filter) && terms.every(term => text.includes(term));
            card.hidden = !matches;
            if (matches) shown++;
        });
        const total = shown.toLocaleString('fa-IR');
        if (count) count.textContent = total;
        if (noResults) noResults.hidden = shown !== 0;
        if (clearSearch) clearSearch.hidden = !search?.value;
        if (summary) {
            const category = filter === 'all' ? '' : ' در گروه «' + filterButtons.find(button => button.dataset.filter === filter)?.textContent.trim() + '»';
            const phrase = query ? ' برای «' + search.value.trim() + '»' : '';
            summary.textContent = total + ' مرکز' + category + phrase + ' نمایش داده می‌شود.';
        }
    };
    const selectFilter = value => {
        filter = value;
        filterButtons.forEach(button => {
            const selected = button.dataset.filter === filter;
            button.classList.toggle('is-active', selected);
            button.setAttribute('aria-pressed', String(selected));
        });
        filterCenters();
    };
    const clearQuery = () => {
        if (!search) return;
        search.value = '';
        filterCenters();
        search.focus();
    };
    globalThis.FanasaCenterFilter = filterCenters;
    search?.addEventListener('input', filterCenters);
    search?.addEventListener('keydown', event => {
        if (event.key === 'Escape' && search.value) {
            event.preventDefault();
            clearQuery();
        }
    });
    clearSearch?.addEventListener('click', clearQuery);
    resetFilters?.addEventListener('click', () => {
        if (search) search.value = '';
        selectFilter('all');
        search?.focus();
    });
    filterButtons.forEach(button => button.addEventListener('click', () => selectFilter(button.dataset.filter)));
    filterCenters();

    const panel = document.getElementById('workspace');
    const form = document.getElementById('organization-form');
    if (!form) return;
    const select = document.getElementById('organization-select');
    const submit = document.getElementById('organization-submit');
    const refresh = document.getElementById('workspace-refresh');
    const status = document.getElementById('workspace-status');
    const grid = document.getElementById('product-grid');
    const empty = document.getElementById('workspace-empty');
    const emptyTitle = document.getElementById('empty-title');
    const emptyDescription = document.getElementById('empty-description');
    const productCount = document.getElementById('product-count');
    const login = document.getElementById('workspace-login');
    let requestId = 0;
    let controller;
    // Three sequential server calls can each take 15 seconds. Bound the whole
    // browser request (including its body) without cutting off a normal server timeout.
    const workspaceTimeoutMs = 50000;
    const textElement = (tag, className, text) => {
        const element = document.createElement(tag);
        element.className = className;
        element.textContent = text;
        return element;
    };
    const safeUrl = value => {
        try {
            const url = new URL(value);
            return ['http:', 'https:'].includes(url.protocol) && !url.username && !url.password ? url.href : null;
        } catch { return null; }
    };
    const render = data => {
        select.replaceChildren(new Option(data.presentation.organizationPlaceholder, ''));
        data.organizations.forEach(organization => select.add(new Option(organization.name, organization.id, false, organization.id === data.activeOrganizationId)));
        grid.replaceChildren();
        data.products.forEach(product => {
            const card = textElement('article', 'product-card', '');
            const mark = textElement('span', 'product-mark', '◈');
            mark.setAttribute('aria-hidden', 'true');
            card.append(mark);
            const details = textElement('div', 'product-details', '');
            details.append(textElement('h3', '', product.displayName), textElement('p', '', data.activeOrganizationName || ''));
            card.append(details);
            const url = safeUrl(product.url);
            if (url) {
                const link = textElement('a', 'product-open', 'ورود به سامانه ↗');
                link.href = url;
                link.target = '_blank';
                link.rel = 'noopener noreferrer';
                link.setAttribute('aria-label', 'ورود به ' + product.displayName);
                card.append(link);
            } else card.append(textElement('span', 'unavailable-link', 'نشانی سامانه هنوز آماده نیست'));
            grid.append(card);
        });
        panel.dataset.status = data.status;
        status.textContent = data.message;
        status.classList.toggle('is-error', ['NotConfigured', 'Forbidden', 'Unavailable'].includes(data.status));
        empty.hidden = data.products.length > 0;
        grid.hidden = data.products.length === 0;
        emptyTitle.textContent = data.presentation.emptyTitle;
        emptyDescription.textContent = data.message;
        login.hidden = !data.presentation.needsSignIn;
        productCount.textContent = data.presentation.countLabel;
        const url = new URL(location.href);
        if (data.activeOrganizationId) url.searchParams.set('tenantId', data.activeOrganizationId);
        else url.searchParams.delete('tenantId');
        history.replaceState(null, '', url);
        globalThis.FanasaServiceCatalog?.render(data.serviceCatalog);
    };
    const load = async () => {
        const id = ++requestId;
        controller?.abort();
        controller = new AbortController();
        const requestController = controller;
        let timedOut = false;
        const timeout = setTimeout(() => {
            timedOut = true;
            requestController.abort();
        }, workspaceTimeoutMs);
        const requested = select.value;
        globalThis.FanasaServiceCatalog?.invalidate();
        panel.setAttribute('aria-busy', 'true');
        refresh.disabled = submit.disabled = select.disabled = true;
        status.textContent = 'در حال دریافت سازمان‌ها و سامانه‌ها…';
        status.classList.remove('is-error');
        // Clear the old tenant's links immediately; never show stale access after a switch.
        grid.replaceChildren();
        grid.hidden = true;
        empty.hidden = true;
        productCount.textContent = 'در حال دریافت…';
        try {
            const url = new URL(location.pathname, location.origin);
            url.searchParams.set('handler', 'Workspace');
            if (requested) url.searchParams.set('tenantId', requested);
            const response = await fetch(url, { credentials: 'same-origin', cache: 'no-store', signal: requestController.signal });
            if (!response.ok || !response.headers.get('content-type')?.includes('application/json')) throw new Error('workspace');
            const data = await response.json();
            if (requestController.signal.aborted) throw new Error('workspace-aborted');
            if (id === requestId) render(data);
        } catch (error) {
            if (id !== requestId) return;
            status.textContent = timedOut ? 'دریافت اطلاعات بیش از حد طول کشید. دوباره تلاش کنید.' : 'ارتباط برقرار نشد. دوباره تلاش کنید.';
            panel.dataset.status = 'Unavailable';
            status.classList.add('is-error');
            empty.hidden = false;
            emptyTitle.textContent = 'اطلاعات فضای کار دریافت نشد';
            emptyDescription.textContent = 'با تازه‌سازی فهرست دوباره تلاش کنید.';
            login.hidden = true;
            productCount.textContent = 'دریافت ناموفق';
            globalThis.FanasaServiceCatalog?.failed();
        } finally {
            clearTimeout(timeout);
            if (id === requestId) {
                panel.removeAttribute('aria-busy');
                refresh.disabled = false;
                select.disabled = select.options.length <= 1;
                submit.disabled = select.disabled;
            }
        }
    };
    form.addEventListener('submit', event => { event.preventDefault(); load(); });
    select.addEventListener('change', load);
    refresh.addEventListener('click', load);
})();
