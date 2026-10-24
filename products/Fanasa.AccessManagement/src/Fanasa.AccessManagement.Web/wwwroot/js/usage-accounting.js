(function () {
    'use strict';

    const uuidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
    const digestPattern = /^[0-9a-f]{64}$/;
    const ledgerPattern = /^measured:v1:[0-9a-f]{64}$/;
    const isUuid = value => typeof value === 'string' && uuidPattern.test(value) && value !== '00000000-0000-0000-0000-000000000000';

    // Wrap amount number tokens before parsing. JSON strings, including escaped property-like text,
    // are copied intact; a decimal never passes through a JavaScript Number.
    function parseReportJson(raw) {
        if (typeof raw !== 'string') throw new TypeError('پاسخ گزارش معتبر نیست.');
        let output = '', i = 0;
        while (i < raw.length) {
            if (raw[i] !== '"') { output += raw[i++]; continue; }
            const start = i++;
            while (i < raw.length) {
                if (raw[i] === '\\') { i += 2; continue; }
                if (raw[i++] === '"') break;
            }
            const literal = raw.slice(start, i); output += literal;
            if (JSON.parse(literal) !== 'amount') continue;
            let next = i; while (/\s/.test(raw[next] || '') && next < raw.length) next++;
            if (raw[next] !== ':') continue;
            next++; while (/\s/.test(raw[next] || '') && next < raw.length) next++;
            const number = /^-?(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?/.exec(raw.slice(next));
            if (!number) continue;
            output += raw.slice(i, next) + JSON.stringify(number[0]); i = next + number[0].length;
        }
        return JSON.parse(output);
    }

    function decimalText(value) {
        if (typeof value === 'number') {
            if (!Number.isSafeInteger(value) || value < 0) throw new TypeError('مبلغ دقیق در پاسخ در دسترس نیست.');
            value = String(value);
        }
        if (typeof value !== 'string' || !/^(?:0|[1-9]\d*)(?:\.\d+)?(?:[eE][+-]?\d+)?$/.test(value))
            throw new TypeError('مبلغ گزارش معتبر نیست.');
        const parts = value.toLowerCase().split('e');
        if (parts.length === 1) return parts[0];
        const exponent = Number(parts[1]);
        if (!Number.isInteger(exponent) || Math.abs(exponent) > 100) throw new TypeError('مبلغ گزارش خارج از محدوده است.');
        const [integer, fraction = ''] = parts[0].split('.'); const digits = integer + fraction; const point = integer.length + exponent;
        if (point <= 0) return '0.' + '0'.repeat(-point) + digits;
        if (point >= digits.length) return (digits + '0'.repeat(point - digits.length)).replace(/^0+(?=\d)/, '');
        return (digits.slice(0, point) + '.' + digits.slice(point)).replace(/^0+(?=\d)/, '');
    }

    function formatDecimal(value) {
        const [integer, fraction] = decimalText(value).split('.');
        return integer.replace(/\B(?=(\d{3})+(?!\d))/g, ',') + (fraction ? '.' + fraction : '');
    }

    function validateReason(value) {
        const reason = typeof value === 'string' ? value.trim() : '';
        if (!reason || reason.length > 1000) throw new TypeError('دلیل بازبینی باید بین ۱ تا ۱۰۰۰ نویسه باشد.');
        return reason;
    }

    function validOperation(operation) {
        try {
            decimalText(operation?.amount);
            return operation && ledgerPattern.test(operation.ledgerKey || '') && digestPattern.test(operation.operationFingerprint || '')
                && ['committed', 'accounting-pending'].includes(operation.status)
                && ['IRR', 'USD', 'EUR'].includes(operation.currency) && typeof operation.occurredAt === 'string'
                && Number.isFinite(Date.parse(operation.occurredAt));
        } catch { return false; }
    }

    function canRecover(report, operation, fresh) {
        if (!fresh || !report || report.coverage !== 'complete' || report.integrityVerified !== true || report.canRecoverPending !== true
            || !validOperation(operation) || operation.status !== 'accounting-pending' || !Array.isArray(report.operations)
            || !Array.isArray(report.findings) || report.findings.some(finding => finding.code !== 'accounting-pending')) return false;
        return report.operations.some(current => current.ledgerKey === operation.ledgerKey && current.operationFingerprint === operation.operationFingerprint
            && current.status === operation.status && current.amount === operation.amount && current.currency === operation.currency
            && current.occurredAt === operation.occurredAt);
    }

    function createRecoveryCommand(consumer, operation, reason, requestId) {
        if (!isUuid(consumer) || !isUuid(requestId) || !validOperation(operation) || operation.status !== 'accounting-pending')
            throw new TypeError('شناسه سازمان، عملیات یا درخواست بازبینی معتبر نیست.');
        return Object.freeze({ consumerTenantId: consumer, businessKey: operation.ledgerKey,
            expectedOperationFingerprint: operation.operationFingerprint, requestId, reason: validateReason(reason) });
    }

    function classifyRecoveryFailure(status) {
        const messages = {
            400: 'درخواست یا نشست معتبر نیست. اطلاعات را بررسی کنید و گزارش را تازه کنید.',
            401: 'نشست پایان یافته است. دوباره وارد شوید.',
            403: 'مجوز بازبینی حسابداری ندارید یا دسترسی شما تغییر کرده است.',
            404: 'شواهد نگه‌داری‌شده پیدا نشد. گزارش را تازه کنید.',
            409: 'شواهد یا شناسه درخواست تعارض دارد. پیش از تلاش بعدی گزارش را تازه کنید.'
        };
        const uncertain = !Object.prototype.hasOwnProperty.call(messages, status);
        return { uncertain, stale: !uncertain, message: uncertain
            ? 'نتیجه درخواست مشخص نیست. برای بررسی نتیجه، فقط همین درخواست با همان شناسه و دلیل را دوباره ارسال کنید.'
            : messages[status] || 'درخواست پذیرفته نشد. گزارش را تازه کنید.' };
    }

    function createRecoverySession(idFactory) {
        let state = 'idle', command = null, receipt = null;
        const nextId = idFactory || (() => {
            if (typeof globalThis.crypto?.randomUUID !== 'function') throw new Error('مرورگر شناسه امن درخواست را پشتیبانی نمی‌کند.');
            return globalThis.crypto.randomUUID();
        });
        return {
            prepare(consumer, operation, reason) {
                if (state === 'posting') throw new Error('درخواست در حال ارسال است.');
                if (state === 'uncertain') {
                    if (command.consumerTenantId !== consumer || command.businessKey !== operation?.ledgerKey
                        || command.expectedOperationFingerprint !== operation?.operationFingerprint || command.reason !== validateReason(reason))
                        throw new Error('تا تعیین نتیجه، عملیات و دلیل درخواست نامشخص را تغییر ندهید.');
                    state = 'posting'; return command;
                }
                command = createRecoveryCommand(consumer, operation, reason, nextId()); receipt = null; state = 'posting'; return command;
            },
            markUncertain() { if (!command) throw new Error('درخواستی برای بررسی نتیجه وجود ندارد.'); state = 'uncertain'; },
            settle(value) {
                if (!command || !value || value.requestId !== command.requestId || value.consumerTenantId !== command.consumerTenantId
                    || value.businessKey !== command.businessKey || value.operationFingerprint !== command.expectedOperationFingerprint
                    || value.reason !== command.reason || !['committed', 'accounting-pending'].includes(value.status)) {
                    if (command) state = 'uncertain'; throw new Error('پاسخ دریافتی با درخواست بازبینی تطبیق ندارد؛ نتیجه هنوز مشخص نیست.');
                }
                receipt = Object.freeze({ ...value }); command = null; state = value.status; return receipt;
            },
            rejectKnown() { command = null; receipt = null; state = 'idle'; },
            resetKnown() {
                if (state === 'posting' || state === 'uncertain') throw new Error('ابتدا نتیجه درخواست فعلی را مشخص کنید.');
                command = null; receipt = null; state = 'idle';
            },
            restoreUncertain(value) {
                if (state !== 'idle' || !value) throw new Error('درخواست محفوظ قابل بازیابی نیست.');
                command = createRecoveryCommand(value.consumerTenantId, {
                    ledgerKey: value.businessKey, operationFingerprint: value.expectedOperationFingerprint,
                    status: 'accounting-pending', amount: '0', currency: 'IRR', occurredAt: '2000-01-01T00:00:00Z'
                }, value.reason, value.requestId);
                state = 'uncertain';
            },
            snapshot() { return Object.freeze({ state, command, receipt }); }
        };
    }

    const exported = { parseReportJson, decimalText, formatDecimal, validateReason, canRecover, createRecoveryCommand,
        classifyRecoveryFailure, createRecoverySession };
    if (typeof module !== 'undefined' && module.exports) module.exports = exported;
    if (typeof document === 'undefined') return;
    const root = document.getElementById('usage-accounting-app'); if (!root) return;

    const byId = id => document.getElementById('ua-' + id);
    const ui = Object.fromEntries(['refresh', 'load-status', 'banner', 'banner-title', 'banner-text', 'state', 'state-title', 'state-text',
        'content', 'metric-committed', 'metric-pending', 'metric-coverage', 'metric-findings', 'coverage-detail',
        'stage-receipts', 'stage-ledger', 'stage-outbox', 'receipt-step', 'ledger-step', 'outbox-step',
        'pending-count', 'pending-list', 'pending-empty', 'search', 'filter', 'table-body', 'table', 'table-empty',
        'visible-count', 'findings-list', 'findings-empty', 'detail', 'detail-close', 'detail-title', 'detail-status',
        'detail-time', 'detail-amount', 'detail-currency', 'detail-key', 'detail-fingerprint', 'detail-findings',
        'copy-key', 'copy-fingerprint', 'copy-amount', 'recovery-form', 'recovery-reason', 'recovery-submit', 'recovery-message',
        'recovery-eligibility', 'recovery-request', 'new-request', 'tenant'].map(id => [id, byId(id)]));
    const tenant = (root.dataset.tenant || '').toLowerCase();
    const endpoint = '/api/usage-accounting/organization/' + encodeURIComponent(tenant);
    const session = createRecoverySession();
    const storageKey = 'fanasa.usage-accounting.recovery.v1.' + tenant;
    const tenantForm = ui.tenant?.closest('form');
    const resumeButton = document.createElement('button');
    resumeButton.type = 'button'; resumeButton.className = 'ua-table-action';
    resumeButton.textContent = 'مشاهدهٔ درخواست محفوظ'; resumeButton.hidden = true;
    ui.banner.append(resumeButton);
    let report = null, fresh = false, loading = false, generation = 0, expiresAt = 0;
    let selected = null, opener = null, recoveryMessage = '', clipboardMessage = '', resultNotice = null, expiryTimer;
    const numberFormat = new Intl.NumberFormat('fa-IR');
    const dateFormat = new Intl.DateTimeFormat('fa-IR', { dateStyle: 'medium', timeStyle: 'short' });
    const currencyNames = { IRR: 'ریال ایران (IRR)', USD: 'دلار آمریکا (USD)', EUR: 'یورو (EUR)' };
    const findingNames = {
        'scan-limit-exceeded': 'همهٔ شواهد در محدودهٔ این بررسی قرار نگرفته‌اند',
        'unattributable-operation': 'سازمان مربوط به بخشی از شواهد مصرف مشخص نیست',
        'unattributable-event-receipt': 'سازمان مربوط به بخشی از رسیدهای مصرف مشخص نیست',
        'unattributable-outbox': 'سازمان مربوط به بخشی از رویدادهای صف مشخص نیست',
        'accounting-pending': 'ثبت مالی این مصرف در انتظار است',
        'missing-accounting-document': 'حساب سازمان برای ثبت مالی موجود نیست',
        'accounting-document-invalid': 'شواهد حساب سازمان نیازمند بررسی است',
        'accounting-tenant-mismatch': 'سازمان ثبت‌شده در حساب مطابقت ندارد',
        'accounting-snapshot-mismatch': 'ارز یا روش حساب با شواهد مصرف مطابقت ندارد',
        'missing-original-event-receipt': 'رسید اصلی مصرف پیدا نشد',
        'missing-ledger-charge': 'ثبت مبلغ مصرف در حساب پیدا نشد',
        'missing-ledger-marker': 'نشان ثبت مصرف در حساب پیدا نشد',
        'missing-committed-outbox': 'رویداد ثبت مالی در صف پیدا نشد',
        'missing-pending-outbox': 'رویداد مصرف در انتظار در صف پیدا نشد',
        'pending-ledger-effects': 'مصرف در انتظار دارای اثر مالی ناسازگار است',
        'pending-committed-outbox': 'مصرف در انتظار دارای رویداد ثبت مالی ناسازگار است',
        'duplicate-operation': 'بیش از یک ثبت برای همین مصرف وجود دارد',
        'duplicate-ledger-charge': 'ثبت مبلغ مصرف تکراری است',
        'duplicate-ledger-marker': 'نشان ثبت مصرف تکراری است',
        'duplicate-ledger-id': 'شناسهٔ ثبت حساب تکراری است',
        'duplicate-event-receipt': 'رسید مصرف تکراری است',
        'duplicate-committed-outbox': 'رویداد ثبت مالی تکراری است',
        'duplicate-pending-outbox': 'رویداد مصرف در انتظار تکراری است',
        'orphan-event-receipt': 'رسید مصرف بدون ثبت مصرف مرتبط است',
        'orphan-usage-outbox': 'رویداد صف بدون ثبت مصرف مرتبط است',
        'orphan-measured-ledger-entry': 'ثبت حساب بدون شواهد مصرف مرتبط است',
        'usage-outbox-mismatch': 'رویداد صف با شواهد مصرف مطابقت ندارد',
        'ledger-charge-mismatch': 'مبلغ یا مشخصات ثبت حساب مطابقت ندارد',
        'ledger-marker-mismatch': 'نشان ثبت حساب با شواهد مصرف مطابقت ندارد',
        'receipt-operation-mismatch': 'رسید با شواهد اصلی مصرف مطابقت ندارد',
        'operation-amount-mismatch': 'مبلغ ثبت مصرف با شواهد محفوظ مطابقت ندارد',
        'operation-fingerprint-mismatch': 'اثر انگشت ثبت مصرف مطابقت ندارد'
    };
    function element(tag, className, value) {
        const node = document.createElement(tag);
        if (className) node.className = className;
        if (value !== undefined) node.textContent = String(value);
        return node;
    }
    function setText(id, value) { ui[id].textContent = String(value); }
    function unresolved() { return ['posting', 'uncertain'].includes(session.snapshot().state); }
    function statusName(operation) { return operation.status === 'committed' ? 'ثبت مالی انجام‌شده' : 'در انتظار ثبت مالی'; }
    function displayTime(value) { return Number.isFinite(Date.parse(value)) ? dateFormat.format(new Date(value)) : 'زمان در دسترس نیست'; }
    function displayAmount(operation) {
        try { return formatDecimal(operation.amount); } catch { return 'مبلغ دقیق در دسترس نیست'; }
    }
    function showBanner(kind, title, message) {
        ui.banner.hidden = false; ui.banner.className = 'ua-banner ua-banner--' + kind;
        setText('banner-title', title); setText('banner-text', message);
        resumeButton.hidden = !unresolved();
    }
    function operationFindings(operation) {
        return report ? report.findings.filter(finding => !finding.ledgerKey || finding.ledgerKey === operation.ledgerKey) : [];
    }
    function issueFindings(operation) { return operationFindings(operation).filter(finding => finding.code !== 'accounting-pending'); }
    function appendFinding(container, finding) {
        const item = element('div', 'ua-finding');
        item.append(element('strong', '', findingNames[finding.code] || 'ناهماهنگی شواهد مصرف؛ نیازمند بررسی'));
        if (finding.ledgerKey) { const key = element('small', 'ua-code', finding.ledgerKey); key.dir = 'ltr'; item.append(key); }
        container.append(item);
    }
    function setStatus(node, operation) {
        const hasIssues = issueFindings(operation).length > 0;
        node.className = 'ua-status ua-status--' + (hasIssues ? 'issue' : operation.status === 'committed' ? 'committed' : 'pending');
        node.textContent = statusName(operation) + (hasIssues ? ' · نیازمند بررسی' : '');
    }
    function mayOpen(operation) {
        const command = session.snapshot().command;
        return !unresolved() || command?.businessKey === operation.ledgerKey;
    }
    function detailButton(operation, label) {
        const button = element('button', 'ua-table-action', label);
        button.type = 'button'; button.disabled = !mayOpen(operation);
        button.setAttribute('aria-label', label + '، ' + operation.ledgerKey);
        button.addEventListener('click', () => openDetails(operation, button));
        return button;
    }
    function validateReport(value) {
        const countKeys = ['operations', 'eventReceipts', 'committed', 'pending', 'ledgerCharges', 'ledgerMarkers',
            'committedOutbox', 'pendingOutbox', 'unattributableRows'];
        if (!value || value.consumerTenantId?.toLowerCase() !== tenant || !['complete', 'incomplete'].includes(value.coverage)
            || !['clean', 'issues', 'incomplete'].includes(value.status) || !value.counts
            || countKeys.some(key => !Number.isSafeInteger(value.counts[key]) || value.counts[key] < 0)
            || typeof value.integrityVerified !== 'boolean' || typeof value.canRecoverPending !== 'boolean'
            || !Array.isArray(value.operations) || !Array.isArray(value.findings)
            || value.operations.some(operation => !validOperation(operation))
            || new Set(value.operations.map(operation => operation.ledgerKey)).size !== value.operations.length
            || value.findings.some(finding => !finding || typeof finding.code !== 'string'
                || finding.ledgerKey != null && typeof finding.ledgerKey !== 'string'))
            throw new Error('ساختار یا سازمان گزارش دریافتی معتبر نیست.');
        const integrity = value.coverage === 'complete' && value.findings.every(finding => finding.code === 'accounting-pending');
        const status = value.coverage !== 'complete' ? 'incomplete' : value.findings.length ? 'issues' : 'clean';
        if (value.integrityVerified !== integrity || value.canRecoverPending !== (integrity && value.counts.pending > 0) || value.status !== status)
            throw new Error('وضعیت گزارش با شواهد دریافتی سازگار نیست.');
        return value;
    }
    function renderTable() {
        if (!report) return;
        const normalize = value => String(value).replace(/[۰-۹]/g, digit => '۰۱۲۳۴۵۶۷۸۹'.indexOf(digit))
            .replace(/[٠-٩]/g, digit => '٠١٢٣٤٥٦٧٨٩'.indexOf(digit)).toLowerCase();
        const query = normalize(ui.search.value.trim()), filter = ui.filter.value;
        const operations = report.operations.filter(operation => {
            if (filter === 'issues' ? issueFindings(operation).length === 0 : filter !== 'all' && filter !== operation.status) return false;
            return !query || normalize([operation.ledgerKey, operation.operationFingerprint, displayAmount(operation), operation.amount,
                operation.currency, statusName(operation), displayTime(operation.occurredAt)].join(' ')).includes(query);
        });
        ui['table-body'].replaceChildren();
        for (const operation of operations) {
            const row = element('tr');
            const keyCell = element('td'), code = element('code', 'ua-code', operation.ledgerKey);
            code.dir = 'ltr'; code.title = operation.ledgerKey; keyCell.append(code);
            const timeCell = element('td', '', displayTime(operation.occurredAt)); timeCell.title = operation.occurredAt;
            const amountCell = element('td', '', displayAmount(operation));
            amountCell.append(element('small', '', currencyNames[operation.currency]));
            const statusCell = element('td'), badge = element('span'); setStatus(badge, operation); statusCell.append(badge);
            const actionCell = element('td'); actionCell.append(detailButton(operation, 'جزئیات'));
            row.append(keyCell, timeCell, amountCell, statusCell, actionCell); ui['table-body'].append(row);
        }
        ui.table.hidden = operations.length === 0; ui['table-empty'].hidden = operations.length !== 0;
        setText('visible-count', numberFormat.format(operations.length) + ' از ' + numberFormat.format(report.operations.length) + ' مصرف');
    }
    function renderReport() {
        if (!report) return;
        const counts = report.counts, issues = report.findings.filter(finding => finding.code !== 'accounting-pending');
        const empty = counts.operations === 0 && counts.eventReceipts === 0 && report.findings.length === 0;
        ui.content.hidden = empty; ui.state.hidden = !empty;
        if (empty) { setText('state-title', 'هنوز مصرفی ثبت نشده است'); setText('state-text', 'مصرف‌های ثبت‌شدهٔ این سازمان پس از دریافت شواهد در این صفحه نمایش داده می‌شوند.'); }
        setText('metric-committed', numberFormat.format(counts.committed)); setText('metric-pending', numberFormat.format(counts.pending));
        setText('metric-findings', numberFormat.format(issues.length)); setText('metric-coverage', report.coverage === 'complete' ? 'کامل' : 'ناقص');
        setText('coverage-detail', report.coverage === 'complete' ? 'همهٔ شواهد در محدودهٔ بررسی' : 'بخشی از شواهد بررسی نشده است');
        setText('stage-receipts', numberFormat.format(counts.eventReceipts) + ' رسید مصرف');
        setText('stage-ledger', numberFormat.format(counts.ledgerCharges) + ' ثبت مبلغ · ' + numberFormat.format(counts.ledgerMarkers) + ' نشان ثبت');
        setText('stage-outbox', numberFormat.format(counts.committedOutbox) + ' رویداد ثبت مالی · ' + numberFormat.format(counts.pendingOutbox) + ' رویداد انتظار');
        ui['receipt-step'].classList.toggle('ua-step--issue', issues.some(finding => /receipt|operation|scan-limit|unattributable/.test(finding.code)));
        ui['ledger-step'].classList.toggle('ua-step--issue', counts.pending > 0 || issues.some(finding => /ledger|accounting/.test(finding.code)));
        ui['outbox-step'].classList.toggle('ua-step--issue', issues.some(finding => /outbox|scan-limit/.test(finding.code)));
        const pending = report.operations.filter(operation => operation.status === 'accounting-pending');
        setText('pending-count', numberFormat.format(counts.pending) + ' مصرف');
        ui['pending-list'].replaceChildren(); ui['pending-empty'].hidden = counts.pending > 0;
        for (const operation of pending) {
            const item = element('div', 'ua-pending-item'), details = element('div');
            details.append(element('strong', '', displayAmount(operation) + ' ' + operation.currency));
            details.append(element('span', 'ua-pending-meta', displayTime(operation.occurredAt)));
            const key = element('code', 'ua-code', operation.ledgerKey); key.dir = 'ltr'; key.title = operation.ledgerKey; details.append(key);
            item.append(details, detailButton(operation, canRecover(report, operation, fresh) ? 'بازبینی ثبت مالی' : 'بررسی شواهد'));
            ui['pending-list'].append(item);
        }
        if (counts.pending > pending.length) ui['pending-list'].append(element('p', 'ua-detail-issue', 'جزئیات بخشی از مصرف‌های در انتظار به دلیل ناهماهنگی شواهد قابل نمایش نیست.'));
        ui['findings-list'].replaceChildren(); ui['findings-empty'].hidden = issues.length > 0;
        for (const finding of issues) appendFinding(ui['findings-list'], finding);
        renderTable();
    }

    function isFresh() { if (expiresAt && Date.now() >= expiresAt) fresh = false; return fresh; }
    function renderRecovery() {
        const snapshot = session.snapshot(), locked = unresolved();
        const eligible = !!selected && canRecover(report, selected, isFresh()) && !loading;
        if (tenantForm) for (const control of tenantForm.querySelectorAll('select, button')) control.disabled = locked;
        ui.refresh.disabled = loading || locked;
        resumeButton.hidden = !locked;
        ui['recovery-reason'].readOnly = locked || snapshot.state === 'accounting-pending';
        ui['recovery-reason'].disabled = !selected || !locked && !eligible;
        if (snapshot.command) ui['recovery-reason'].value = snapshot.command.reason;
        else if (snapshot.receipt && snapshot.receipt.businessKey === selected?.ledgerKey) ui['recovery-reason'].value = snapshot.receipt.reason;
        let reasonValid = false;
        try { validateReason(ui['recovery-reason'].value); reasonValid = true; } catch { /* The form explains the reason requirement. */ }
        ui['recovery-submit'].disabled = !selected || snapshot.state === 'posting' || snapshot.state === 'accounting-pending'
            || snapshot.state === 'committed' || snapshot.state !== 'uncertain' && (!eligible || !reasonValid);
        ui['recovery-submit'].textContent = snapshot.state === 'posting' ? 'در حال ارسال درخواست…'
            : snapshot.state === 'uncertain' ? 'ارسال دوبارهٔ همین درخواست' : 'تلاش مجدد برای ثبت مالی';
        ui['new-request'].hidden = snapshot.state !== 'accounting-pending' || selected?.status === 'committed'; ui['new-request'].disabled = !eligible;
        setText('recovery-request', snapshot.command?.requestId || snapshot.receipt?.requestId || '—');
        setText('recovery-message', [recoveryMessage, clipboardMessage].filter(Boolean).join(' '));
        let note;
        if (locked) note = 'عملیات، دلیل و شناسهٔ این درخواست محفوظ است. تا دریافت نتیجه، سازمان یا عملیات دیگری انتخاب نکنید. ارسال دوباره فقط با همین شناسه انجام می‌شود.';
        else if (snapshot.state === 'accounting-pending' && selected?.status === 'committed') note = 'رسید تلاش قبلی در انتظار بود، اما گزارش تازه ثبت مالی این مصرف را انجام‌شده نشان می‌دهد. درخواست جدید لازم نیست.';
        else if (snapshot.state === 'accounting-pending') note = 'رسید تلاش قبلی یک نتیجهٔ تاریخی است و هنوز ثبت مالی انجام نشده است. پس از رفع محدودیت حساب و به‌روزرسانی گزارش، «آماده‌سازی درخواست جدید» را انتخاب کنید تا تلاش بعدی شناسهٔ تازه داشته باشد.';
        else if (snapshot.state === 'committed' || selected?.status === 'committed') note = 'ثبت مالی این مصرف انجام شده است و تلاش مجدد لازم نیست.';
        else if (!isFresh()) note = 'گزارش به‌روز نیست. پیش از تلاش مجدد، گزارش را به‌روزرسانی کنید.';
        else if (report?.coverage !== 'complete') note = 'پوشش بررسی کامل نیست؛ تلاش مجدد تا بررسی کامل شواهد فعال نمی‌شود.';
        else if (!eligible) note = 'شواهد این سازمان نیازمند بررسی است؛ تلاش مجدد فقط با تأیید سلامت کل گزارش فعال می‌شود.';
        else note = 'سلامت شواهد در گزارش به‌روز تأیید شده است. پس از رفع محدودیت حساب، دلیل اقدام را بنویسید و ثبت مالی همین مصرف را بررسی کنید.';
        setText('recovery-eligibility', note);
    }
    function renderDetails() {
        if (!selected) { renderRecovery(); return; }
        let displayed = unresolved() ? report?.operations.find(operation => operation.ledgerKey === selected.ledgerKey
            && operation.operationFingerprint === selected.operationFingerprint) || selected : selected;
        const receipt = session.snapshot().receipt;
        if (receipt?.businessKey === selected.ledgerKey && receipt.status === 'committed') displayed = { ...displayed, status: 'committed' };
        setText('detail-title', 'جزئیات ثبت مصرف'); setStatus(ui['detail-status'], displayed);
        setText('detail-amount', displayAmount(selected)); setText('detail-currency', currencyNames[selected.currency] || selected.currency);
        setText('detail-time', displayTime(selected.occurredAt)); ui['detail-time'].title = selected.occurredAt;
        setText('detail-key', selected.ledgerKey); setText('detail-fingerprint', selected.operationFingerprint);
        ui['detail-findings'].replaceChildren();
        const findings = operationFindings(selected);
        if (findings.length) for (const finding of findings) appendFinding(ui['detail-findings'], finding);
        else ui['detail-findings'].append(element('p', 'ua-muted', report && isFresh()
            ? 'ناهماهنگی مرتبط با این مصرف در گزارش دیده نشد.' : 'برای مشاهدهٔ نتیجهٔ معتبر، گزارش را تازه کنید.'));
        renderRecovery();
    }
    function openDetails(operation, source) {
        if (!operation || !mayOpen(operation)) {
            showBanner('notice', 'نتیجهٔ درخواست قبلی مشخص نیست', 'ابتدا نتیجهٔ درخواست محفوظ را با همان شناسه دریافت کنید.'); return;
        }
        if (!unresolved()) {
            const priorKey = selected?.ledgerKey;
            if (priorKey !== operation.ledgerKey) {
                session.resetKnown(); ui['recovery-reason'].value = ''; recoveryMessage = ''; clipboardMessage = '';
            }
            selected = Object.freeze({ ...operation });
        }
        opener = source || document.activeElement; renderDetails();
        if (!ui.detail.open) ui.detail.showModal();
    }
    function persistUnresolved() {
        const snapshot = session.snapshot();
        try {
            if (snapshot.command && selected) sessionStorage.setItem(storageKey, JSON.stringify({ command: snapshot.command, operation: selected }));
            else sessionStorage.removeItem(storageKey);
        } catch { /* Navigation is also guarded when storage is unavailable. */ }
    }
    function restoreUnresolved() {
        try {
            const raw = sessionStorage.getItem(storageKey); if (!raw) return;
            const saved = parseReportJson(raw);
            if (!validOperation(saved.operation) || saved.operation.status !== 'accounting-pending' || saved.command?.consumerTenantId !== tenant
                || saved.operation.ledgerKey !== saved.command.businessKey || saved.operation.operationFingerprint !== saved.command.expectedOperationFingerprint)
                throw new Error('درخواست محفوظ نامعتبر است.');
            session.restoreUncertain(saved.command); selected = Object.freeze({ ...saved.operation });
            recoveryMessage = 'نتیجهٔ درخواست محفوظ هنوز مشخص نیست. برای دریافت نتیجه، ارسال دوبارهٔ همین درخواست را صریحاً انتخاب کنید.';
            showBanner('notice', 'درخواست تعیین‌تکلیف‌نشده محفوظ است', recoveryMessage);
        } catch {
            try { sessionStorage.removeItem(storageKey); } catch { /* Storage may be unavailable. */ }
        }
    }
    function isJson(response) { return /^(?:application\/json|[^;]+\+json)(?:\s*;|$)/i.test((response.headers.get('content-type') || '').trim()); }
    async function request(path, options) {
        const controller = new AbortController();
        const timer = setTimeout(() => controller.abort(), 20000);
        try {
            const response = await fetch(endpoint + path, { credentials: 'same-origin', cache: 'no-store', ...options, signal: controller.signal });
            const raw = await response.text(); return { response, raw };
        } finally { clearTimeout(timer); }
    }
    function reportBanner() {
        if (unresolved()) showBanner('notice', 'نتیجهٔ درخواست محفوظ مشخص نیست', 'هیچ تلاش خودکاری انجام نمی‌شود. برای دریافت نتیجه، همین درخواست محفوظ را از جزئیات باز کنید.');
        else if (report.coverage !== 'complete') showBanner('notice', 'پوشش بررسی کامل نیست', 'گزارش بخشی از شواهد را پوشش می‌دهد. تلاش مجدد تا بررسی کامل شواهد غیرفعال است.');
        else if (!report.integrityVerified || report.findings.some(finding => finding.code !== 'accounting-pending'))
            showBanner('error', 'شواهد نیازمند بررسی است', 'ناهماهنگی‌های گزارش را بررسی کنید؛ تلاش مجدد ثبت مالی تا تأیید سلامت شواهد غیرفعال است.');
        else if (resultNotice) showBanner(resultNotice.kind, resultNotice.title, resultNotice.message);
        else if (report.counts.pending > 0) showBanner('notice', 'مصرف در انتظار ثبت مالی وجود دارد', 'پس از رفع محدودیت حساب، از جزئیات هر مصرف برای بازبینی ثبت مالی آن استفاده کنید.');
        else ui.banner.hidden = true;
    }
    async function loadReport() {
        const snapshot = session.snapshot();
        if (snapshot.state === 'posting' || snapshot.state === 'uncertain' && report) return;
        const currentGeneration = ++generation; loading = true; fresh = false; clearTimeout(expiryTimer);
        root.setAttribute('aria-busy', 'true'); setText('load-status', 'در حال دریافت گزارش…'); renderRecovery();
        if (!report) { ui.content.hidden = true; ui.state.hidden = false; setText('state-title', 'گزارش مصرف در حال آماده شدن است'); setText('state-text', 'شواهد و ثبت‌های مالی سازمان انتخاب‌شده بررسی می‌شوند.'); }
        else showBanner('notice', 'گزارش در حال به‌روزرسانی است', 'آخرین گزارش تا دریافت پاسخ تازه نمایش داده می‌شود.');
        try {
            const { response, raw } = await request('/reconciliation', { headers: { Accept: 'application/json' } });
            if (currentGeneration !== generation) return;
            if (response.redirected || !isJson(response)) throw new Error('نشست یا پاسخ گزارش معتبر نیست. دوباره وارد شوید و گزارش را تازه کنید.');
            if (!response.ok) throw new Error(response.status === 403 ? 'مجوز مشاهدهٔ گزارش ندارید یا دسترسی شما تغییر کرده است.'
                : response.status === 401 ? 'نشست پایان یافته است. دوباره وارد شوید.' : 'گزارش دریافت نشد. برای دریافت اطلاعات تازه دوباره تلاش کنید.');
            report = validateReport(parseReportJson(raw)); fresh = true; expiresAt = Date.now() + 5 * 60 * 1000;
            if (selected && !unresolved()) selected = report.operations.find(operation => operation.ledgerKey === selected.ledgerKey
                && operation.operationFingerprint === selected.operationFingerprint) || selected;
            setText('load-status', 'به‌روز شده در ' + dateFormat.format(new Date())); renderReport(); reportBanner();
            expiryTimer = setTimeout(() => {
                if (!fresh) return; fresh = false; setText('load-status', 'گزارش نیازمند به‌روزرسانی است');
                if (!unresolved()) showBanner('notice', 'گزارش نیازمند به‌روزرسانی است', 'پیش از تلاش مجدد ثبت مالی، شواهد را با گزارش تازه بررسی کنید.');
                renderDetails();
            }, 5 * 60 * 1000);
        } catch (error) {
            if (currentGeneration !== generation) return;
            fresh = false; expiresAt = 0;
            setText('load-status', report ? 'آخرین گزارش به‌روز نیست' : 'دریافت گزارش ناموفق بود');
            showBanner('error', 'گزارش تازه دریافت نشد', error.name === 'AbortError' ? 'مهلت دریافت گزارش پایان یافت. گزارش را دوباره به‌روزرسانی کنید.' : error.message);
            if (!report) { ui.state.hidden = false; setText('state-title', 'گزارش دریافت نشد'); setText('state-text', 'برای دریافت شواهد ثبت‌شدهٔ این سازمان، از دکمهٔ به‌روزرسانی استفاده کنید.'); }
        } finally {
            if (currentGeneration === generation) { loading = false; root.setAttribute('aria-busy', 'false'); renderDetails(); }
        }
    }
    function canonicalDecimal(value) {
        const [whole, fraction = ''] = decimalText(value).split('.');
        const significant = fraction.replace(/0+$/, ''); return whole.replace(/^0+(?=\d)/, '') + (significant ? '.' + significant : '');
    }
    function validateReceipt(value, operation) {
        if (!value || !digestPattern.test(value.requestFingerprint || '') || typeof value.actor !== 'string' || !value.actor.trim()
            || !['committed', 'accounting-pending'].includes(value.previousStatus) || typeof value.recordedAt !== 'string'
            || !Number.isFinite(Date.parse(value.recordedAt)) || canonicalDecimal(value.amount) !== canonicalDecimal(operation.amount))
            throw new Error('رسید بازبینی معتبر نیست؛ نتیجهٔ درخواست هنوز مشخص نیست.');
        return value;
    }
    function setUncertain(message) {
        session.markUncertain(); fresh = false; expiresAt = 0; recoveryMessage = message;
        persistUnresolved(); showBanner('notice', 'نتیجهٔ درخواست مشخص نیست', message);
        setText('load-status', 'در انتظار نتیجهٔ درخواست محفوظ'); renderReport(); renderDetails();
    }
    async function submitRecovery(event) {
        event.preventDefault();
        const snapshot = session.snapshot();
        if (!selected || snapshot.state === 'posting' || snapshot.state === 'accounting-pending' || snapshot.state === 'committed') return;
        if (snapshot.state !== 'uncertain' && (!isFresh() || loading || !canRecover(report, selected, fresh))) {
            recoveryMessage = 'گزارش به‌روز و سلامت کامل شواهد برای تلاش مجدد لازم است.'; renderRecovery(); return;
        }
        const token = root.querySelector('[name=__RequestVerificationToken]')?.value;
        if (!token) { fresh = false; recoveryMessage = 'نشست بازبینی معتبر نیست. صفحه را پس از ورود دوباره باز کنید.'; renderRecovery(); return; }
        let command;
        try { command = session.prepare(tenant, selected, ui['recovery-reason'].value); }
        catch (error) { recoveryMessage = error.message; renderRecovery(); return; }
        ++generation; loading = false; root.setAttribute('aria-busy', 'false'); clearTimeout(expiryTimer);
        fresh = false; expiresAt = 0; clipboardMessage = ''; recoveryMessage = 'درخواست بازبینی با شناسهٔ زیر در حال ارسال است…';
        resultNotice = null; persistUnresolved(); renderReport(); renderDetails();
        try {
            const { consumerTenantId, ...body } = command;
            const { response, raw } = await request('/recover', { method: 'POST', headers: {
                Accept: 'application/json', 'Content-Type': 'application/json', 'X-CSRF-TOKEN': token
            }, body: JSON.stringify(body) });
            if (response.redirected) {
                setUncertain('نشست یا پاسخ تغییر کرده است و نتیجه مشخص نیست. پس از بررسی نشست، فقط همین درخواست محفوظ را دوباره ارسال کنید.'); return;
            }
            if (!response.ok) {
                const failure = classifyRecoveryFailure(response.status);
                if (failure.uncertain) setUncertain(failure.message);
                else {
                    session.rejectKnown(); persistUnresolved(); fresh = false; expiresAt = 0;
                    recoveryMessage = failure.message; showBanner('error', 'درخواست بازبینی پذیرفته نشد', failure.message);
                    setText('load-status', 'پیش از تلاش بعدی گزارش را تازه کنید'); renderReport(); renderDetails();
                }
                return;
            }
            if (!isJson(response)) { setUncertain('رسید معتبر دریافت نشد؛ نتیجه مشخص نیست. فقط همین درخواست محفوظ را دوباره ارسال کنید.'); return; }
            const receipt = session.settle(validateReceipt(parseReportJson(raw), selected)); persistUnresolved();
            if (receipt.status === 'committed') {
                recoveryMessage = 'ثبت مالی انجام شد. رسید این درخواست در ' + displayTime(receipt.recordedAt) + ' ثبت شده است.';
                resultNotice = { kind: 'success', title: 'ثبت مالی مصرف انجام شد', message: 'نتیجه با شناسهٔ همین درخواست ثبت شده است. گزارش برای نمایش شواهد تازه به‌روزرسانی می‌شود.' };
            } else {
                recoveryMessage = 'این تلاش همچنان در انتظار ثبت مالی ماند. رسید آن در ' + displayTime(receipt.recordedAt)
                    + ' ثبت شد. پس از رفع محدودیت حساب و بازبینی گزارش تازه، برای تلاش بعدی درخواست جدید آماده کنید.';
                resultNotice = { kind: 'notice', title: 'مصرف همچنان در انتظار ثبت مالی است', message: 'این نتیجه مربوط به تلاش ثبت‌شده است. تلاش بعدی پس از بررسی حساب و گزارش تازه، به دلیل بازبینی و شناسهٔ درخواست جدید نیاز دارد.' };
            }
            showBanner(resultNotice.kind, resultNotice.title, resultNotice.message); renderReport(); renderDetails();
            await loadReport();
        } catch (error) {
            setUncertain(error.name === 'AbortError' ? 'مهلت پاسخ پایان یافت و نتیجه مشخص نیست. فقط همین درخواست با همان شناسه و دلیل را دوباره ارسال کنید.'
                : 'پاسخ قابل تأیید دریافت نشد و نتیجه مشخص نیست. فقط همین درخواست با همان شناسه و دلیل را دوباره ارسال کنید.');
        }
    }
    async function copyValue(value, label) {
        try {
            await navigator.clipboard.writeText(value); clipboardMessage = label + ' کپی شد.';
        } catch { clipboardMessage = 'کپی انجام نشد. متن ' + label + ' را انتخاب و کپی کنید.'; }
        renderRecovery();
    }
    ui.refresh.addEventListener('click', () => { resultNotice = null; loadReport(); });
    ui.search.addEventListener('input', renderTable); ui.filter.addEventListener('change', renderTable);
    ui['detail-close'].addEventListener('click', () => ui.detail.close());
    ui.detail.addEventListener('close', () => {
        const target = opener?.isConnected && !opener.disabled ? opener : !resumeButton.hidden ? resumeButton : ui.refresh;
        target.focus();
    });
    resumeButton.addEventListener('click', () => openDetails(selected, resumeButton));
    ui['recovery-form'].addEventListener('submit', submitRecovery);
    ui['recovery-reason'].addEventListener('input', renderRecovery);
    ui['new-request'].addEventListener('click', () => {
        if (session.snapshot().state !== 'accounting-pending' || !selected || !canRecover(report, selected, isFresh()) || loading) return;
        session.resetKnown(); persistUnresolved(); ui['recovery-reason'].value = ''; clipboardMessage = '';
        recoveryMessage = 'برای تلاش تازه، دلیل بازبینی پس از رفع محدودیت حساب را بنویسید. شناسهٔ جدید هنگام ارسال ساخته می‌شود.';
        renderRecovery(); ui['recovery-reason'].focus();
    });
    ui['copy-key'].addEventListener('click', () => selected && copyValue(selected.ledgerKey, 'شناسهٔ مصرف'));
    ui['copy-fingerprint'].addEventListener('click', () => selected && copyValue(selected.operationFingerprint, 'اثر انگشت شواهد'));
    ui['copy-amount']?.addEventListener('click', () => selected && copyValue(decimalText(selected.amount), 'مبلغ دقیق'));
    tenantForm?.addEventListener('submit', event => {
        if (!unresolved()) return; event.preventDefault();
        showBanner('notice', 'درخواست محفوظ در انتظار نتیجه است', 'تا دریافت نتیجه، سازمان را تغییر ندهید. همین درخواست محفوظ را باز کنید.');
        resumeButton.focus();
    });
    window.addEventListener('beforeunload', event => {
        if (!unresolved()) return; persistUnresolved(); event.preventDefault(); event.returnValue = '';
    });
    document.addEventListener('visibilitychange', () => {
        if (!document.hidden && !isFresh() && report && !loading && !unresolved()) {
            showBanner('notice', 'گزارش نیازمند به‌روزرسانی است', 'پیش از تلاش مجدد ثبت مالی، شواهد را با گزارش تازه بررسی کنید.'); renderRecovery();
        }
    });
    if (!isUuid(tenant)) {
        root.setAttribute('aria-busy', 'false'); ui.refresh.disabled = true;
        setText('state-title', 'سازمان معتبر انتخاب نشده است'); setText('state-text', 'صفحهٔ گزارش را برای یک سازمان فعال باز کنید.'); return;
    }
    restoreUnresolved(); renderRecovery(); loadReport();
})();
