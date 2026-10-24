using System.Text.Json;
using Fanasa.AccessManagement.Web.Accounting;
using Fanasa.AccessManagement.Web.Persistence;

namespace Fanasa.AccessManagement.Web.Metering;

public sealed class MeasuredAccountingException(string message) : InvalidOperationException(message);
public sealed record MeasuredUsageReceipt(string EventKey, string LedgerKey, string Status, decimal Amount, AccountView? Account, string? Error);
public sealed record MeasuredEventReceipt(string EventKey, string Fingerprint, string BusinessKey, string EventJson, DateTimeOffset ReceivedAt);
public sealed record MeasuredUsageOperation(string BusinessKey, string Fingerprint, string EventJson, string Status, decimal Amount,
    DateTimeOffset ReceivedAt, DateTimeOffset? CommittedAt, string? Error);

/// <summary>Internal completed-usage intake sharing the business and accounting Fabric transaction.</summary>
public sealed class MeasuredUsageStore
{
    public FabricDatabase Database { get; }
    private readonly AccountingStore accounting;
    private readonly IUsageSnapshotValidator snapshotValidator;
    private readonly TimeProvider clock;

    public MeasuredUsageStore(FabricDatabase database, AccountingStore accounting, IUsageSnapshotValidator snapshotValidator, TimeProvider? timeProvider = null)
    {
        if (!ReferenceEquals(database, accounting.Database)) throw new ArgumentException("Measured intake requires the same Fabric instance as accounting.");
        Database = database; this.accounting = accounting; this.snapshotValidator = snapshotValidator; clock = timeProvider ?? TimeProvider.System;
    }

    // Local business callers leave retainPending false: any accounting failure rolls back their entire transaction.
    // A trusted completed remote event may explicitly retain its evidence for accounting-only recovery.
    public MeasuredUsageReceipt Accept(UsageEventV1 usage, bool retainPending = false)
    {
        usage.Validate();
        return Database.Transaction(() => AcceptCore(usage, retainPending));
    }

    private MeasuredUsageReceipt AcceptCore(UsageEventV1 usage, bool retainPending)
    {
        var now = clock.GetUtcNow(); var eventKey = usage.EventKey; var businessKey = usage.BusinessKey;
        var receipt = Database.Read<MeasuredEventReceipt>("measured.event", eventKey);
        if (receipt is not null && (receipt.Fingerprint != usage.EventFingerprint || receipt.BusinessKey != businessKey))
            throw new InvalidOperationException("Usage delivery identity conflicts with its immutable payload.");
        var operation = Database.Read<MeasuredUsageOperation>("measured.operation", businessKey);
        if (receipt is not null && operation is null) throw new InvalidDataException("Usage receipt has no retained operation.");
        if (operation is not null && operation.Fingerprint != usage.OperationFingerprint)
            throw new InvalidOperationException("Usage operation identity conflicts with its immutable business evidence.");
        if (operation is null)
        {
            snapshotValidator.ValidateNew(usage);
            operation = new(businessKey, usage.OperationFingerprint, usage.ToWireJson(), "accounting-pending", usage.Amount, now, null, null);
        }
        // Recovery always uses the original event. An alias cannot replace pending evidence or its historical rate.
        var original = UsageEventV1.FromJson(operation.EventJson);
        AccountView? account = null;
        if (operation.Status != "committed")
        {
            try
            {
                // AppendMeasured uses a nested savepoint: caught financial rejection cannot leak staged writes.
                account = accounting.AppendMeasured(original);
                operation = operation with { Status = "committed", CommittedAt = now, Error = null };
                Database.Put("measured.operation", businessKey, operation);
                Emit(businessKey + ":committed", "UsageCommitted", original, operation, now);
            }
            catch (MeasuredAccountingException exception) when (retainPending)
            {
                var firstPending = Database.Read<MeasuredUsageOperation>("measured.operation", businessKey) is null;
                operation = operation with { Error = exception.Message };
                Database.Put("measured.operation", businessKey, operation);
                if (firstPending) Emit(businessKey + ":pending", "UsageAccountingPending", original, operation, now);
            }
        }
        if (receipt is null)
            Database.Put("measured.event", eventKey, new MeasuredEventReceipt(eventKey, usage.EventFingerprint, businessKey, usage.ToWireJson(), now));
        if (account is null)
        {
            try { account = accounting.Read(original.ConsumerTenantId); }
            catch (KeyNotFoundException) { /* Retained evidence/receipts remain available to trusted accounting recovery. */ }
        }
        return new(eventKey, businessKey, operation.Status, operation.Amount, account, operation.Error);
    }

    private void Emit(string key, string kind, UsageEventV1 original, MeasuredUsageOperation operation, DateTimeOffset at)
    {
        using var wire = JsonDocument.Parse(operation.EventJson);
        Database.Emit(key, kind, new { TenantId = original.ConsumerTenantId, LedgerKey = operation.BusinessKey,
            operation.Status, operation.Amount, Usage = wire.RootElement.Clone() }, at);
    }
}
