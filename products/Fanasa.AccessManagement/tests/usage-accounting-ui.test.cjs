'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const {
    parseReportJson, decimalText, formatDecimal, validateReason, canRecover,
    createRecoveryCommand, classifyRecoveryFailure, createRecoverySession
} = require('../src/Fanasa.AccessManagement.Web/wwwroot/js/usage-accounting.js');

const consumer = '11111111-1111-4111-8111-111111111111';
const otherConsumer = '22222222-2222-4222-8222-222222222222';
const requestIds = [
    'aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa',
    'bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb'
];
const operation = Object.freeze({
    ledgerKey: 'measured:v1:' + 'a'.repeat(64),
    operationFingerprint: 'b'.repeat(64), status: 'accounting-pending',
    amount: '999999999999999999.123456789', currency: 'IRR',
    occurredAt: '2026-10-10T12:00:00+00:00'
});
const report = {
    coverage: 'complete', integrityVerified: true, canRecoverPending: true,
    findings: [{ code: 'accounting-pending', ledgerKey: operation.ledgerKey }],
    operations: [operation]
};
const reason = 'اصلاح محدودیت حساب با مرجع ۱۲۳';

function sessionFixture() {
    let generated = 0;
    const session = createRecoverySession(() => requestIds[generated++]);
    return { session, generated: () => generated };
}
function receipt(command, status = 'committed') {
    return {
        requestId: command.requestId, consumerTenantId: command.consumerTenantId,
        businessKey: command.businessKey, operationFingerprint: command.expectedOperationFingerprint,
        reason: command.reason, status, amount: operation.amount, actor: 'reviewer',
        recordedAt: '2026-10-11T12:00:00+00:00'
    };
}

test('wire decimal amounts and receipt amounts retain every digit', () => {
    const value = parseReportJson('{"operations":[{"amount":999999999999999999.123456789}],"receipt":{"amount":0.0000000000000000000000000001},"counts":{"pending":3}}');
    assert.equal(value.operations[0].amount, operation.amount);
    assert.equal(value.receipt.amount, '0.0000000000000000000000000001');
    assert.equal(value.counts.pending, 3);
    assert.equal(formatDecimal(value.operations[0].amount), '999,999,999,999,999,999.123456789');
});

test('amount parser preserves escaped text and escaped property names', () => {
    const source = { note: 'Example: "amount":123 and \\ escaped data', amount: '120000.00', nested: { label: 'amount' } };
    assert.deepEqual(parseReportJson(JSON.stringify(source)), source);
    assert.equal(parseReportJson('{"a\\u006dount":9007199254740993}').amount, '9007199254740993');
    for (const raw of ['{"amount":}', '{"amount":01}', '{"amount":1x}', '{"note":"unterminated}'])
        assert.throws(() => parseReportJson(raw));
});

test('decimal exponents expand without floating point arithmetic', () => {
    assert.equal(decimalText('1.23456789e20'), '123456789000000000000');
    assert.equal(decimalText('1.23456789e-20'), '0.0000000000000000000123456789');
    assert.equal(formatDecimal('120000.00'), '120,000.00');
    assert.equal(formatDecimal('0.001e2'), '0.1');
    assert.equal(formatDecimal(0), '0');
    for (const value of [9007199254740992, 1.5, -1, NaN, Infinity, '-1', '1e101', '01', null])
        assert.throws(() => formatDecimal(value));
});

test('reason is explicit and bounded before a request can be created', () => {
    assert.equal(validateReason('  ' + reason + '  '), reason);
    assert.equal(validateReason('x'.repeat(1000)).length, 1000);
    for (const value of ['', '   ', 'x'.repeat(1001), null]) assert.throws(() => validateReason(value));
});

test('only the exact pending operation in a fresh complete verified report is actionable', () => {
    assert.equal(canRecover(report, operation, true), true);
    assert.equal(canRecover(report, { ...operation }, true), true);
    assert.equal(canRecover(report, operation, false), false);
    for (const changed of [
        { coverage: 'incomplete' }, { integrityVerified: false }, { canRecoverPending: false },
        { findings: [{ code: 'ledger-charge-mismatch' }] }, { operations: [] }
    ]) assert.equal(canRecover({ ...report, ...changed }, operation, true), false);
    for (const changed of [
        { status: 'committed' }, { operationFingerprint: 'c'.repeat(64) },
        { ledgerKey: 'measured:v1:' + 'c'.repeat(64) }, { amount: '120000' },
        { currency: 'USD' }, { occurredAt: '2026-10-09T12:00:00+00:00' }
    ]) assert.equal(canRecover(report, { ...operation, ...changed }, true), false);
});

test('recovery command carries an immutable reviewed identity and trimmed reason', () => {
    const command = createRecoveryCommand(consumer, operation, '  ' + reason + '  ', requestIds[0]);
    assert.deepEqual(command, {
        consumerTenantId: consumer, businessKey: operation.ledgerKey,
        expectedOperationFingerprint: operation.operationFingerprint,
        requestId: requestIds[0], reason
    });
    assert.equal(Object.isFrozen(command), true);
    assert.throws(() => { command.reason = 'changed'; }, TypeError);
    for (const target of ['', '00000000-0000-0000-0000-000000000000', 'not-a-uuid'])
        assert.throws(() => createRecoveryCommand(target, operation, reason, requestIds[0]));
    assert.throws(() => createRecoveryCommand(consumer, { ...operation, ledgerKey: 'invoice:123' }, reason, requestIds[0]));
    assert.throws(() => createRecoveryCommand(consumer, operation, reason, 'not-a-uuid'));
});

