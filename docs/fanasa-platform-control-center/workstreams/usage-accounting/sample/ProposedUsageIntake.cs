using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Fanasa.AccessManagement.Web.Accounting;
using Fanasa.AccessManagement.Web.Persistence;

namespace UsageAccounting.Sample;

public sealed record IntakeReceipt(string EventId, string Fingerprint, string BusinessKey, Guid ChargeId, decimal Amount, string Disposition, DateTimeOffset ReceivedAt);
public sealed record RecordedUsage(string BusinessKey, string EventId, string Fingerprint, JsonElement Event, Guid ChargeId, decimal Amount);
public sealed record ChargedOperation(string BusinessKey, string Fingerprint, string EventId, Guid ChargeId, decimal Amount);
public sealed record IntakeResult(string Disposition, Guid ChargeId, decimal Amount);
public sealed record PendingAccounting(string EventId, string BusinessKey, string Fingerprint, JsonElement Event, string Status, string Reason);
public sealed record PendingOperation(string BusinessKey, string Fingerprint, string OriginalEventKey, string Status);
public enum Fault { None, BeforeCommit, AfterCommit }
public sealed class InjectedCrash(string message) : Exception(message);
public sealed class PayloadConflict(string message) : Exception(message);
public sealed class IntegrationPending(string message) : Exception(message);

