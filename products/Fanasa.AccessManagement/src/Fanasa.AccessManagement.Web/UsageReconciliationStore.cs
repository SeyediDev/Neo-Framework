using System.Globalization;
using System.Text;
using System.Text.Json;
using Fanasa.AccessManagement.Web.Accounting;
using Fanasa.AccessManagement.Web.Persistence;

namespace Fanasa.AccessManagement.Web.Metering;

public sealed record UsageReconciliationFinding(string Code, string? LedgerKey = null, string? EventKey = null, long? OutboxId = null);
public sealed record UsageReconciliationCounts(int Operations, int EventReceipts, int Committed, int Pending,
    int LedgerCharges, int LedgerMarkers, int CommittedOutbox, int PendingOutbox, int UnattributableRows);
public sealed record UsageReconciliationOperation(string LedgerKey, string OperationFingerprint, string Status,
    decimal Amount, string Currency, DateTimeOffset OccurredAt);
public sealed record UsageReconciliationReport(Guid ConsumerTenantId, string Coverage, string Status,
    UsageReconciliationCounts Counts, UsageReconciliationFinding[] Findings, UsageReconciliationOperation[] Operations)
{
    public bool IntegrityVerified => Coverage == "complete" && Findings.All(x => x.Code == "accounting-pending");
    public bool CanRecoverPending => IntegrityVerified && Counts.Pending > 0;
}

/// <summary>Bounded inspection of retained usage evidence. Inspection never changes or replays accounting.</summary>
public sealed class UsageReconciliationStore(FabricDatabase database)
{
    public UsageReconciliationReport Inspect(Guid consumer, int maxRows = 10000)
    {
        ValidateRequest(consumer, maxRows);
        return database.ReadSnapshot(() => InspectCore(consumer, maxRows));
    }

    internal UsageReconciliationReport InspectCurrentTransaction(Guid consumer, int maxRows = 10000)
    {
        ValidateRequest(consumer, maxRows);
        if (!database.IsInTransaction) throw new InvalidOperationException("Reconciliation requires the current Fabric transaction.");
        return InspectCore(consumer, maxRows);
    }

