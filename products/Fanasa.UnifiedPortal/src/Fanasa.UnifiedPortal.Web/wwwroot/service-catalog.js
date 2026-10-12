(() => {
    const centers = [...document.querySelectorAll('.center-card[data-center]')];
    const status = document.getElementById('service-catalog-status');
    const refresh = document.getElementById('service-catalog-refresh');
    if (!status || !refresh) return;
    const safeUrl = value => {
        try {
            const url = new URL(value);
            return ['https:', 'http:'].includes(url.protocol) && !url.username && !url.password ? url.href : null;
        } catch { return null; }
    };
    const text = (tag, className, content) => {
        const element = document.createElement(tag);
        element.className = className;
        element.textContent = content;
        return element;
    };
    const link = (className, label, value, name) => {
        const url = safeUrl(value);
        if (!url) return null;
        const element = text('a', className, label + ' ↗');
        element.href = url;
        element.target = '_blank';
        element.rel = 'noopener noreferrer';
        element.setAttribute('aria-label', label + ' ' + name);
        return element;
    };
    const labels = {
        allowed: 'دسترسی فعال', requestRequired: 'نیازمند درخواست دسترسی',
        signInRequired: 'برای بررسی دسترسی وارد شوید', selectOrganization: 'سازمان خود را انتخاب کنید',
        unverified: 'دسترسی ورود هنوز تأیید نشده است'
    };
    const invalidate = () => {
        // A change of consumer, logout or outage invalidates ALL launch links immediately.
        centers.forEach(center => {
            center.querySelectorAll('.service-launch').forEach(element => element.remove());
            center.querySelectorAll('.service-access').forEach(element => {
                element.textContent = labels.unverified;
                element.dataset.accessState = 'unverified';
            });
        });
        status.textContent = 'در حال بررسی کاتالوگ و دسترسی سازمان…';
    };
    const render = data => {
        const ready = data?.status === 'Ready' && Array.isArray(data.services);
        centers.forEach(center => {
            const mount = center.querySelector('[data-services]');
            if (!mount) return;
            mount.replaceChildren();
            const services = ready ? data.services.filter(item => item.center === center.dataset.center) : [];
            services.forEach(item => {
                const card = text('section', 'service-card', '');
                card.dataset.serviceId = item.productId;
                card.append(text('h4', '', item.displayName), text('p', 'service-description', item.description));
                const access = text('span', 'service-access', labels[item.accessState] || labels.unverified);
                access.dataset.accessState = item.accessState;
                card.append(access);
                const actions = text('div', 'service-actions', '');
                const details = link('', 'معرفی و جزئیات', item.detailsUrl, item.displayName);
                if (details) actions.append(details);
                const launch = item.accessState === 'allowed' ? link('service-launch', 'ورود به سامانه', item.launchUrl, item.displayName) : null;
                const request = item.accessState !== 'allowed' ? link('service-request', 'درخواست دسترسی / خرید', item.requestAccessUrl, item.displayName) : null;
                if (launch) actions.append(launch);
                else if (item.accessState === 'allowed') actions.append(text('span', '', 'نشانی ورود هنوز آماده نیست'));
                else if (request) actions.append(request);
                else actions.append(text('span', '', 'مسیر درخواست دسترسی هنوز منتشر نشده است'));
                card.append(actions);
                mount.append(card);
            });
            if (!services.length) mount.append(text('p', 'service-empty', ready
                ? 'هنوز سرویس منتشرشده‌ای در این مرکز نیست.' : 'فهرست سرویس‌های این مرکز دریافت نشده است.'));
            center.dataset.search = (center.dataset.centerSearch || '') + ' ' + services.map(item => item.displayName + ' ' + item.description).join(' ');
        });
        status.textContent = data?.message || 'کاتالوگ خدمات دریافت نشد؛ دوباره تلاش کنید.';
        globalThis.FanasaCenterFilter?.();
    };
    const failed = () => { status.textContent = 'کاتالوگ یا دسترسی ورود دریافت نشد؛ دوباره تلاش کنید.'; };
    globalThis.FanasaServiceCatalog = { render, invalidate, failed };

    // Logged-in refresh uses the existing bounded workspace request. No second grant query.
    let pending = false;
    refresh.addEventListener('click', async () => {
        const workspaceRefresh = document.getElementById('workspace-refresh');
        if (workspaceRefresh) {
            if (!workspaceRefresh.disabled) workspaceRefresh.click();
            return;
        }
        if (pending) return;
        pending = true;
        refresh.disabled = true;
        invalidate();
        const controller = new AbortController();
        const timeout = setTimeout(() => controller.abort(), 50000);
        try {
            const url = new URL(location.pathname, location.origin);
            url.searchParams.set('handler', 'Workspace');
            const response = await fetch(url, { credentials: 'same-origin', cache: 'no-store', signal: controller.signal });
            if (!response.ok || !response.headers.get('content-type')?.includes('application/json')) throw new Error('catalog');
            const data = await response.json();
            if (controller.signal.aborted) throw new Error('aborted');
            render(data.serviceCatalog);
        } catch { failed(); }
        finally { clearTimeout(timeout); pending = false; refresh.disabled = false; }
    });
})();
