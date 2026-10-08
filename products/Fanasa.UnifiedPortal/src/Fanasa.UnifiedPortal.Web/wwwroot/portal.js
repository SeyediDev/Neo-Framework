(() => {
    const cards = [...document.querySelectorAll('.center-card')];
    const search = document.getElementById('center-search');
    const count = document.getElementById('visible-count');
    const noResults = document.getElementById('no-results');
    let filter = 'all';
    const normalize = text => text.normalize('NFKC').replace(/ي/g, 'ی').replace(/ك/g, 'ک').replace(/\u200c/g, ' ').trim().toLocaleLowerCase();
    const filterCenters = () => {
        const query = normalize(search.value || '');
        let shown = 0;
        cards.forEach(card => {
            const matches = (filter === 'all' || card.dataset.state === filter) && (!query || normalize(card.dataset.search).includes(query));
            card.hidden = !matches;
            if (matches) shown++;
        });
        count.textContent = shown.toLocaleString('fa-IR');
        noResults.hidden = shown !== 0;
    };
    search?.addEventListener('input', filterCenters);
    document.querySelectorAll('[data-filter]').forEach(button => button.addEventListener('click', () => {
        filter = button.dataset.filter;
        document.querySelectorAll('[data-filter]').forEach(item => {
            item.classList.toggle('is-active', item === button);
            item.setAttribute('aria-pressed', String(item === button));
        });
        filterCenters();
    }));

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
    let requestId = 0;
    let controller;
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
        select.replaceChildren(new Option(data.organizations.length ? 'سازمان موردنظر را انتخاب کنید' : 'سازمانی در دسترس نیست', ''));
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
        emptyTitle.textContent = data.status === 'Ready' ? 'فضای کار شما از اینجا آغاز می‌شود' : 'اطلاعات فضای کار در دسترس نیست';
        emptyDescription.textContent = data.message;
        if (data.status === 'SignInRequired' && !empty.querySelector('a')) {
            const login = textElement('a', 'button button-secondary', 'ورود دوباره');
            login.href = '/login';
            empty.append(login);
        }
        productCount.textContent = data.products.length.toLocaleString('fa-IR') + ' سامانه';
        const url = new URL(location.href);
        if (data.activeOrganizationId) url.searchParams.set('tenantId', data.activeOrganizationId);
        else url.searchParams.delete('tenantId');
        history.replaceState(null, '', url);
    };
    const load = async () => {
        const id = ++requestId;
        controller?.abort();
        controller = new AbortController();
        const requested = select.value;
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
            const response = await fetch(url, { credentials: 'same-origin', cache: 'no-store', signal: controller.signal });
            if (!response.ok || !response.headers.get('content-type')?.includes('application/json')) throw new Error('workspace');
            const data = await response.json();
            if (id === requestId) render(data);
        } catch (error) {
            if (error.name === 'AbortError' || id !== requestId) return;
            status.textContent = 'ارتباط برقرار نشد. دوباره تلاش کنید.';
            status.classList.add('is-error');
            empty.hidden = false;
            emptyTitle.textContent = 'اطلاعات فضای کار دریافت نشد';
            emptyDescription.textContent = 'با تازه‌سازی فهرست دوباره تلاش کنید.';
            productCount.textContent = '— سامانه';
        } finally {
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