    private UsageReconciliationReport InspectCore(Guid consumer, int maxRows)
    {
        var findings = new HashSet<UsageReconciliationFinding>(); var complete = true; var unattributable = 0;
        void Add(string code, string? ledger = null, string? eventKey = null, long? outbox = null)
            => findings.Add(new(code, ledger, eventKey, outbox));
        void Unknown(string code)
        { unattributable++; complete = false; Add(code); }
        void Limit()
        { complete = false; Add("scan-limit-exceeded"); }

        FabricDocument[] Documents(string kind)
        {
            var rows = new List<FabricDocument>(); string? after = null;
            while (rows.Count <= maxRows)
            {
                var page = database.DocumentPage(kind, after, Math.Min(100, maxRows - rows.Count + 1));
                if (page.Length == 0) break;
                rows.AddRange(page); after = page[^1].Key;
            }
            if (rows.Count > maxRows) { Limit(); rows.RemoveRange(maxRows, rows.Count - maxRows); }
            return rows.ToArray();
        }
        FabricOutboxEvidence[] OutboxRows()
        {
            var rows = new List<FabricOutboxEvidence>(); long after = 0;
            while (rows.Count <= maxRows)
            {
                var page = database.OutboxEvidence(after, Math.Min(100, maxRows - rows.Count + 1));
                if (page.Length == 0) break;
                rows.AddRange(page); after = page[^1].Id;
            }
            if (rows.Count > maxRows) { Limit(); rows.RemoveRange(maxRows, rows.Count - maxRows); }
            return rows.ToArray();
        }

        var entries = new List<LedgerEntry>(); var ledgerKeys = new HashSet<string>(StringComparer.Ordinal);
        string? accountMode = null; string? accountCurrency = null;
        var accountRow = database.ReadDocument("accounting", consumer.ToString());
        if (accountRow is not null)
        {
            try
            {
                using var account = Parse(accountRow.Json, 32);
                var root = account.RootElement;
                if (!UniqueObject(root) || !root.TryGetProperty("TenantId", out var tenant) || !tenant.TryGetGuid(out var accountTenant))
                    throw new InvalidDataException();
                if (accountTenant != consumer) Add("accounting-tenant-mismatch");
                accountMode = String(root, "Mode"); accountCurrency = String(root, "Currency");
                if (accountMode is not ("payg" or "postpaid") || accountCurrency is not ("IRR" or "USD" or "EUR")) Add("accounting-document-invalid");
                if (!root.TryGetProperty("Entries", out var rows) || rows.ValueKind != JsonValueKind.Array) throw new InvalidDataException();
                var scanned = 0;
                foreach (var row in rows.EnumerateArray())
                {
                    if (scanned++ == maxRows) { Limit(); break; }
                    var key = String(row, "Key");
                    if (key is null) { Add("ledger-entry-invalid"); complete = false; continue; }
                    if (!key.StartsWith("measured:", StringComparison.Ordinal)) continue;
                    var scopedKey = IsBusinessKey(key) ? key : null;
                    if (scopedKey is not null) ledgerKeys.Add(scopedKey);
                    try
                    {
                        if (!UniqueObject(row)) throw new InvalidDataException();
                        var entry = row.Deserialize<LedgerEntry>() ?? throw new InvalidDataException();
                        if (scopedKey is null || entry.Key != key || entry.Id == Guid.Empty) throw new InvalidDataException();
                        entries.Add(entry);
                    }
                    catch (Exception exception) when (IsEvidenceError(exception)) { Add("ledger-entry-invalid", scopedKey); }
                }
            }
            catch (Exception exception) when (IsEvidenceError(exception)) { Add("accounting-document-invalid"); }
        }

        var operationRows = Documents("measured.operation"); var eventRows = Documents("measured.event"); var outboxRows = OutboxRows();
        var operations = new List<OperationNode>(); var originalEventKeys = new Dictionary<string, string>(StringComparer.Ordinal);
        var operationCount = 0; var eventCount = 0;
        foreach (var row in operationRows)
        {
            var wire = JournalWire(row.Json);
            var knownKey = ledgerKeys.Contains(row.Key) ? row.Key : null;
            if (wire.Usage?.ConsumerTenantId != consumer && wire.Tenant != consumer && knownKey is null)
            {
                if (wire.Tenant is null) Unknown("unattributable-operation");
                continue;
            }
            operationCount++;
            var key = wire.Usage?.ConsumerTenantId == consumer ? wire.Usage.BusinessKey : knownKey;
            if (wire.Usage is null) { Add("operation-usage-invalid", key); continue; }
            if (wire.Usage.ConsumerTenantId != consumer) { Add("operation-consumer-mismatch", key); continue; }
            var usage = wire.Usage; ledgerKeys.Add(usage.BusinessKey); originalEventKeys.TryAdd(usage.EventKey, usage.BusinessKey);
            MeasuredUsageOperation operation;
            try
            {
                using var json = Parse(row.Json);
                if (!Shape(json.RootElement, "BusinessKey", "Fingerprint", "EventJson", "Status", "Amount", "ReceivedAt", "CommittedAt", "Error"))
                    throw new InvalidDataException();
                operation = json.RootElement.Deserialize<MeasuredUsageOperation>() ?? throw new InvalidDataException();
            }
            catch (Exception exception) when (IsEvidenceError(exception)) { Add("operation-document-invalid", usage.BusinessKey); continue; }
            var node = new OperationNode(row.Key, operation, usage); operations.Add(node);
            if (row.Key != usage.BusinessKey || operation.BusinessKey != usage.BusinessKey) Add("operation-key-mismatch", usage.BusinessKey);
            if (operation.Fingerprint != usage.OperationFingerprint) Add("operation-fingerprint-mismatch", usage.BusinessKey);
            if (operation.Amount != usage.Amount) Add("operation-amount-mismatch", usage.BusinessKey);
            if (operation.Status is not ("committed" or "accounting-pending")) Add("operation-status-invalid", usage.BusinessKey);
            if (operation.ReceivedAt.Offset != TimeSpan.Zero || operation.CommittedAt is { Offset: var offset } && offset != TimeSpan.Zero
                || operation.Status == "committed" && (operation.CommittedAt is null || operation.CommittedAt < operation.ReceivedAt || operation.Error is not null)
                || operation.Status == "accounting-pending" && (operation.CommittedAt is not null || string.IsNullOrWhiteSpace(operation.Error)))
                Add("operation-timestamp-mismatch", usage.BusinessKey);
        }
        var operationGroups = operations.GroupBy(x => x.Usage.BusinessKey).ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.Ordinal);
        foreach (var group in operationGroups.Values)
            if (group.Length != 1) Add("duplicate-operation", group[0].Usage.BusinessKey);

