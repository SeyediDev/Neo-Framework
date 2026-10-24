using System.Text.Json;
using Fanasa.AccessManagement.Web.Persistence;

namespace Fanasa.AccessManagement.Web.Metering;

public sealed record MeasuredUsageRecoveryReceipt(Guid RequestId, Guid ConsumerTenantId, string BusinessKey,
    string OperationFingerprint, string RequestFingerprint, string Actor, string Reason,
    string PreviousStatus, string Status, decimal Amount, DateTimeOffset RecordedAt);

/// <summary>Explicit accounting-only retry of evidence already validated and retained by trusted intake.</summary>
public sealed class MeasuredUsageRecoveryStore
{
    public const string ReceiptKind = "usage.accounting.recovery";
    private readonly FabricDatabase database;
    private readonly MeasuredUsageStore measured;
    private readonly UsageReconciliationStore reconciliation;
    private readonly TimeProvider clock;

    public MeasuredUsageRecoveryStore(FabricDatabase database, MeasuredUsageStore measured, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(database); ArgumentNullException.ThrowIfNull(measured);
        if (!ReferenceEquals(database, measured.Database)) throw new ArgumentException("Usage recovery requires the original Fabric instance.");
        this.database = database; this.measured = measured;
        reconciliation = new(database); clock = timeProvider ?? TimeProvider.System;
    }

    public MeasuredUsageRecoveryReceipt Recover(Guid consumer, string businessKey, string expectedOperationFingerprint,
        Guid requestId, string actor, string reason)
    {
        if (consumer == Guid.Empty || requestId == Guid.Empty || !IsDigest(expectedOperationFingerprint)
            || businessKey is null || !businessKey.StartsWith("measured:v1:", StringComparison.Ordinal)
            || !IsDigest(businessKey["measured:v1:".Length..]))
            throw new ArgumentException("A consumer, recovery request UUID, measured ledger key and exact operation fingerprint are required.");
        UsageEventV1.Label(actor); UsageEventV1.Label(reason, 1000);
        var requestKey = consumer.ToString("D") + ":" + requestId.ToString("D");
        var fingerprint = UsageEventV1.Hash(JsonSerializer.Serialize(new
            { consumer, businessKey, expectedOperationFingerprint, requestId, actor, reason }));
        return database.Transaction(() =>
        {
            var previous = database.Read<MeasuredUsageRecoveryReceipt>(ReceiptKind, requestKey);
            if (previous is not null)
            {
                if (previous.RequestFingerprint != fingerprint) throw new InvalidOperationException("Recovery request identity conflicts with its original command.");
                if (previous.RequestId != requestId || previous.ConsumerTenantId != consumer || previous.BusinessKey != businessKey
                    || previous.OperationFingerprint != expectedOperationFingerprint || previous.Actor != actor || previous.Reason != reason
                    || previous.PreviousStatus is not ("committed" or "accounting-pending")
                    || previous.Status is not ("committed" or "accounting-pending") || previous.Amount < 0 || previous.RecordedAt.Offset != TimeSpan.Zero)
                    throw new InvalidDataException("Recovery receipt is inconsistent.");
                // An attempt receipt is historical. Retrying after an account change requires a new request UUID.
                return previous;
            }
            var operation = database.Read<MeasuredUsageOperation>("measured.operation", businessKey)
                ?? throw new KeyNotFoundException("No retained measured operation exists for accounting recovery.");
            UsageEventV1 original;
            try { original = UsageEventV1.FromJson(operation.EventJson); }
            catch (ArgumentException error) { throw new InvalidDataException("Retained usage evidence is invalid.", error); }
            if (original.ConsumerTenantId != consumer) throw new UnauthorizedAccessException("Retained usage belongs to another consumer.");
            if (operation.Fingerprint != expectedOperationFingerprint || original.OperationFingerprint != expectedOperationFingerprint)
                throw new InvalidOperationException("Retained operation fingerprint differs from the recovery command.");
            var report = reconciliation.InspectCurrentTransaction(consumer);
            if (report.Coverage != "complete" || report.Findings.Any(x => x.Code != "accounting-pending"))
                throw new InvalidDataException("Usage evidence requires reconciliation before accounting recovery.");
            var result = measured.Accept(original, retainPending: true);
            var receipt = new MeasuredUsageRecoveryReceipt(requestId, consumer, businessKey, expectedOperationFingerprint,
                fingerprint, actor, reason, operation.Status, result.Status, result.Amount, clock.GetUtcNow().ToUniversalTime());
            database.Put(ReceiptKind, requestKey, receipt);
            database.Emit("usage-recovery:" + requestKey, "UsageAccountingRecoveryAttempted", new
            { TenantId = consumer, LedgerKey = businessKey, requestId, actor, reason, operationFingerprint = expectedOperationFingerprint,
                receipt.PreviousStatus, receipt.Status, receipt.Amount }, receipt.RecordedAt);
            return receipt;
        });
    }

    private static bool IsDigest(string? value) => value is { Length: 64 } && value.All(x => x is >= '0' and <= '9' or >= 'a' and <= 'f');
}
