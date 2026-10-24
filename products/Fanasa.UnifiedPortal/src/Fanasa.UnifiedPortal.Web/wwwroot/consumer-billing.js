(function (root) {
    'use strict';
    const max = 79228162514264337593543950335n;
    function decimal(value) {
        if (typeof value !== 'string' || value.length > 64 || !/^(0|[1-9]\d*)(?:\.\d+)?$/.test(value)) throw Error('Invalid money.');
        const [whole, fraction = ''] = value.split('.');
        let coefficient = BigInt(whole + fraction), scale = fraction.length;
        while (scale > 0 && coefficient % 10n === 0n) { coefficient /= 10n; scale--; }
        if (scale > 28 || coefficient > max) throw Error('Inexact money.');
        return { coefficient, scale };
    }
    function amount(value, currency) {
        const number = decimal(value);
        if (!['IRR', 'USD', 'EUR'].includes(currency) || number.scale > (currency === 'IRR' ? 0 : 2)) throw Error('Invalid currency precision.');
        return number;
    }
    function formatAmount(value) {
        decimal(value);
        const [whole, fraction] = value.split('.');
        return whole.replace(/\B(?=(\d{3})+(?!\d))/g, ',') + (fraction === undefined ? '' : '.' + fraction);
    }
    function org(value) {
        if (typeof value !== 'string' || !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value)
            || value === '00000000-0000-0000-0000-000000000000') throw Error('Invalid organization.');
        return value.toLowerCase();
    }
    function utc(value) {
        const match = typeof value === 'string' && /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.(\d{1,7}))?(Z|\+00:00)$/.exec(value);
        if (!match) throw Error('Invalid UTC time.');
        const date = new Date(value), fields = match.slice(1, 7).map(Number);
        if (!Number.isFinite(date.getTime()) || date.getUTCFullYear() !== fields[0] || date.getUTCMonth() + 1 !== fields[1]
            || date.getUTCDate() !== fields[2] || date.getUTCHours() !== fields[3] || date.getUTCMinutes() !== fields[4]
            || date.getUTCSeconds() !== fields[5]) throw Error('Invalid UTC time.');
        return date;
    }
    function units(value, currency) {
        const parsed = amount(value, currency), scale = currency === 'IRR' ? 0 : 2;
        return parsed.coefficient * 10n ** BigInt(scale - parsed.scale);
    }
    function validate(report, organizationId) {
        if (!report || org(report.organizationId) !== org(organizationId) || !['ready', 'notRecorded', 'unavailable'].includes(report.status)
            || !Array.isArray(report.invoices) || report.invoices.length > 1000) throw Error('Invalid account.');
        if (report.status !== 'ready') {
            if (report.asOf !== null || report.mode !== null || report.currency !== null || report.walletBalance !== null
                || report.outstanding !== null || report.availableCredit !== null || report.invoices.length) throw Error('Nonready account contains money.');
            return report;
        }
        utc(report.asOf);
        if (!['payg', 'postpaid'].includes(report.mode)) throw Error('Invalid billing mode.');
        for (const value of [report.walletBalance, report.outstanding, report.availableCredit]) amount(value, report.currency);
        const ids = new Set(), periods = new Set();
        for (const invoice of report.invoices) {
            if (!invoice || !['issued', 'partiallyPaid', 'paid', 'credited', 'statement'].includes(invoice.status)) throw Error('Invalid invoice.');
            const id = org(invoice.id);
            if (ids.has(id) || periods.has(invoice.period) || !/^\d{4}-(0[1-9]|1[0-2])$/.test(invoice.period)) throw Error('Duplicate or invalid invoice.');
            ids.add(id); periods.add(invoice.period); utc(invoice.issuedAt);
            if ((invoice.from === null) !== (invoice.to === null)) throw Error('Partial invoice period.');
            if (invoice.from !== null) {
                const from = utc(invoice.from), to = utc(invoice.to);
                if (!/T00:00:00(?:\.0{1,7})?(Z|\+00:00)$/.test(invoice.from) || !/T00:00:00(?:\.0{1,7})?(Z|\+00:00)$/.test(invoice.to)
                    || from.toISOString().slice(0, 7) !== invoice.period || from.getUTCDate() !== 1 || from.getUTCHours() || from.getUTCMinutes()
                    || from.getUTCSeconds() || from.getUTCMilliseconds() || to <= from || to.getUTCDate() !== 1 || to.getUTCHours()
                    || to.getUTCMinutes() || to.getUTCSeconds() || to.getUTCMilliseconds()
                    || to.getUTCFullYear() * 12 + to.getUTCMonth() !== from.getUTCFullYear() * 12 + from.getUTCMonth() + 1)
                    throw Error('Invalid invoice period.');
            }
            if (units(invoice.amount, report.currency) !== units(invoice.creditedAmount, report.currency)
                + units(invoice.paidAmount, report.currency) + units(invoice.remainingAmount, report.currency)) throw Error('Invalid invoice balance.');
            const total = units(invoice.amount, report.currency), credited = units(invoice.creditedAmount, report.currency);
            const paid = units(invoice.paidAmount, report.currency), remaining = units(invoice.remainingAmount, report.currency);
            const status = report.mode === 'payg' ? 'statement' : remaining === 0n ? (credited === total && credited > 0n ? 'credited' : 'paid') : paid > 0n || credited > 0n ? 'partiallyPaid' : 'issued';
            if (invoice.status !== status) throw Error('Inconsistent invoice status.');
        }
        return report;
    }
    function renderConsumerAccount(container, report, expectedOrganizationId) {
        if (!container?.ownerDocument || typeof container.replaceChildren !== 'function') throw Error('DOM container required.');
        container.replaceChildren();
        let current;
        try { current = validate(report, expectedOrganizationId); } catch { current = null; }
        const document = container.ownerDocument, state = current?.status ?? 'unavailable';
        const node = (tag, className, value) => {
            const element = document.createElement(tag);
            if (className) element.className = className;
            if (value !== undefined) element.textContent = value;
            return element;
        };
        const section = node('section', 'consumer-account'); section.setAttribute('dir', 'rtl');
        section.setAttribute('aria-label', 'حساب سازمان'); section.append(node('h2', '', 'حساب سازمان'));
        const status = node('p', 'consumer-account-status', state === 'notRecorded' ? 'حسابی ثبت نشده است.' : state === 'ready' ? '' : 'حساب در دسترس نیست.');
        status.setAttribute('role', 'status'); status.setAttribute('aria-live', 'polite'); section.append(status);
        if (state !== 'ready') { container.append(section); return { status: state }; }
        section.append(node('p', 'consumer-account-mode', current.mode === 'payg' ? 'پرداخت بر اساس مصرف' : 'پس‌پرداخت'));
        const time = node('time', 'consumer-account-time', new Intl.DateTimeFormat('fa-IR', { dateStyle: 'short', timeStyle: 'short', timeZone: 'UTC' }).format(utc(current.asOf)) + ' UTC');
        time.setAttribute('datetime', current.asOf); time.setAttribute('title', current.asOf); section.append(time);
        const money = value => {
            const span = node('span'), number = node('bdi', '', formatAmount(value)), currency = node('bdi', '', current.currency);
            number.setAttribute('dir', 'ltr'); currency.setAttribute('dir', 'ltr'); span.append(number, node('span', '', ' '), currency); return span;
        };
        const summary = node('dl', 'workspace-usage-summary');
        for (const [label, value] of [['ماندهٔ اعتبار', current.walletBalance], ['بدهی', current.outstanding], ['اعتبار قابل مصرف', current.availableCredit]]) {
            const pair = node('div', 'workspace-usage-stat'), dd = node('dd'); dd.append(money(value));
            pair.append(node('dt', '', label), dd); summary.append(pair);
        }
        const payg = current.mode === 'payg';
        section.append(summary, node('h3', '', payg ? 'صورت مصرف' : 'صورتحساب‌ها'));
        if (current.invoices.length === 0) section.append(node('p', '', 'صورتحسابی صادر نشده است.'));
        else {
            const scroll = node('div', 'workspace-table-container'); scroll.setAttribute('tabindex', '0');
            scroll.setAttribute('role', 'region'); scroll.setAttribute('aria-label', 'صورتحساب‌های سازمان');
            const table = node('table', 'workspace-table'), thead = node('thead'), header = node('tr');
            for (const title of (payg ? ['دوره', 'مبلغ مصرف', 'بستانکاری', 'وضعیت'] : ['دوره', 'مبلغ', 'بستانکاری', 'پرداخت', 'مانده', 'وضعیت'])) {
                const th = node('th', '', title); th.setAttribute('scope', 'col'); header.append(th);
            }
            thead.append(header); table.append(thead); const tbody = node('tbody');
            const labels = { issued: 'صادرشده', partiallyPaid: 'پرداخت بخشی', paid: 'تسویه‌شده', credited: 'بستانکارشده' };
            for (const invoice of current.invoices) {
                const tr = node('tr'); tr.append(node('td', '', invoice.period));
                for (const key of (payg ? ['amount', 'creditedAmount'] : ['amount', 'creditedAmount', 'paidAmount', 'remainingAmount'])) {
                    const td = node('td'); td.append(money(invoice[key])); tr.append(td);
                }
                tr.append(node('td', '', payg && invoice.status !== 'credited' ? 'از اعتبار کسر شده' : labels[invoice.status])); tbody.append(tr);
            }
            table.append(tbody); scroll.append(table); section.append(scroll);
        }
        container.append(section); return { status: 'ready' };
    }
    const api = Object.freeze({ renderConsumerAccount, formatAmount });
    if (typeof module !== 'undefined' && module.exports) module.exports = api;
    else root.FanasaConsumerBilling = api;
}(typeof globalThis !== 'undefined' ? globalThis : this));
