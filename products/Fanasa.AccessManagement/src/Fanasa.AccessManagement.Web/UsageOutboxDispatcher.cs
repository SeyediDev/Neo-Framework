using System.Text;
using System.Text.Json;
using Fanasa.AccessManagement.Web.Accounting;
using Fanasa.AccessManagement.Web.Persistence;

namespace Fanasa.AccessManagement.Web.Metering;

public sealed record UsageOutboxSubscription(string DestinationId, string EndpointBinding, Guid ConsumerTenantId,
    TimeSpan LeaseDuration, TimeSpan RetryDelay);
public sealed record UsageOutboxDelivery(string DestinationId, string LedgerKey, Guid EventId, string EventFingerprint,
    string DeliveryId, string UsageJson, long OutboxId, string SourceDatabaseId);
public sealed record UsageOutboxAcknowledgement(string DestinationId, string LedgerKey, Guid EventId,
    string EventFingerprint, string Disposition);
public interface IUsageOutboxTransport
{
    Task<UsageOutboxAcknowledgement> SendAsync(UsageOutboxDelivery delivery, CancellationToken cancellationToken);
}
public sealed record UsageOutboxAttempt(UsageOutboxDelivery Delivery, string OutboxKey, string PayloadHash,
    int Failures, DateTimeOffset? NextAttemptAt, string? LastErrorCode);
public sealed record UsageOutboxCursor(string DestinationId, string EndpointBinding, Guid ConsumerTenantId,
    string SourceDatabaseId, long LastOutboxId, string? LastOutboxKey, string? LastOutboxPayloadHash,
    long Fence, string? LeaseOwner, DateTimeOffset? LeaseUntil, UsageOutboxAttempt? Attempt,
    string? ErrorCode = null, long? BlockedOutboxId = null);
public sealed record UsageDispatchResult(string Status, int Scanned, int Delivered, long LastOutboxId,
    string? ErrorCode = null, DateTimeOffset? NextAttemptAt = null);

/// <summary>Sequential, tenant-scoped delivery from the existing durable Fabric outbox.</summary>
public sealed class UsageOutboxDispatcher
{
    public const string CursorKind = "usage.outbox.cursor";
    public const string DatabaseIdentityKind = "usage.outbox.database";
    public const string DatabaseIdentityKey = "identity";
    private readonly FabricDatabase database;
    private readonly IUsageOutboxTransport transport;
    private readonly UsageOutboxSubscription subscription;
    private readonly TimeProvider clock;
    private readonly string cursorKey;