        var receipts = new List<EventNode>();
        foreach (var row in eventRows)
        {
            var wire = JournalWire(row.Json);
            var linked = originalEventKeys.GetValueOrDefault(row.Key);
            var declaredBusiness = JournalString(row.Json, "BusinessKey");
            if (linked is null && declaredBusiness is not null && ledgerKeys.Contains(declaredBusiness)) linked = declaredBusiness;
            if (wire.Usage?.ConsumerTenantId != consumer && wire.Tenant != consumer && linked is null)
            {
                if (wire.Tenant is null) Unknown("unattributable-event-receipt");
                continue;
            }
            eventCount++;
            var key = wire.Usage?.ConsumerTenantId == consumer ? wire.Usage.BusinessKey : linked;
            if (wire.Usage is null) { Add("event-receipt-usage-invalid", key); continue; }
            if (wire.Usage.ConsumerTenantId != consumer) { Add("event-receipt-consumer-mismatch", key); continue; }
            var usage = wire.Usage;
            MeasuredEventReceipt receipt;
            try
            {
                using var json = Parse(row.Json);
                if (!Shape(json.RootElement, "EventKey", "Fingerprint", "BusinessKey", "EventJson", "ReceivedAt")) throw new InvalidDataException();
                receipt = json.RootElement.Deserialize<MeasuredEventReceipt>() ?? throw new InvalidDataException();
            }
            catch (Exception exception) when (IsEvidenceError(exception)) { Add("event-receipt-document-invalid", usage.BusinessKey, usage.EventKey); continue; }
            receipts.Add(new(row.Key, receipt, usage));
            if (row.Key != usage.EventKey || receipt.EventKey != usage.EventKey) Add("event-receipt-key-mismatch", usage.BusinessKey, usage.EventKey);
            if (receipt.BusinessKey != usage.BusinessKey) Add("event-receipt-business-mismatch", usage.BusinessKey, usage.EventKey);
            if (receipt.Fingerprint != usage.EventFingerprint) Add("event-receipt-fingerprint-mismatch", usage.BusinessKey, usage.EventKey);
            if (receipt.ReceivedAt.Offset != TimeSpan.Zero) Add("event-receipt-timestamp-invalid", usage.BusinessKey, usage.EventKey);
            if (!operationGroups.TryGetValue(usage.BusinessKey, out var group)) Add("orphan-event-receipt", usage.BusinessKey, usage.EventKey);
            else if (group.All(x => x.Usage.OperationFingerprint != usage.OperationFingerprint)) Add("receipt-operation-mismatch", usage.BusinessKey, usage.EventKey);
        }
        foreach (var group in receipts.GroupBy(x => x.Usage.EventKey))
            if (group.Count() > 1) Add("duplicate-event-receipt", group.First().Usage.BusinessKey, group.Key);
        var receiptGroups = receipts.GroupBy(x => x.PhysicalKey).ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.Ordinal);

        var outboxes = new List<OutboxNode>();
        foreach (var row in outboxRows)
        {
            if (row.Kind is not ("UsageCommitted" or "UsageAccountingPending"))
            {
                var measuredKey = BusinessFromOutboxKey(row.Key);
                if (measuredKey is not null && ledgerKeys.Contains(measuredKey)) Add("usage-outbox-mismatch", measuredKey, outbox: row.Id);
                continue;
            }
            Guid? tenant = null; string? declaredKey = null; UsageEventV1? usage = null; Guid? wireTenant = null; var valid = false;
            try
            {
                if (Encoding.UTF8.GetByteCount(row.Payload) > 128 * 1024) throw new InvalidDataException();
                using var json = Parse(row.Payload, 20); var root = json.RootElement;
                tenant = GuidValue(root, "TenantId"); declaredKey = String(root, "LedgerKey");
                if (root.TryGetProperty("Usage", out var eventJson))
                {
                    wireTenant = GuidValue(eventJson, "consumerTenantId");
                    try { usage = UsageEventV1.FromJson(eventJson.GetRawText()); }
                    catch (Exception exception) when (IsEvidenceError(exception)) { }
                }
                if (usage is not null)
                    valid = Shape(root, "TenantId", "LedgerKey", "Status", "Amount", "Usage")
                        && tenant == usage.ConsumerTenantId && declaredKey == usage.BusinessKey
                        && String(root, "Status") == (row.Kind == "UsageCommitted" ? "committed" : "accounting-pending")
                        && root.GetProperty("Amount").ValueKind == JsonValueKind.Number && root.GetProperty("Amount").TryGetDecimal(out var amount) && amount == usage.Amount
                        && row.Key == usage.BusinessKey + (row.Kind == "UsageCommitted" ? ":committed" : ":pending");
            }
            catch (Exception exception) when (IsEvidenceError(exception)) { }
            var linked = declaredKey is not null && ledgerKeys.Contains(declaredKey) ? declaredKey : BusinessFromOutboxKey(row.Key);
            if (linked is not null && !ledgerKeys.Contains(linked)) linked = null;
            var scoped = tenant == consumer || wireTenant == consumer || usage?.ConsumerTenantId == consumer || linked is not null;
            if (!scoped)
            {
                if (tenant is null && wireTenant is null) Unknown("unattributable-outbox");
                continue;
            }
            var key = usage?.ConsumerTenantId == consumer ? usage.BusinessKey : linked ?? (tenant == consumer && IsBusinessKey(declaredKey) ? declaredKey : null);
            var eventKey = usage?.ConsumerTenantId == consumer ? usage.EventKey : null;
            if (!valid || usage?.ConsumerTenantId != consumer) Add("usage-outbox-mismatch", key, eventKey, row.Id);
            var recorded = DateTimeOffset.MinValue;
            var timestampValid = row.RecordedAt.Length <= 64 && DateTimeOffset.TryParse(row.RecordedAt, CultureInfo.InvariantCulture,
                DateTimeStyles.None, out recorded) && recorded.Offset == TimeSpan.Zero;
            if (!timestampValid) Add("usage-outbox-timestamp-invalid", key, eventKey, row.Id);
            if (key is null) continue;
            outboxes.Add(new(new FabricOutbox(row.Id, row.Key, row.Kind, row.Payload, recorded), key,
                usage?.ConsumerTenantId == consumer ? usage : null, valid && usage?.ConsumerTenantId == consumer, timestampValid));
            if (!operationGroups.ContainsKey(key)) Add("orphan-usage-outbox", key, eventKey, row.Id);
        }

        var entryGroups = entries.GroupBy(x => x.Key).ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.Ordinal);
        foreach (var group in entryGroups)
        {
            if (!operationGroups.ContainsKey(group.Key)) Add("orphan-measured-ledger-entry", group.Key);
            if (group.Value.Any(x => x.Kind is not ("receipt" or "usage"))) Add("unexpected-measured-ledger-entry", group.Key);
            if (group.Value.Count(x => x.Kind == "usage") > 1) Add("duplicate-ledger-charge", group.Key);
            if (group.Value.Count(x => x.Kind == "receipt") > 1) Add("duplicate-ledger-marker", group.Key);
        }
        foreach (var group in entries.GroupBy(x => x.Id))
            if (group.Count() > 1) foreach (var key in group.Select(x => x.Key).Distinct()) Add("duplicate-ledger-id", key);
        var outboxGroups = outboxes.GroupBy(x => x.BusinessKey).ToDictionary(x => x.Key, x => x.ToArray(), StringComparer.Ordinal);
        foreach (var group in outboxGroups)
        {
            if (group.Value.Count(x => x.Row.Kind == "UsageCommitted") > 1) Add("duplicate-committed-outbox", group.Key);
            if (group.Value.Count(x => x.Row.Kind == "UsageAccountingPending") > 1) Add("duplicate-pending-outbox", group.Key);
        }

        foreach (var node in operations)
        {
            var usage = node.Usage; var key = usage.BusinessKey; var operation = node.Operation;
            var originalReceipt = (receiptGroups.GetValueOrDefault(usage.EventKey) ?? []).FirstOrDefault(x => x.Receipt.EventKey == usage.EventKey
                && x.Receipt.BusinessKey == key && x.Receipt.Fingerprint == usage.EventFingerprint && x.Usage.EventFingerprint == usage.EventFingerprint);
            if (originalReceipt is null)
                Add("missing-original-event-receipt", key, usage.EventKey);
            else if (originalReceipt.Receipt.ReceivedAt != operation.ReceivedAt)
                Add("original-event-receipt-timestamp-mismatch", key, usage.EventKey);
            var linkedEntries = entryGroups.GetValueOrDefault(key) ?? [];
            var charges = linkedEntries.Where(x => x.Kind == "usage").ToArray(); var markers = linkedEntries.Where(x => x.Kind == "receipt").ToArray();
            var linkedOutboxes = outboxGroups.GetValueOrDefault(key) ?? [];
            foreach (var box in linkedOutboxes)
                if (box.Usage is null || box.Usage.EventFingerprint != usage.EventFingerprint || box.Usage.OperationFingerprint != usage.OperationFingerprint
                    || box.TimestampValid && box.Row.Kind == "UsageAccountingPending" && box.Row.RecordedAt != operation.ReceivedAt)
                    Add("usage-outbox-mismatch", key, outbox: box.Row.Id);
            if (operation.Status == "committed")
            {
                if (accountRow is null) Add("missing-accounting-document", key);
                else if (accountMode != usage.RateSnapshot.BillingMode || accountCurrency != usage.RateSnapshot.Currency) Add("accounting-snapshot-mismatch", key);
                if (charges.Length == 0) Add("missing-ledger-charge", key);
                if (markers.Length == 0) Add("missing-ledger-marker", key);
                foreach (var charge in charges)
                    if (!ChargeMatches(charge, usage)) Add("ledger-charge-mismatch", key);
                foreach (var marker in markers)
                    if (!MarkerMatches(marker, usage)) Add("ledger-marker-mismatch", key);
                if (charges.Length == 1 && markers.Length == 1 && charges[0].At != markers[0].At) Add("ledger-timestamp-mismatch", key);
                var expected = linkedOutboxes.FirstOrDefault(x => x.Row.Key == key + ":committed" && x.Row.Kind == "UsageCommitted");
                if (expected is null) Add("missing-committed-outbox", key);
                else if (!expected.Valid || expected.Usage?.EventFingerprint != usage.EventFingerprint
                    || expected.TimestampValid && expected.Row.RecordedAt != operation.CommittedAt)
                    Add("usage-outbox-mismatch", key, outbox: expected.Row.Id);
            }
            else if (operation.Status == "accounting-pending")
            {
                Add("accounting-pending", key);
                if (linkedEntries.Length != 0) Add("pending-ledger-effects", key);
                if (linkedOutboxes.Any(x => x.Row.Kind == "UsageCommitted")) Add("pending-committed-outbox", key);
                var expected = linkedOutboxes.FirstOrDefault(x => x.Row.Key == key + ":pending" && x.Row.Kind == "UsageAccountingPending");
                if (expected is null) Add("missing-pending-outbox", key);
                else if (!expected.Valid || expected.Usage?.EventFingerprint != usage.EventFingerprint
                    || expected.TimestampValid && expected.Row.RecordedAt != operation.ReceivedAt)
                    Add("usage-outbox-mismatch", key, outbox: expected.Row.Id);
            }
        }
        var sorted = findings.OrderBy(x => x.LedgerKey ?? "", StringComparer.Ordinal).ThenBy(x => x.EventKey ?? "", StringComparer.Ordinal)
            .ThenBy(x => x.OutboxId ?? 0).ThenBy(x => x.Code, StringComparer.Ordinal).ToArray();
        var counts = new UsageReconciliationCounts(operationCount, eventCount, operations.Count(x => x.Operation.Status == "committed"),
            operations.Count(x => x.Operation.Status == "accounting-pending"), entries.Count(x => x.Kind == "usage"), entries.Count(x => x.Kind == "receipt"),
            outboxes.Count(x => x.Row.Kind == "UsageCommitted"), outboxes.Count(x => x.Row.Kind == "UsageAccountingPending"), unattributable);
        var summaries = operations.Where(x => operationGroups[x.Usage.BusinessKey].Length == 1
                && x.PhysicalKey == x.Usage.BusinessKey && x.Operation.BusinessKey == x.Usage.BusinessKey
                && x.Operation.Fingerprint == x.Usage.OperationFingerprint && x.Operation.Amount == x.Usage.Amount
                && x.Operation.ReceivedAt.Offset == TimeSpan.Zero && x.Operation.Status is "committed" or "accounting-pending"
                && (x.Operation.Status == "committed" ? x.Operation.CommittedAt is { Offset: var offset } && offset == TimeSpan.Zero
                    && x.Operation.CommittedAt >= x.Operation.ReceivedAt && x.Operation.Error is null
                    : x.Operation.CommittedAt is null && !string.IsNullOrWhiteSpace(x.Operation.Error)))
            .Select(x => new UsageReconciliationOperation(x.Usage.BusinessKey, x.Usage.OperationFingerprint, x.Operation.Status,
                x.Usage.Amount, x.Usage.RateSnapshot.Currency, x.Usage.OccurredAt)).OrderBy(x => x.LedgerKey, StringComparer.Ordinal).ToArray();
        return new(consumer, complete ? "complete" : "incomplete", !complete ? "incomplete" : sorted.Length == 0 ? "clean" : "issues", counts, sorted, summaries);
    }

    private static bool ChargeMatches(LedgerEntry entry, UsageEventV1 usage)
        => entry.Id == UsageEventV1.StableGuid(usage.BusinessKey + ":charge") && entry.ProductKey == usage.ProductKey
            && entry.Metric == usage.Meter.Id && entry.Quantity == usage.Quantity && entry.Amount == usage.Amount
            && entry.UnitPrice == usage.UnitPrice && entry.UsageOccurredAt == usage.OccurredAt && entry.Actor == usage.Operation.ActorId
            && entry.Reference == usage.Evidence.Reference && entry.InvoiceId is null && entry.At.Offset == TimeSpan.Zero;
    private static bool MarkerMatches(LedgerEntry entry, UsageEventV1 usage)
        => entry.Id == UsageEventV1.StableGuid(usage.BusinessKey + ":receipt") && entry.Reference == usage.OperationFingerprint
            && entry.Actor == usage.Operation.ActorId && entry.ProductKey is null && entry.Metric is null && entry.Quantity == 0 && entry.Amount == 0
            && entry.InvoiceId is null && entry.UnitPrice is null && entry.UsageOccurredAt is null && entry.At.Offset == TimeSpan.Zero;
    private static (UsageEventV1? Usage, Guid? Tenant) JournalWire(string json)
    {
        Guid? tenant = null;
        try
        {
            if (Encoding.UTF8.GetByteCount(json) > 128 * 1024) return (null, null);
            using var document = Parse(json); var wire = String(document.RootElement, "EventJson");
            if (wire is null) return (null, null);
            using var eventJson = Parse(wire); tenant = GuidValue(eventJson.RootElement, "consumerTenantId");
            return (UsageEventV1.FromJson(wire), tenant);
        }
        catch (Exception exception) when (IsEvidenceError(exception)) { return (null, tenant); }
    }
    private static string? JournalString(string json, string name)
    {
        try { using var document = Parse(json); return String(document.RootElement, name); }
        catch (Exception exception) when (IsEvidenceError(exception)) { return null; }
    }
    private static JsonDocument Parse(string json, int depth = 16) => JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = depth });
    private static string? String(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static Guid? GuidValue(JsonElement root, string name)
        => root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && value.TryGetGuid(out var id) && id != Guid.Empty ? id : null;
    private static bool UniqueObject(JsonElement root)
        => root.ValueKind == JsonValueKind.Object && root.EnumerateObject().Select(x => x.Name).Distinct(StringComparer.Ordinal).Count() == root.EnumerateObject().Count();
    private static bool Shape(JsonElement root, params string[] names)
        => UniqueObject(root) && root.EnumerateObject().Select(x => x.Name).ToHashSet(StringComparer.Ordinal).SetEquals(names);
    private static bool IsBusinessKey(string? key)
        => key is not null && key.Length == "measured:v1:".Length + 64 && key.StartsWith("measured:v1:", StringComparison.Ordinal)
            && key.AsSpan("measured:v1:".Length).ToArray().All(x => x is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static string? BusinessFromOutboxKey(string key)
    {
        var suffix = key.EndsWith(":committed", StringComparison.Ordinal) ? ":committed" : key.EndsWith(":pending", StringComparison.Ordinal) ? ":pending" : null;
        if (suffix is null) return null;
        var business = key[..^suffix.Length]; return IsBusinessKey(business) ? business : null;
    }
    private static bool IsEvidenceError(Exception exception)
        => exception is ArgumentException or InvalidDataException or JsonException or InvalidOperationException or FormatException or OverflowException or KeyNotFoundException;
    private static void ValidateRequest(Guid consumer, int maxRows)
    {
        if (consumer == Guid.Empty) throw new ArgumentException("A consumer tenant is required.", nameof(consumer));
        if (maxRows is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(maxRows));
    }
    private sealed record OperationNode(string PhysicalKey, MeasuredUsageOperation Operation, UsageEventV1 Usage);
    private sealed record EventNode(string PhysicalKey, MeasuredEventReceipt Receipt, UsageEventV1 Usage);
    private sealed record OutboxNode(FabricOutbox Row, string BusinessKey, UsageEventV1? Usage, bool Valid, bool TimestampValid);
}