// PROPOSED sample only. Not an installed endpoint, worker, or approved production accounting API.
public sealed class ProposedUsageIntake(FabricDatabase database, AccountingStore accounting, PinnedHistoricalResolver resolver, TimeProvider? timeProvider = null,
    Action<FixtureEvent>? evidenceValidation = null)
{
    public const string Receipts = "proposed.usage.receipt.v1";
    public const string Usage = "proposed.usage.v1";
    public const string Operations = "proposed.usage.operation.v1";
    public const string ChargedKind = "MeasuredUsageCharged.v1";
    public const string Pending = "proposed.usage.pending.v1";
    public const string PendingOperations = "proposed.usage.pending-operation.v1";
    // Deterministic fixture hook for the rejection/retention concurrency window.
    public Action? BeforeRetainingPending { get; set; }

    public IntakeResult Accept(FixtureEvent message, Fault fault = Fault.None)
    {
        IntakeResult result;
        try { result = database.Transaction(() =>
        {
            ValidatePendingBindings(message);
            var receivedAt = (timeProvider ?? TimeProvider.System).GetUtcNow();
            // Receipt and business deduplication are checked before validation against evolving live state.
            if (database.Read<IntakeReceipt>(Receipts, message.EventKey) is { } receipt)
            {
                if (receipt.Fingerprint != message.EventFingerprint) throw new PayloadConflict("Same eventId with different payload.");
                ResolvePendingForBusiness(message);
                return new IntakeResult("event-replay", receipt.ChargeId, receipt.Amount);
            }
            if (database.Read<ChargedOperation>(Operations, message.BusinessKey) is { } operation)
            {
                if (operation.Fingerprint != message.OperationFingerprint) throw new PayloadConflict("Same business operation with different immutable payload.");
                database.Put(Receipts, message.EventKey, new IntakeReceipt(message.EventId, message.EventFingerprint, message.BusinessKey, operation.ChargeId, operation.Amount, "operation-replay", receivedAt));
                ResolvePendingForBusiness(message);
                return new IntakeResult("operation-replay", operation.ChargeId, operation.Amount);
            }
            evidenceValidation?.Invoke(message);
            resolver.Validate(message);
            var view = accounting.Read(message.Consumer);
            if (view.Account.Invoices.Any(x => x.Period == message.OccurredAt.ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture)))
                throw new IntegrationPending("Closed accounting period: retain source event for manual adjustment; issued invoices are immutable.");
            if (view.Account.Mode != message.BillingMode || view.Account.Currency != message.Currency)
                throw new IntegrationPending("Historical account/currency mapping requires integration.");
            if (message.Amount > view.AvailableCredit)
                throw new IntegrationPending("Completed remote operation awaits accounting credit resolution; never reexecute the business operation.");

            var entry = AppendMeasuredUsage_PROPOSED(view.Account, message, receivedAt);
            database.Put(Receipts, message.EventKey, new IntakeReceipt(message.EventId, message.EventFingerprint, message.BusinessKey, entry.Id, entry.Amount, "charged", receivedAt));
            database.Put(Usage, message.BusinessKey, new RecordedUsage(message.BusinessKey, message.EventId, message.OperationFingerprint, message.Payload, entry.Id, entry.Amount));
            database.Put(Operations, message.BusinessKey, new ChargedOperation(message.BusinessKey, message.OperationFingerprint, message.EventId, entry.Id, entry.Amount));
            ResolvePendingForBusiness(message);
            database.Emit(message.BusinessKey, ChargedKind, new
            {
                TenantId = message.Consumer, OwnerTenantId = message.Owner, message.EventId, message.BusinessKey,
                message.OperationId, ChargeId = entry.Id, message.Product, message.Meter, message.Quantity,
                message.UnitPrice, entry.Amount, message.Currency, message.OccurredAt,
                RateSnapshot = message.Element("rateSnapshot"), ContractVersion = message.Element("contractVersion").GetInt32(),
                message.BusinessReceipt,
                UsageEvent = message.Payload
            }, receivedAt);
            if (fault == Fault.BeforeCommit) throw new InjectedCrash("Before Fabric commit.");
            return new IntakeResult("charged", entry.Id, entry.Amount);
        }); }
        catch (IntegrationPending pending)
        {
            BeforeRetainingPending?.Invoke();
            var alreadyCommitted = database.Transaction<IntakeResult?>(() =>
            {
                ValidatePendingBindings(message);
                // A competing request may commit while the failed transaction unwinds.
                if (database.Read<IntakeReceipt>(Receipts, message.EventKey) is { } committed)
                {
                    if (committed.Fingerprint != message.EventFingerprint) throw new PayloadConflict("Same eventId committed with different payload.");
                    ResolvePendingForBusiness(message);
                    return new IntakeResult("event-replay", committed.ChargeId, committed.Amount);
                }
                if (database.Read<ChargedOperation>(Operations, message.BusinessKey) is { } operation)
                {
                    if (operation.Fingerprint != message.OperationFingerprint) throw new PayloadConflict("Same business operation committed with different payload.");
                    database.Put(Receipts, message.EventKey, new IntakeReceipt(message.EventId, message.EventFingerprint,
                        message.BusinessKey, operation.ChargeId, operation.Amount, "operation-replay", (timeProvider ?? TimeProvider.System).GetUtcNow()));
                    ResolvePendingForBusiness(message);
                    return new IntakeResult("operation-replay", operation.ChargeId, operation.Amount);
                }
                database.Put(Pending, message.EventKey, new PendingAccounting(message.EventId, message.BusinessKey,
                    message.EventFingerprint, message.Payload, "accounting-pending", pending.Message));
                if (database.Read<PendingOperation>(PendingOperations, message.BusinessKey) is null)
                    database.Put(PendingOperations, message.BusinessKey, new PendingOperation(message.BusinessKey,
                        message.OperationFingerprint, message.EventKey, "accounting-pending"));
                return null;
            });
            if (alreadyCommitted is not null) return alreadyCommitted;
            throw;
        }
        if (fault == Fault.AfterCommit) throw new InjectedCrash("After Fabric commit, before the acknowledgement.");
        return result;
    }

    // Integration seam: move this narrow append into AccountingStore.ExecuteMeasuredUsage.
    // Production consumers must NOT write raw BillingAccount documents through public Fabric.Put.
    // Existing Execute("usage") ignores command.UnitPrice/Amount and uses the live tariff and clock.
    private LedgerEntry AppendMeasuredUsage_PROPOSED(BillingAccount account, FixtureEvent message, DateTimeOffset receivedAt)
    {
        if (account.Entries.Any(x => x.Key == message.BusinessKey)) throw new PayloadConflict("Ledger business key already exists without its operation receipt; reconcile first.");
        var entry = new LedgerEntry(StableGuid(message.BusinessKey), message.BusinessKey, "usage", message.Product, message.Meter,
            message.Quantity, message.Amount, receivedAt, message.Actor, message.Reference, UnitPrice: message.UnitPrice);
        var receipt = new LedgerEntry(StableGuid(message.BusinessKey + ":receipt"), message.BusinessKey, "receipt", null, null, 0, 0,
            receivedAt, message.Actor, message.OperationFingerprint);
        database.Put("accounting", message.Consumer.ToString(), account with
        { Revision = account.Revision + 1, Entries = [.. account.Entries, entry, receipt] });
        return entry;
    }
    private static Guid StableGuid(string key) => new(SHA256.HashData(Encoding.UTF8.GetBytes(key)).AsSpan(0, 16));
    private void ValidatePendingBindings(FixtureEvent message)
    {
        if (database.Read<PendingAccounting>(Pending, message.EventKey) is { } pending && pending.Fingerprint != message.EventFingerprint)
            throw new PayloadConflict("Pending eventId with different payload.");
        if (database.Read<PendingOperation>(PendingOperations, message.BusinessKey) is { } operation && operation.Fingerprint != message.OperationFingerprint)
            throw new PayloadConflict("Pending business operation with different immutable payload.");
    }
    private void ResolvePendingForBusiness(FixtureEvent message)
    {
        if (database.Read<PendingOperation>(PendingOperations, message.BusinessKey) is { } operation)
            database.Put(PendingOperations, message.BusinessKey, operation with { Status = "resolved" });
        foreach (var pending in database.All<PendingAccounting>(Pending).Where(x => x.BusinessKey == message.BusinessKey && x.Status == "accounting-pending"))
        {
            var alias = new FixtureEvent(pending.Event);
            if (alias.OperationFingerprint != message.OperationFingerprint) throw new PayloadConflict("Persisted pending operation binding conflicts.");
            database.Put(Pending, alias.EventKey, pending with { Status = "resolved", Reason = "Accounting committed." });
        }
    }

    public void RejectUnsafeLegacyHistoricalPath(FixtureEvent message)
    {
        var live = accounting.Read(message.Consumer).Account.Tariffs.SingleOrDefault(x => x.ProductKey == message.Product && x.Metric == message.Meter);
        if (live is null || live.UnitPrice != message.UnitPrice)
            throw new IntegrationPending("Existing AccountingStore.Execute cannot consume this historical snapshot; await ExecuteMeasuredUsage integration.");
        throw new IntegrationPending("Existing Execute also replaces occurredAt with processing time; historical append integration is still required.");
    }
}