    public UsageOutboxDispatcher(FabricDatabase database, IUsageOutboxTransport transport,
        UsageOutboxSubscription subscription, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(database); ArgumentNullException.ThrowIfNull(transport);
        ArgumentNullException.ThrowIfNull(subscription);
        UsageEventV1.Label(subscription.DestinationId);
        if (subscription.ConsumerTenantId == Guid.Empty || subscription.LeaseDuration < TimeSpan.FromSeconds(5)
            || subscription.LeaseDuration > TimeSpan.FromMinutes(5) || subscription.RetryDelay < TimeSpan.FromSeconds(1)
            || subscription.RetryDelay > TimeSpan.FromMinutes(5))
            throw new ArgumentException("A tenant scope and bounded lease/retry durations are required.");
        if (!Uri.TryCreate(subscription.EndpointBinding, UriKind.Absolute, out var endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps || endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0
            || endpoint.Fragment.Length != 0 || endpoint.Host.Length == 0 || subscription.EndpointBinding.Length > 2000)
            throw new ArgumentException("Usage delivery requires an HTTPS endpoint without credentials, query or fragment.");
        this.database = database; this.transport = transport;
        this.subscription = subscription with { EndpointBinding = endpoint.AbsoluteUri };
        clock = timeProvider ?? TimeProvider.System;
        cursorKey = UsageEventV1.Hash(subscription.DestinationId);
    }

    public UsageOutboxCursor? ReadCursor() => database.Read<UsageOutboxCursor>(CursorKind, cursorKey);

    public Task<UsageDispatchResult> DispatchBatchAsync(int take = 100, CancellationToken cancellationToken = default)
    {
        // Fabric transactions use synchronous ThreadLocal state and must never span an await.
        if (database.IsInTransaction) throw new InvalidOperationException("Usage dispatch cannot run inside a Fabric transaction.");
        if (take is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(take));
        return DispatchCoreAsync(take, cancellationToken);
    }

    private async Task<UsageDispatchResult> DispatchCoreAsync(int take, CancellationToken cancellationToken)
    {
        var owner = Guid.NewGuid().ToString("N");
        var acquired = database.Transaction(() => Acquire(owner));
        if (acquired.LeaseOwner != owner) return new("leased", 0, 0, acquired.LastOutboxId);
        var fence = acquired.Fence; var scanned = 0; var delivered = 0; var last = acquired.LastOutboxId;
        try
        {
            while (scanned < take)
            {
                if (cancellationToken.IsCancellationRequested) return new("cancelled", scanned, delivered, last, "cancelled");
                var stage = database.Transaction(() => Stage(owner, fence));
                last = stage.Cursor.LastOutboxId; scanned += stage.Scanned;
                if (stage.Stop is not null)
                    return new(stage.Stop == "idle" && scanned > 0 ? "completed" : stage.Stop,
                        scanned, delivered, last, stage.ErrorCode, stage.NextAttemptAt);
                if (stage.Delivery is null) continue;

                UsageOutboxAcknowledgement? acknowledgement = null; string? error = null;
                try { acknowledgement = await transport.SendAsync(stage.Delivery, cancellationToken).ConfigureAwait(false); }
                catch (OperationCanceledException) { error = cancellationToken.IsCancellationRequested ? "cancelled" : "transport-timeout"; }
                catch (Exception) { error = "transport-failure"; }
                if (error is null && !Matches(acknowledgement, stage.Delivery)) error = "acknowledgement-mismatch";
                // The only asynchronous work is complete before opening a new synchronous transaction.
                var finished = database.Transaction(() => Finish(owner, fence, stage.Delivery, error));
                last = finished.Cursor.LastOutboxId;
                if (finished.Stop is not null)
                    return new(finished.Stop, scanned, delivered, last, finished.ErrorCode, finished.NextAttemptAt);
                delivered++;
            }
            return new("completed", scanned, delivered, last);
        }
        finally
        {
            database.Transaction(() =>
            {
                var current = ReadCursor();
                if (current is not null && current.Fence == fence && current.LeaseOwner == owner)
                    Save(current with { LeaseOwner = null, LeaseUntil = null });
                return true;
            });
        }
    }

    private UsageOutboxCursor Acquire(string owner)
    {
        var identity = database.Read<UsageOutboxDatabaseIdentity>(DatabaseIdentityKind, DatabaseIdentityKey);
        if (identity is null)
        {
            identity = new(Guid.NewGuid().ToString("D"));
            database.Put(DatabaseIdentityKind, DatabaseIdentityKey, identity);
        }
        if (!Guid.TryParseExact(identity.Id, "D", out var id) || id == Guid.Empty)
            throw new InvalidDataException("Usage outbox database identity is invalid.");
        var current = ReadCursor() ?? new(subscription.DestinationId, subscription.EndpointBinding,
            subscription.ConsumerTenantId, identity.Id, 0, null, null, 0, null, null, null);
        ValidateBinding(current, identity.Id); ValidateAnchor(current);
        var now = clock.GetUtcNow();
        if (current.LeaseOwner is not null && current.LeaseUntil > now) return current;
        current = current with { Fence = checked(current.Fence + 1), LeaseOwner = owner, LeaseUntil = now + subscription.LeaseDuration };
        Save(current); return current;
    }

    private Staged Stage(string owner, long fence)
    {
        var current = RequireCursor(); var now = clock.GetUtcNow();
        if (!Owns(current, owner, fence, now)) return new(current, null, "fenced");
        ValidateAnchor(current);
        var row = database.Outbox(current.LastOutboxId, 1).FirstOrDefault();
        if (row is null)
        {
            if (current.Attempt is not null) return Block(current, current.Attempt.Delivery.OutboxId, "source-attempt-missing");
            return new(current, null, "idle");
        }
        var payloadHash = UsageEventV1.Hash(row.Payload);
        if (current.Attempt is { } attempt && (attempt.Delivery.OutboxId != row.Id || attempt.OutboxKey != row.Key || attempt.PayloadHash != payloadHash))
            return Block(current, row.Id, "source-attempt-mismatch");
        current = current with { LeaseUntil = now + subscription.LeaseDuration };
        if (row.Kind != "UsageCommitted")
        {
            if (current.Attempt is not null) return Block(current, row.Id, "source-attempt-mismatch");
            current = Advance(current, row, payloadHash); Save(current); return new(current, null, null, 1);
        }
        UsageEventV1 usage; string usageJson;
        try { (usage, usageJson) = ValidateCommitted(row); }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or JsonException
            or InvalidOperationException or FormatException or OverflowException or KeyNotFoundException)
        { return Block(current, row.Id, "invalid-committed-usage"); }
        if (usage.ConsumerTenantId != subscription.ConsumerTenantId)
        {
            if (current.Attempt is not null) return Block(current, row.Id, "source-attempt-mismatch");
            current = Advance(current, row, payloadHash); Save(current); return new(current, null, null, 1);
        }
        var deliveryId = "usage-delivery:v1:" + UsageEventV1.Hash(JsonSerializer.Serialize(new[]
            { subscription.DestinationId, usage.BusinessKey, usage.EventKey, usage.EventFingerprint }));
        var delivery = new UsageOutboxDelivery(subscription.DestinationId, usage.BusinessKey, usage.EventId,
            usage.EventFingerprint, deliveryId, usageJson, row.Id, current.SourceDatabaseId);
        if (current.Attempt is { } retained && retained.Delivery != delivery)
            return Block(current, row.Id, "source-attempt-mismatch");
        if (current.Attempt?.NextAttemptAt is { } next && next > now)
        { Save(current); return new(current, null, "retry", 0, current.Attempt.LastErrorCode, next); }
        current = current with { Attempt = current.Attempt ?? new(delivery, row.Key, payloadHash, 0, null, null), ErrorCode = null, BlockedOutboxId = null };
        Save(current); return new(current, delivery, null, 1);
    }

