(function (root) {
    'use strict';

    const decimalMax = 79228162514264337593543950335n;
    const states = new Set(['ready', 'notRecorded', 'incomplete', 'unavailable']);
    const messages = Object.freeze({
        ready: 'گزارش مصرف آماده است.',
        notRecorded: 'هنوز گزارشی ثبت نشده است.',
        incomplete: 'گزارش مصرف کامل نیست.',
        unavailable: 'گزارش مصرف در دسترس نیست.'
    });
    const dateFormat = new Intl.DateTimeFormat('fa-IR', { dateStyle: 'short', timeStyle: 'short', timeZone: 'UTC' });

    // Money never passes through Number or a locale formatter that rounds it.
    function exactDecimal(value) {
        if (typeof value !== 'string' || value.length > 64 || !/^(0|[1-9]\d*)(?:\.\d+)?$/.test(value))
            throw new TypeError('Expected a nonnegative plain decimal string.');
        const [integer, fraction = ''] = value.split('.');
        let coefficient = BigInt(integer + fraction), scale = fraction.length;
        while (scale > 0 && coefficient % 10n === 0n) { coefficient /= 10n; scale--; }
        if (scale > 28 || coefficient > decimalMax) throw new RangeError('Decimal is outside the exact report range.');
        return { value, coefficient, scale };
    }

    function formatExactDecimal(value) {
        exactDecimal(value);
        const [integer, fraction] = value.split('.');
        return integer.replace(/\B(?=(\d{3})+(?!\d))/g, ',') + (fraction === undefined ? '' : '.' + fraction);
    }

    function text(value, max = 200) {
        if (typeof value !== 'string' || !value.trim() || value.length > max || /[\u0000-\u001f\u007f-\u009f]/.test(value))
            throw new TypeError('Expected report text.');
        return value;
    }

    function organization(value) {
        if (typeof value !== 'string' || !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(value)
            || value === '00000000-0000-0000-0000-000000000000') throw new TypeError('Expected an organization identifier.');
        return value.toLowerCase();
    }

    function utcDate(value) {
        if (typeof value !== 'string') throw new TypeError('Expected a UTC report time.');
        const match = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.\d{1,7})?(?:Z|\+00:00)$/.exec(value);
        if (!match) throw new TypeError('Expected a UTC report time.');
        const date = new Date(value), parts = match.slice(1).map(Number);
        if (!Number.isFinite(date.getTime()) || date.getUTCFullYear() !== parts[0]
            || date.getUTCMonth() + 1 !== parts[1] || date.getUTCDate() !== parts[2]
            || date.getUTCHours() !== parts[3] || date.getUTCMinutes() !== parts[4]
            || date.getUTCSeconds() !== parts[5]) throw new TypeError('Invalid report time.');
        return date;
    }

    function utcTicks(value) {
        const date = utcDate(value), fraction = /\.(\d{1,7})(?:Z|\+00:00)$/.exec(value);
        return BigInt(Math.floor(date.getTime() / 1000)) * 10000000n
            + BigInt(fraction ? fraction[1].padEnd(7, '0') : '0');
    }

    function money(value, currency) {
        const amount = exactDecimal(value);
        if (amount.scale > (currency === 'IRR' ? 0 : 2)) throw new TypeError('Invalid currency precision.');
        return amount;
    }

    function checkReport(report, expectedOrganizationId) {
        if (!report || organization(report.organizationId) !== organization(expectedOrganizationId)
            || !states.has(report.status) || report.basis !== 'posted-ledger' || !Array.isArray(report.items)
            || report.items.length > 10000) throw new TypeError('Invalid report or scope.');
        const from = utcTicks(report.from), to = utcTicks(report.to);
        if (from >= to || to - from > 366n * 24n * 60n * 60n * 10000000n) throw new RangeError('Invalid report period.');
        text(report.periodLabel);
        if (report.status !== 'ready') {
            if (report.items.length !== 0 || report.totalAmount !== null || report.currency !== null || report.asOf !== null)
                throw new TypeError('Nonready report contains financial values.');
            return report;
        }
        utcDate(report.asOf);
        if (!['IRR', 'USD', 'EUR'].includes(report.currency)) throw new TypeError('Invalid report currency.');
        const total = money(report.totalAmount, report.currency), amounts = [];
        let chargeCount = 0;
        const groups = new Set();
        for (const item of report.items) {
            if (!item) throw new TypeError('Invalid report item.');
            text(item.productKey); text(item.name); text(item.meterId);
            const legacy = item.quantity === null && item.unit === null && item.meterVersion === null;
            if (!legacy) {
                text(item.unit); text(item.meterVersion);
                if (exactDecimal(item.quantity).coefficient === 0n) throw new TypeError('Measured quantity must be positive.');
            }
            if (!Number.isSafeInteger(item.chargeCount) || item.chargeCount < 1 || item.chargeCount > 10000)
                throw new TypeError('Invalid charge count.');
            chargeCount += item.chargeCount;
            if (chargeCount > 10000) throw new TypeError('Report charge count exceeds its limit.');
            amounts.push(money(item.amount, report.currency));
            const group = JSON.stringify([item.productKey, item.meterId, item.meterVersion, item.unit]);
            if (groups.has(group)) throw new TypeError('Duplicate report group.');
            groups.add(group);
        }
        const scale = Math.max(total.scale, ...amounts.map(value => value.scale));
        const sum = amounts.reduce((value, amount) => value + amount.coefficient * 10n ** BigInt(scale - amount.scale), 0n);
        if (sum !== total.coefficient * 10n ** BigInt(scale - total.scale)) throw new TypeError('Report total does not match its rows.');
        return report;
    }

    function renderUsageReport(container, report, expectedOrganizationId) {
        if (!container || typeof container.replaceChildren !== 'function' || !container.ownerDocument)
            throw new TypeError('Expected a DOM container.');
        // Erase the previous organization's data before validating any new response.
        container.replaceChildren();
        let current;
        try { current = checkReport(report, expectedOrganizationId); }
        catch { current = null; }
        const state = current ? current.status : 'unavailable', document = container.ownerDocument;
        function node(tag, className, value) {
            const element = document.createElement(tag);
            if (className) element.className = className;
            if (value !== undefined) element.textContent = value;
            return element;
        }
        function isolate(value, direction = 'ltr') {
            const element = node('bdi', '', value); element.setAttribute('dir', direction); return element;
        }
        function time(value) {
            const element = node('time', '', dateFormat.format(utcDate(value)) + ' UTC');
            element.setAttribute('datetime', value); element.setAttribute('title', value); return element;
        }
        const section = node('section', 'consumer-usage-report');
        section.setAttribute('dir', 'rtl'); section.setAttribute('aria-label', 'مصرف سازمان');
        section.append(node('h2', 'consumer-usage-heading', 'مصرف سازمان'));
        const status = node('p', 'consumer-usage-status' + (state === 'ready' ? ' consumer-usage-visually-hidden' : ''), messages[state]);
        status.setAttribute('role', 'status'); status.setAttribute('aria-live', 'polite'); status.setAttribute('aria-atomic', 'true');
        section.append(status);
        if (current) {
            const period = node('p', 'consumer-usage-period', current.periodLabel);
            period.setAttribute('data-from', current.from); period.setAttribute('data-to', current.to);
            period.setAttribute('title', 'از ' + current.from + ' تا پیش از ' + current.to); section.append(period);
        }
        if (state !== 'ready') {
            container.append(section);
            return Object.freeze({ status: state, itemCount: 0 });
        }
        const summary = node('dl', 'consumer-usage-summary');
        function statistic(label, content) {
            const pair = node('div', 'consumer-usage-stat'), value = node('dd');
            pair.append(node('dt', '', label));
            if (typeof content === 'string') value.textContent = content; else value.append(content);
            pair.append(value); summary.append(pair);
        }
        const cost = node('span');
        cost.append(isolate(formatExactDecimal(current.totalAmount)), node('span', '', ' '), isolate(current.currency));
        statistic('هزینهٔ ثبت‌شده', cost);
        statistic('سرویس‌های دارای مصرف', new Intl.NumberFormat('fa-IR').format(new Set(current.items.map(item => item.productKey)).size));
        statistic('آخرین گزارش', time(current.asOf));
        section.append(summary);
        if (current.items.length === 0) section.append(node('p', 'consumer-usage-empty', 'هزینهٔ ثبت‌شده در این دوره صفر است.'));
        else {
            const scroll = node('div', 'consumer-usage-table-scroll');
            scroll.setAttribute('tabindex', '0'); scroll.setAttribute('role', 'region'); scroll.setAttribute('aria-label', 'جزئیات مصرف سازمان');
            const table = node('table', 'consumer-usage-table');
            table.append(node('caption', 'consumer-usage-visually-hidden', 'جزئیات مصرف سازمان'));
            const header = node('thead'), headerRow = node('tr');
            for (const label of ['خدمت', 'شاخص مصرف', 'مصرف', 'هزینهٔ ثبت‌شده', 'تعداد ثبت']) {
                const column = node('th', '', label); column.setAttribute('scope', 'col'); headerRow.append(column);
            }
            header.append(headerRow); table.append(header);
            const rows = node('tbody');
            for (const item of current.items) {
                const row = node('tr'), service = node('td'), meter = node('td'), quantity = node('td'), amount = node('td'), count = node('td');
                service.append(node('strong', '', item.name));
                meter.append(isolate(item.meterId));
                if (item.meterVersion !== null) {
                    const version = node('small', 'consumer-usage-secondary', 'نسخه '); version.append(isolate(item.meterVersion)); meter.append(version);
                }
                if (item.quantity === null) quantity.textContent = 'نامعلوم';
                else {
                    quantity.append(isolate(formatExactDecimal(item.quantity)));
                    const unit = node('small', 'consumer-usage-secondary'); unit.append(isolate(item.unit, 'auto')); quantity.append(unit);
                }
                amount.append(isolate(formatExactDecimal(item.amount)), node('span', '', ' '), isolate(current.currency));
                count.textContent = new Intl.NumberFormat('fa-IR').format(item.chargeCount);
                row.append(service, meter, quantity, amount, count); rows.append(row);
            }
            table.append(rows); scroll.append(table); section.append(scroll);
        }
        container.append(section);
        return Object.freeze({ status: state, itemCount: current.items.length });
    }

    const api = Object.freeze({ renderUsageReport, formatExactDecimal });
    if (typeof module !== 'undefined' && module.exports) module.exports = api;
    else root.FanasaConsumerUsageReport = api;
}(typeof globalThis !== 'undefined' ? globalThis : this));
