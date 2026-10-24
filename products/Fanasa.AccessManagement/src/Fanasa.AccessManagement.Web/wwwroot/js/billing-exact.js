(function (scope) {
    'use strict';
    const names = new Set(['walletBalance', 'outstanding', 'availableCredit', 'creditLimit', 'unitPrice', 'quantity',
        'amount', 'grossAmount', 'creditAmount', 'netAmount', 'revision', 'sourceRevision']);
    function parse(text) {
        if (typeof text !== 'string' || text.length > 8 * 1024 * 1024) throw Error('پاسخ حساب معتبر نیست.');
        return JSON.parse(text.replace(/("(?:\\.|[^"\\])*")(\s*:\s*)(-?\d+(?:\.\d+)?(?:[eE][+-]?\d+)?)/g,
            (all, key, colon, number) => names.has(JSON.parse(key)) ? key + colon + JSON.stringify(number) : all));
    }
    const canonical = value => {
        if (typeof value !== 'string' || !/^-?\d{1,29}(?:\.\d{1,28})?$/.test(value)) throw Error('مقدار دقیق در دسترس نیست.');
        return value;
    };
    function units(value, scale) {
        const text = canonical(value), negative = text.startsWith('-'), [whole, fraction = ''] = text.replace(/^-/, '').split('.');
        if (fraction.length > scale && /[1-9]/.test(fraction.slice(scale))) throw Error('دقت مبلغ معتبر نیست.');
        const result = BigInt(whole) * 10n ** BigInt(scale) + BigInt((fraction.slice(0, scale)).padEnd(scale, '0') || '0');
        return negative ? -result : result;
    }
    function decimal(value, scale) {
        const negative = value < 0n, digits = (negative ? -value : value).toString().padStart(scale + 1, '0');
        return (negative ? '-' : '') + (scale ? digits.slice(0, -scale) + '.' + digits.slice(-scale) : digits);
    }
    const persian = value => value.replace(/[0-9]/g, digit => '۰۱۲۳۴۵۶۷۸۹'[digit]);
    function format(value) {
        const text = canonical(value), negative = text.startsWith('-'), [whole, fraction] = text.replace(/^-/, '').split('.');
        return (negative ? '−' : '') + new Intl.NumberFormat('fa-IR').format(BigInt(whole)) + (fraction ? '٫' + persian(fraction) : '');
    }
    const api = Object.freeze({parse, units, decimal, format});
    scope.FanasaBillingExact = api;
    if (typeof module !== 'undefined') module.exports = api;
})(globalThis);
