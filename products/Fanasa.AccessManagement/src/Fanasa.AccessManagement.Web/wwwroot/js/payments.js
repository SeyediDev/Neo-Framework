(() => {
    'use strict';
    const root = document.getElementById('accounting-app'), form = document.getElementById('gateway-payment');
    if (!root || !form) return;
    const endpoint = '/api/payments/zarinpal/tenants/' + root.dataset.tenant;
    const message = document.getElementById('gateway-message');
    let requestId;
    async function call(url, body) {
        const response = await fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json', 'X-CSRF-TOKEN': root.querySelector('[name=__RequestVerificationToken]').value }, body: JSON.stringify(body) });
        const data = await response.json();
        if (!response.ok) throw Error(data.error || 'پرداخت انجام نشد.');
        return data;
    }
    async function load() {
        try {
            const response = await fetch('/api/accounting/tenants/' + root.dataset.tenant);
            if (!response.ok) return;
            const view = await response.json(), account = view.account;
            document.getElementById('gateway-invoice-label').hidden = account.mode !== 'postpaid';
            form.elements.invoiceId.replaceChildren(new Option('انتخاب صورتحساب', ''), ...account.invoices.map(i => new Option(i.period, i.id)));
            const attempts = await fetch(endpoint);
            if (!attempts.ok) return;
            const list = await attempts.json();
            const cards = list.map(p => {
                const row = document.createElement('p');
                const labels = { requesting: 'درخواست نامشخص؛ نیازمند تطبیق پذیرنده', pending: 'در انتظار تأیید', verified: 'تأییدشده؛ نیازمند ثبت در حساب', paid: 'ثبت‌شده در حساب', reversed: 'برگشت تأییدشده؛ رزرو آزاد شد' };
                row.textContent = p.amount.toLocaleString('fa-IR') + ' ریال · ' + labels[p.state] + (p.refId ? ' · پیگیری: ' + p.refId : '');
                if (p.providerStatus) {
                    const status = document.createElement('span');
                    const providerLabels = { PAID: 'پرداخت‌شده در درگاه', VERIFIED: 'تأییدشده در درگاه', IN_BANK: 'در حال پرداخت بانکی', FAILED: 'ناموفق؛ رزرو محفوظ', REVERSED: 'برگشت درگاه' };
                    status.textContent = ' · ' + providerLabels[p.providerStatus]; row.append(status);
                    if (p.providerStatus === 'REVERSED' && p.refId) row.append(' · نیازمند تطبیق دفتر مالی');
                }
                if (p.state !== 'requesting' && p.state !== 'reversed') {
                    const inquiry = document.createElement('button'); inquiry.className = 'button secondary'; inquiry.textContent = 'استعلام و تطبیق';
                    inquiry.onclick = async () => {
                        inquiry.disabled = true;
                        try { const result = await call(endpoint + '/' + p.id + '/reconcile', {}); message.textContent = result.state === 'paid' ? 'پرداخت در حساب ثبت شده است.' : result.state === 'reversed' ? 'برگشت تأیید و رزرو آزاد شد.' : 'وضعیت درگاه بررسی شد؛ رزرو پرداخت محفوظ است.'; document.getElementById('billing-refresh').click(); await load(); }
                        catch (e) { message.textContent = e.message; } finally { inquiry.disabled = false; }
                    }; row.append(inquiry);
                }
                if (p.state === 'pending' || p.state === 'verified') {
                    const retry = document.createElement('button'); retry.className = 'button secondary'; retry.textContent = 'بررسی و ثبت پرداخت';
                    retry.onclick = async () => { retry.disabled = true; try { await call(endpoint + '/' + p.id + '/retry', {}); message.textContent = 'پرداخت ثبت شد.'; document.getElementById('billing-refresh').click(); await load(); } catch (e) { message.textContent = e.message; } finally { retry.disabled = false; } };
                    row.append(retry);
                }
                return row;
            });
            document.getElementById('gateway-attempts').replaceChildren(...cards);
        } catch { message.textContent = 'وضعیت پرداخت‌ها در دسترس نیست.'; }
    }
    form.oninput = () => { requestId = null; };
    form.onsubmit = async event => {
        event.preventDefault(); requestId ||= crypto.randomUUID();
        const button = form.querySelector('button'); button.disabled = true;
        try {
            const data = await call(endpoint, { requestId, amount: form.elements.amount.value, invoiceId: document.getElementById('gateway-invoice-label').hidden ? null : form.elements.invoiceId.value || null });
            if (data.paymentUrl) location.assign(data.paymentUrl);
            else { message.textContent = data.state === 'paid' ? 'این پرداخت قبلاً ثبت شده است.' : 'وضعیت درخواست نیازمند بررسی است؛ پرداخت جدید ایجاد نشد.'; await load(); }
        } catch (e) { message.textContent = e.message; await load(); }
        finally { button.disabled = false; }
    };
    document.getElementById('billing-refresh').addEventListener('click', load);
    load();
})();