    private Staged Finish(string owner, long fence, UsageOutboxDelivery delivery, string? error)
    {
        var current = RequireCursor(); var now = clock.GetUtcNow();
        if (!Owns(current, owner, fence, now)) return new(current, null, "fenced");
        ValidateAnchor(current);
        var row = database.Outbox(current.LastOutboxId, 1).FirstOrDefault();
        if (current.Attempt?.Delivery != delivery || row is null || row.Id != delivery.OutboxId
            || row.Key != current.Attempt.OutboxKey || UsageEventV1.Hash(row.Payload) != current.Attempt.PayloadHash)
            return Block(current, delivery.OutboxId, "source-attempt-mismatch");
        try
        {
            var (usage, wire) = ValidateCommitted(row);
            if (usage.EventFingerprint != delivery.EventFingerprint || wire != delivery.UsageJson)
                return Block(current, delivery.OutboxId, "source-attempt-mismatch");
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or JsonException
            or InvalidOperationException or FormatException or OverflowException or KeyNotFoundException)
        { return Block(current, delivery.OutboxId, "invalid-committed-usage"); }
        if (error is not null)
        {
            var failures = Math.Min(current.Attempt.Failures, int.MaxValue - 1) + 1;
            var delaySeconds = Math.Min(300, subscription.RetryDelay.TotalSeconds * Math.Pow(2, Math.Min(failures - 1, 9)));
            var next = now + TimeSpan.FromSeconds(delaySeconds);
            current = current with { Attempt = current.Attempt with { Failures = failures, NextAttemptAt = next, LastErrorCode = error }, ErrorCode = error };
            Save(current); return new(current, null, error == "cancelled" ? "cancelled" : "retry", 0, error, next);
        }
        current = Advance(current, row, current.Attempt.PayloadHash); Save(current); return new(current, null, null);
    }