test('double submission is refused while the first request is in flight', () => {
    const { session, generated } = sessionFixture();
    const first = session.prepare(consumer, operation, reason);
    assert.equal(session.snapshot().state, 'posting');
    assert.throws(() => session.prepare(consumer, operation, reason));
    assert.throws(() => session.resetKnown());
    assert.equal(session.snapshot().command, first);
    assert.equal(generated(), 1);
});

test('timeout retry keeps exactly the same command and request UUID', () => {
    const { session, generated } = sessionFixture();
    const first = session.prepare(consumer, operation, reason);
    session.markUncertain();
    const retried = session.prepare(consumer, { ...operation }, '  ' + reason + '  ');
    assert.equal(retried, first);
    assert.equal(retried.requestId, requestIds[0]);
    assert.equal(generated(), 1);
    session.settle(receipt(retried));
    assert.equal(session.snapshot().state, 'committed');
    assert.equal(session.snapshot().command, null);
});

test('unknown outcome locks organization, operation, fingerprint and reason', () => {
    const { session, generated } = sessionFixture();
    const first = session.prepare(consumer, operation, reason);
    session.markUncertain();
    assert.throws(() => session.resetKnown());
    assert.throws(() => session.prepare(otherConsumer, operation, reason));
    assert.throws(() => session.prepare(consumer, { ...operation, ledgerKey: 'measured:v1:' + 'c'.repeat(64) }, reason));
    assert.throws(() => session.prepare(consumer, { ...operation, operationFingerprint: 'c'.repeat(64) }, reason));
    assert.throws(() => session.prepare(consumer, operation, 'changed'));
    assert.equal(session.snapshot().state, 'uncertain');
    assert.equal(session.snapshot().command, first);
    assert.equal(generated(), 1);
});

test('a mismatched or malformed acknowledgement leaves the outcome unknown', () => {
    for (const changed of [
        { requestId: requestIds[1] }, { consumerTenantId: otherConsumer }, { reason: 'different' },
        { businessKey: 'measured:v1:' + 'c'.repeat(64) },
        { operationFingerprint: 'c'.repeat(64) }, { status: 'unknown' }
    ]) {
        const { session, generated } = sessionFixture();
        const first = session.prepare(consumer, operation, reason);
        assert.throws(() => session.settle({ ...receipt(first), ...changed }));
        assert.equal(session.snapshot().state, 'uncertain');
        assert.equal(session.prepare(consumer, operation, reason), first);
        assert.equal(generated(), 1);
    }
    const { session } = sessionFixture();
    const first = session.prepare(consumer, operation, reason);
    assert.throws(() => session.settle(null));
    assert.equal(session.snapshot().command, first);
});

test('a known pending receipt allows a later deliberate attempt with a fresh UUID', () => {
    const { session, generated } = sessionFixture();
    const first = session.prepare(consumer, operation, reason);
    const known = session.settle(receipt(first, 'accounting-pending'));
    assert.equal(session.snapshot().state, 'accounting-pending');
    assert.equal(Object.isFrozen(known), true);
    assert.equal(session.snapshot().command, null);
    session.resetKnown();
    const second = session.prepare(consumer, operation, 'رفع محدودیت جدید');
    assert.equal(second.requestId, requestIds[1]);
    assert.notEqual(second.requestId, first.requestId);
    assert.equal(generated(), 2);
});

test('known rejection clears command but requires the caller to refresh evidence', () => {
    const { session } = sessionFixture();
    session.prepare(consumer, operation, reason);
    session.rejectKnown();
    assert.deepEqual(session.snapshot(), { state: 'idle', command: null, receipt: null });
    for (const status of [400, 401, 403, 404, 409]) {
        const failure = classifyRecoveryFailure(status);
        assert.equal(failure.uncertain, false);
        assert.equal(failure.stale, true);
        assert.ok(failure.message.length > 0);
    }
});

test('network and server failure cannot justify creating a new request', () => {
    for (const status of [undefined, 0, 302, 408, 429, 500, 502, 503, 504]) {
        const failure = classifyRecoveryFailure(status);
        assert.equal(failure.uncertain, true);
        assert.equal(failure.stale, false);
        const { session } = sessionFixture();
        const first = session.prepare(consumer, operation, reason);
        session.markUncertain();
        assert.equal(session.prepare(consumer, operation, reason), first);
    }
});

test('saved unresolved command resumes after reload without a new UUID or automatic submission', () => {
    const first = createRecoveryCommand(consumer, operation, reason, requestIds[0]);
    const { session, generated } = sessionFixture();
    session.restoreUncertain(JSON.parse(JSON.stringify(first)));
    assert.equal(session.snapshot().state, 'uncertain');
    assert.equal(generated(), 0);
    assert.deepEqual(session.snapshot().command, first);
    assert.equal(Object.isFrozen(session.snapshot().command), true);
    assert.throws(() => session.prepare(otherConsumer, operation, reason));
    assert.equal(session.prepare(consumer, operation, reason).requestId, first.requestId);
    assert.equal(generated(), 0);
    session.settle(receipt(first));
    assert.equal(session.snapshot().state, 'committed');
    const invalid = createRecoverySession();
    assert.throws(() => invalid.restoreUncertain({ ...first, requestId: 'invalid' }));
    assert.equal(invalid.snapshot().state, 'idle');
});
