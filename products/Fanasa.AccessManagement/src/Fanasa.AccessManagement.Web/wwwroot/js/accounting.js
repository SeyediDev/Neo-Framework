(function () {
    'use strict';
    const root = document.getElementById('accounting-app');
    if (!root) return;
    const exact = globalThis.FanasaBillingExact, $ = id => document.getElementById(id), form = $('billing-command');
    const endpoint = '/api/accounting/tenants/' + root.dataset.tenant, lifecycle = '/api/accounting/v1/tenants/' + root.dataset.tenant;
    let view, pending = null, sending = false;
    const storageKey = 'fanasa-billing-pending:' + root.dataset.requestScope;
    function persist() { try { if (pending) sessionStorage.setItem(storageKey, JSON.stringify(pending)); else sessionStorage.removeItem(storageKey); } catch {} }
    try {
        const saved = sessionStorage.getItem(storageKey);
        if (saved && saved.length <= 20000) {
            const candidate = JSON.parse(saved);
            if ([endpoint,lifecycle + '/invoices',lifecycle + '/credit-notes'].includes(candidate?.url)
                && typeof candidate.body === 'string' && candidate.body.length <= 16384
                && typeof JSON.parse(candidate.body).key === 'string') pending = candidate;
        }
    } catch {}
    const node = (tag, text, cls) => { const value = document.createElement(tag); if (text != null) value.textContent = text; if (cls) value.className = cls; return value; };
    const money = value => exact.format(value) + ' ' + (view?.account.currency || '');
    function message(text, error = false) { $('billing-message').textContent = text; $('billing-message').className = error ? 'org-error' : 'org-success'; }
    async function request(url = endpoint, options) {
        const response = await fetch(url, {cache:'no-store', ...options});
        const text = await response.text();
        if (!response.ok) { let data; try { data = JSON.parse(text); } catch {} const error = Error(data?.error || 'عملیات انجام نشد؛ دسترسی و نشست را بررسی کنید.'); error.definite = response.status >= 400 && response.status < 500; throw error; }
        if (!response.headers.get('content-type')?.includes('application/json')) throw Error('نشست را بررسی کنید؛ نتیجهٔ ثبت تأیید نشد.');
        return exact.parse(text);
    }
    function editor() {
        if (!form) return;
        const fields = {configure:['mode','currency','creditLimit'],tariff:['productKey','metric','unitPrice'],credit:['amount'],
            invoice:['period'],payment:['amount','invoiceId'],'credit-note':['amount','chargeId','invoiceId','reason']}[form.elements.operation.value] || [];
        for (const control of form.elements) {
            if (control.name && !['operation','reference'].includes(control.name)) control.closest('label').hidden = !fields.includes(control.name);
            control.disabled = sending || !!pending;
        }
        const submit = form.querySelector('[type=submit]'); submit.disabled = sending;
        submit.textContent = pending ? 'پیگیری همان درخواست' : 'ثبت';
    }
    function render() {
        const account = view.account, scale = account.currency === 'IRR' ? 0 : 2;
        $('billing-mode').textContent = account.mode === 'payg' ? 'پرداخت بر اساس مصرف' : 'پس‌پرداخت';
        $('billing-metrics').replaceChildren(...[['موجودی اعتبار',view.walletBalance],['بدهی جاری',view.outstanding],
            ['اعتبار قابل مصرف',view.availableCredit],['سقف اعتبار',account.creditLimit]].map(([label, value]) => {
                const card = node('div',null,'metric accent'), amount = node('strong',null,'billing-money');
                if (view.recorded === false) amount.textContent = 'نامعلوم';
                else amount.append(node('bdi',exact.format(value),'billing-money-number'),node('small',account.currency,'billing-money-currency'));
                card.append(node('span',label),amount); return card;
            }));
        $('billing-tariffs').replaceChildren(...account.tariffs.map(t => node('p',t.productKey + ' · ' + t.metric + ' · ' + money(t.unitPrice))));
        $('billing-invoices').replaceChildren(...account.invoices.map(invoice => {
            const paid = account.entries.filter(e => e.kind === 'payment' && e.invoiceId === invoice.id).reduce((sum,e) => sum + exact.units(e.amount,scale),0n);
            const credited = account.entries.filter(e => e.kind === 'credit-note' && e.invoiceId === invoice.id).reduce((sum,e) => sum + exact.units(e.amount,scale),0n);
            const remaining = exact.units(invoice.amount,scale) - paid - credited;
            if (remaining < 0n) throw Error('ماندهٔ صورتحساب نیازمند بررسی است.');
            const section = node('div'), title = node('p');
            title.append(node('b',invoice.period + ' · ' + money(invoice.amount)));
            title.append(node('span',account.mode === 'payg' ? ' · صورت مصرف اعتبار' : ' · مانده: ' + money(exact.decimal(remaining,scale))));
            if (credited > 0n) title.append(node('span',' · اصلاح اعتباری: ' + money(exact.decimal(credited,scale))));
            section.append(title);
            if (invoice.lines) {
                const details = node('details'); details.append(node('summary','جزئیات ثبت‌شده'));
                for (const line of invoice.lines) details.append(node('p',line.productKey + ' · ' + line.metric + ' · مقدار ' + exact.format(line.quantity)
                    + (line.unitPrice == null ? ' · تعرفه نامعلوم' : ' · تعرفه ' + money(line.unitPrice)) + ' · مبلغ ' + money(line.netAmount)));
                section.append(details);
            } else section.append(node('p','جزئیات تاریخی ثبت نشده است.','muted'));
            return section;
        }));
        if (!account.invoices.length) $('billing-invoices').append(node('p','صورتحسابی صادر نشده است.','muted'));
        const labels = {usage:'مصرف',credit:'شارژ تأییدشده',payment:'تسویه تأییدشده','credit-note':'اصلاح اعتباری'};
        $('billing-ledger').replaceChildren(...account.entries.filter(e => e.kind !== 'receipt').slice().reverse().map(entry => {
            const item = node('li'); item.append(node('b',(labels[entry.kind] || 'ثبت حساب') + ' · ' + money(entry.amount)),
                node('p',entry.reason || entry.reference), node('small',new Date(entry.at).toLocaleString('fa-IR') + ' · ' + (entry.metric || '')));
            return item;
        }));
        if (form && !pending) {
            form.elements.mode.value = account.mode; form.elements.currency.value = account.currency; form.elements.creditLimit.value = account.creditLimit;
            form.elements.invoiceId.replaceChildren(new Option('بدون صورتحساب',''),...account.invoices.map(i => new Option(i.period,i.id)));
            form.elements.chargeId.replaceChildren(new Option('انتخاب مصرف',''),...account.entries.filter(e => e.kind === 'usage')
                .map(e => new Option((e.productKey || '') + ' · ' + money(e.amount) + ' · ' + e.id,e.id)));
        }
        editor();
    }
    async function load() {
        try { const summary = await request(lifecycle + '/account'); const current = await request(); view = current; view.recorded = summary.status === 'ready'; render(); message(pending ? 'نتیجهٔ ثبت قبلی نامشخص است؛ همان درخواست را پیگیری کنید.' : (view.recorded ? 'حساب به‌روز است.' : 'حساب هنوز ثبت نشده است.')); }
        catch (error) { view = null; for (const id of ['billing-metrics','billing-tariffs','billing-invoices','billing-ledger']) $(id).replaceChildren(); $('billing-mode').textContent = ''; message(error.message,true); }
    }
    $('billing-refresh').onclick = load;
    if (form) {
        form.elements.operation.onchange = editor;
        form.onsubmit = async event => {
            event.preventDefault(); if (!view || sending) return;
            if (!pending) {
                const values = new FormData(form), operation = values.get('operation');
                const data = {expectedRevision:view.account.revision,key:crypto.randomUUID(),reference:values.get('reference')};
                let url = endpoint;
                if (operation === 'invoice') { data.period = values.get('period'); url = lifecycle + '/invoices'; }
                else if (operation === 'credit-note') {
                    Object.assign(data,{chargeId:values.get('chargeId'),invoiceId:values.get('invoiceId') || null,amount:values.get('amount'),reason:values.get('reason')});
                    url = lifecycle + '/credit-notes';
                } else {
                    Object.assign(data,{operation,quantity:'0'});
                    for (const key of ['mode','currency','productKey','metric','period','invoiceId']) data[key] = values.get(key) || null;
                    for (const key of ['creditLimit','unitPrice','amount']) data[key] = values.get(key) || '0';
                }
                pending = {url,body:JSON.stringify(data)}; persist();
            }
            sending = true; editor();
            try {
                view = await request(pending.url,{method:'POST',headers:{'Content-Type':'application/json','X-CSRF-TOKEN':root.querySelector('[name=__RequestVerificationToken]').value},body:pending.body});
                pending = null; persist(); view.recorded = true; render(); message('ثبت شد.');
            } catch (error) {
                if (error.definite) { pending = null; persist(); }
                message(pending ? 'نتیجهٔ ثبت نامشخص است؛ پیگیری با همان شناسه انجام می‌شود.' : error.message,true);
            } finally { sending = false; editor(); }
        };
    }
    load();
})();