    private (UsageEventV1 Usage, string UsageJson) ValidateCommitted(FabricOutbox row)
    {
        if (Encoding.UTF8.GetByteCount(row.Payload) > 128 * 1024) throw new InvalidDataException("Committed usage envelope is too large.");
        using var document = JsonDocument.Parse(row.Payload, new JsonDocumentOptions { MaxDepth = 20 });
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Committed usage envelope is invalid.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in root.EnumerateObject())
            if (!names.Add(property.Name) || property.Name is not ("TenantId" or "LedgerKey" or "Status" or "Amount" or "Usage"))
                throw new InvalidDataException("Committed usage envelope is invalid.");
        if (names.Count != 5 || root.GetProperty("TenantId").ValueKind != JsonValueKind.String
            || !root.GetProperty("TenantId").TryGetGuid(out var tenant) || root.GetProperty("Status").GetString() != "committed"
            || !root.GetProperty("Amount").TryGetDecimal(out var amount))
            throw new InvalidDataException("Committed usage envelope is invalid.");
        var usageJson = root.GetProperty("Usage").GetRawText(); var usage = UsageEventV1.FromJson(usageJson);
        if (tenant != usage.ConsumerTenantId || root.GetProperty("LedgerKey").GetString() != usage.BusinessKey
            || amount != usage.Amount || row.Key != usage.BusinessKey + ":committed")
            throw new InvalidDataException("Committed usage identity is invalid.");
        var receipt = database.Read<MeasuredEventReceipt>("measured.event", usage.EventKey);
        var operation = database.Read<MeasuredUsageOperation>("measured.operation", usage.BusinessKey);
        if (receipt is null || receipt.EventKey != usage.EventKey || receipt.Fingerprint != usage.EventFingerprint
            || receipt.BusinessKey != usage.BusinessKey || UsageEventV1.FromJson(receipt.EventJson).EventFingerprint != usage.EventFingerprint
            || operation is null || operation.BusinessKey != usage.BusinessKey || operation.Fingerprint != usage.OperationFingerprint
            || operation.Status != "committed" || operation.CommittedAt is null || operation.Amount != usage.Amount
            || UsageEventV1.FromJson(operation.EventJson).EventFingerprint != usage.EventFingerprint)
            throw new InvalidDataException("Committed usage journal evidence is missing or mismatched.");
        var account = database.Read<BillingAccount>("accounting", tenant.ToString());
        if (account is null || account.TenantId != tenant || account.Currency != usage.RateSnapshot.Currency || account.Mode != usage.RateSnapshot.BillingMode
            || account.Entries is null || account.Entries.Any(x => x is null))
            throw new InvalidDataException("Committed usage accounting evidence is missing or mismatched.");
        var marker = account.Entries.SingleOrDefault(x => x.Key == usage.BusinessKey && x.Kind == "receipt");
        var charge = account.Entries.SingleOrDefault(x => x.Key == usage.BusinessKey && x.Kind == "usage");
        if (marker is null || marker.Id != UsageEventV1.StableGuid(usage.BusinessKey + ":receipt")
            || marker.Reference != usage.OperationFingerprint || marker.Actor != usage.Operation.ActorId
            || charge is null || charge.Id != UsageEventV1.StableGuid(usage.BusinessKey + ":charge") || charge.ProductKey != usage.ProductKey
            || charge.Metric != usage.Meter.Id || charge.Quantity != usage.Quantity || charge.Amount != usage.Amount
            || charge.UnitPrice != usage.UnitPrice || charge.UsageOccurredAt != usage.OccurredAt
            || charge.Actor != usage.Operation.ActorId || charge.Reference != usage.Evidence.Reference)
            throw new InvalidDataException("Committed usage ledger receipt or charge is missing or mismatched.");
        return (usage, usageJson);
    }

    private void ValidateBinding(UsageOutboxCursor cursor, string databaseId)
    {
        if (cursor.DestinationId != subscription.DestinationId || cursor.EndpointBinding != subscription.EndpointBinding
            || cursor.ConsumerTenantId != subscription.ConsumerTenantId || cursor.SourceDatabaseId != databaseId)
            throw new InvalidOperationException("Usage cursor is bound to another endpoint, tenant or database stream.");
        if (cursor.LastOutboxId < 0 || cursor.Fence < 0 || (cursor.LeaseOwner is null) != (cursor.LeaseUntil is null)
            || cursor.LastOutboxId == 0 && (cursor.LastOutboxKey is not null || cursor.LastOutboxPayloadHash is not null)
            || cursor.LastOutboxId > 0 && (cursor.LastOutboxKey is null || cursor.LastOutboxPayloadHash is null)
            || cursor.BlockedOutboxId is { } blocked && blocked <= cursor.LastOutboxId)
            throw new InvalidDataException("Usage cursor state is invalid.");
        if (cursor.Attempt is { } attempt && (attempt.Delivery is null || attempt.Failures < 0
            || attempt.Delivery.OutboxId <= cursor.LastOutboxId || attempt.Delivery.DestinationId != cursor.DestinationId
            || attempt.Delivery.SourceDatabaseId != databaseId || attempt.Delivery.EventId == Guid.Empty
            || string.IsNullOrEmpty(attempt.OutboxKey) || string.IsNullOrEmpty(attempt.PayloadHash)
            || string.IsNullOrEmpty(attempt.Delivery.UsageJson)))
            throw new InvalidDataException("Usage attempt state is invalid.");
    }

    private void ValidateAnchor(UsageOutboxCursor cursor)
    {
        if (cursor.LastOutboxId == 0) return;
        var anchor = database.Outbox(cursor.LastOutboxId - 1, 1).FirstOrDefault();
        if (anchor is null || anchor.Id != cursor.LastOutboxId || anchor.Key != cursor.LastOutboxKey
            || UsageEventV1.Hash(anchor.Payload) != cursor.LastOutboxPayloadHash)
            throw new InvalidDataException("Usage cursor does not match the restored source outbox.");
    }

    private Staged Block(UsageOutboxCursor cursor, long id, string error)
    {
        cursor = cursor with { ErrorCode = error, BlockedOutboxId = id }; Save(cursor);
        return new(cursor, null, "blocked", 0, error);
    }
    private static bool Matches(UsageOutboxAcknowledgement? ack, UsageOutboxDelivery delivery)
        => ack is not null && ack.DestinationId == delivery.DestinationId && ack.LedgerKey == delivery.LedgerKey
            && ack.EventId == delivery.EventId && ack.EventFingerprint == delivery.EventFingerprint
            && ack.Disposition is "accepted" or "duplicate";
    private static bool Owns(UsageOutboxCursor cursor, string owner, long fence, DateTimeOffset now)
        => cursor.Fence == fence && cursor.LeaseOwner == owner && cursor.LeaseUntil > now;
    private static UsageOutboxCursor Advance(UsageOutboxCursor cursor, FabricOutbox row, string hash)
        => cursor with { LastOutboxId = row.Id, LastOutboxKey = row.Key, LastOutboxPayloadHash = hash,
            Attempt = null, ErrorCode = null, BlockedOutboxId = null };
    private UsageOutboxCursor RequireCursor()
    {
        var cursor = ReadCursor() ?? throw new InvalidDataException("Usage cursor is missing.");
        var identity = database.Read<UsageOutboxDatabaseIdentity>(DatabaseIdentityKind, DatabaseIdentityKey)
            ?? throw new InvalidDataException("Usage database identity is missing.");
        ValidateBinding(cursor, identity.Id); return cursor;
    }
    private void Save(UsageOutboxCursor cursor) => database.Put(CursorKind, cursorKey, cursor);
    private sealed record Staged(UsageOutboxCursor Cursor, UsageOutboxDelivery? Delivery, string? Stop,
        int Scanned = 0, string? ErrorCode = null, DateTimeOffset? NextAttemptAt = null);
    private sealed record UsageOutboxDatabaseIdentity(string Id);
}
