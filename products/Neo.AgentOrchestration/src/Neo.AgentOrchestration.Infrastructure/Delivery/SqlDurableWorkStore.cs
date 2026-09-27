using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Neo.AgentOrchestration.Application.Delivery;
using Neo.AgentOrchestration.Application.Work;
using Neo.AgentOrchestration.Domain.Projects;
using Neo.AgentOrchestration.Domain.Work;
using Neo.AgentOrchestration.Infrastructure.Persistence;
using Neo.Domain.Entities.Common;

namespace Neo.AgentOrchestration.Infrastructure.Delivery;

public sealed class SqlDurableWorkStore(IDbContextFactory<OrchestrationDbContext> factory, TimeProvider clock) : IDurableWorkStore
{
    public Task<DeliveryAcceptance> EnqueueAsync(WorkspaceScope scope, WorkDeliveryRequest request, WorkDeliveryKind kind,
        Func<IWorkItemSession, CancellationToken, Task> mutation, CancellationToken ct)
        => StoreAsync(scope, request, mutation, false, kind, ct);

    public Task<DeliveryAcceptance> ApplyInboxAsync(WorkspaceScope scope, WorkDeliveryRequest request,
        Func<IWorkItemSession, CancellationToken, Task> mutation, CancellationToken ct, WorkDeliveryKind? followUp = null)
        => StoreAsync(scope, request, mutation, true, followUp, ct);

    private async Task<DeliveryAcceptance> StoreAsync(WorkspaceScope scope, WorkDeliveryRequest request,
        Func<IWorkItemSession, CancellationToken, Task> mutation, bool inbox, WorkDeliveryKind? kind, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(mutation);
        var (source, messageId, hash) = Validate(request, inbox, kind);
        await using var db = await factory.CreateDbContextAsync(ct);
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct);
        await WorkspaceTransaction.LockAsync(db, scope, ct);
        if (inbox)
        {
            var existing = await db.InboxReceipts.Include(x => x.FollowUp).SingleOrDefaultAsync(x =>
                x.OrganizationId == scope.OrganizationId && x.WorkspaceId == scope.WorkspaceId &&
                x.Source == source && x.MessageId == messageId, ct);
            if (existing is not null)
            {
                Match(existing.WorkItemId, existing.PayloadHash, request.WorkItemId, hash);
                return new(existing.Id, existing.WorkItemId, existing.WorkItemVersion, existing.FollowUp?.OutboxId, true);
            }
        }
        else
        {
            var existing = await db.Deliveries.SingleOrDefaultAsync(x => x.OrganizationId == scope.OrganizationId &&
                x.WorkspaceId == scope.WorkspaceId && x.Source == source && x.MessageId == messageId, ct);
            if (existing is not null)
            {
                Match(existing.WorkItemId, existing.PayloadHash, request.WorkItemId, hash);
                return new(existing.Id, existing.WorkItemId, existing.WorkItemVersion, existing.OutboxId, true);
            }
        }
        var session = new SqlWorkspaceWorkStore.Session(db, scope);
        var item = await session.GetItemAsync(request.WorkItemId, ct) ?? throw new KeyNotFoundException("Work item not found.");
        var project = await session.GetProjectAsync(item.ProjectId, ct);
        if (project is not { IsEnabled: true }) throw new InvalidOperationException("Enabled project not found.");
        await mutation(session, ct);
        ct.ThrowIfCancellationRequested();
        var now = clock.GetUtcNow();
        DeliveryRecord? delivery = kind.HasValue ? Stage(db, item, source, messageId, hash, kind.Value, now) : null;
        InboxReceipt? receipt = null;
        if (inbox)
        {
            receipt = new InboxReceipt { Id = Guid.NewGuid(), OrganizationId = item.OrganizationId, WorkspaceId = item.WorkspaceId,
                ProjectId = item.ProjectId, WorkItemId = item.Id, WorkItemVersion = item.Version,
                Source = source, MessageId = messageId, PayloadHash = hash, AppliedAtUtc = now, FollowUp = delivery };
            db.InboxReceipts.Add(receipt);
        }
        try
        {
            // Inbox/effect/follow-up and outgoing business changes commit together.
            // Do not use IOutboxStore.AddAsync: it saves independently of staging.
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateConcurrencyException) { throw new DeliveryConflictException("Work changed before delivery could be committed."); }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        { throw new DeliveryConflictException("A delivery key or business assignment already exists."); }
        return new(receipt?.Id ?? delivery!.Id, item.Id, item.Version, delivery?.OutboxId, false);
    }

    private static DeliveryRecord Stage(OrchestrationDbContext db, WorkItem item, string source, string messageId,
        string hash, WorkDeliveryKind kind, DateTimeOffset now)
    {
        var id = Guid.NewGuid();
        var delivery = new DeliveryRecord { Id = id, OrganizationId = item.OrganizationId, WorkspaceId = item.WorkspaceId,
            ProjectId = item.ProjectId, WorkItemId = item.Id, WorkItemVersion = item.Version, Source = source,
            MessageId = messageId, PayloadHash = hash, Kind = kind, CreatedAtUtc = now,
            Outbox = new OutboxMessage { MessageName = nameof(WorkDeliverySignal), MessageType = typeof(WorkDeliverySignal).FullName!,
                MessageContent = JsonSerializer.Serialize(new WorkDeliverySignal(id)), TenantKey = item.WorkspaceId.ToString("N"),
                IdempotencyKey = id.ToString("N"), CreateDate = now.UtcDateTime, LastModified = now.UtcDateTime } };
        db.Deliveries.Add(delivery);
        return delivery;
    }

    private static (string Source, string MessageId, string Hash) Validate(WorkDeliveryRequest request, bool inbox, WorkDeliveryKind? kind)
    {
        if (request.WorkItemId == Guid.Empty) throw new ArgumentException("Work item identifier is required.");
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Source);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.MessageId);
        ArgumentNullException.ThrowIfNull(request.Payload);
        var source = request.Source.Trim().ToLowerInvariant();
        var messageId = request.MessageId.Trim();
        if (source.Length > 80 || source.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.')) ||
            messageId.Length > 200 || messageId.Any(char.IsControl))
            throw new ArgumentException("Invalid delivery namespace or message identifier.");
        if (Encoding.UTF8.GetByteCount(request.Payload) > 262144) throw new ArgumentException("Delivery fingerprint input is too large.");
        if (kind.HasValue && !Enum.IsDefined(kind.Value)) throw new ArgumentOutOfRangeException(nameof(kind));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { Inbox = inbox, request.WorkItemId, Kind = kind, request.Payload });
        return (source, messageId, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private static void Match(Guid existingItem, string existingHash, Guid item, string hash)
    {
        if (existingItem != item || existingHash != hash)
            throw new DeliveryConflictException("Delivery key was already used with a different target, payload or operation.");
    }
}