public sealed record DeliveryCheckpoint(long LastId);
public sealed record SinkReceipt(string BusinessKey, string Payload);

// File-backed fixture sink simulates a remote recipient without a new broker or production database.
public sealed class IdempotentSink(string path)
{
    public int Attempts { get; private set; }
    public SinkReceipt[] Receipts => File.Exists(path) ? JsonSerializer.Deserialize<SinkReceipt[]>(File.ReadAllText(path))! : [];
    public void Receive(FabricOutbox item)
    {
        Attempts++; var receipts = Receipts;
        if (receipts.SingleOrDefault(x => x.BusinessKey == item.Key) is { } prior)
        {
            using var oldPayload = JsonDocument.Parse(prior.Payload); using var newPayload = JsonDocument.Parse(item.Payload);
            if (FixtureEvent.SnapshotFingerprint(oldPayload.RootElement) != FixtureEvent.SnapshotFingerprint(newPayload.RootElement))
                throw new PayloadConflict("Remote outbox business key conflicts.");
            return;
        }
        var temporary = path + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        { JsonSerializer.Serialize(stream, receipts.Append(new SinkReceipt(item.Key, item.Payload)).ToArray()); stream.Flush(true); }
        File.Move(temporary, path, true);
    }
}

public static class ProposedOutboxRelay
{
    public static void Deliver(FabricDatabase database, IdempotentSink sink, bool crashAfterRemoteAck = false)
    {
        var checkpoint = database.Read<DeliveryCheckpoint>("proposed.usage.relay.v1", "fixture-sink")?.LastId ?? 0;
        while (true)
        {
            var batch = database.Outbox(checkpoint);
            if (batch.Length == 0) return;
            foreach (var item in batch)
            {
                if (item.Kind == ProposedUsageIntake.ChargedKind)
                {
                    sink.Receive(item);
                    if (crashAfterRemoteAck) throw new InjectedCrash("Remote acknowledged; local checkpoint has not committed.");
                }
                database.Transaction(() => { database.Put("proposed.usage.relay.v1", "fixture-sink", new DeliveryCheckpoint(item.Id)); return true; });
                checkpoint = item.Id;
            }
        }
    }
}
