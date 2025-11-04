using Neo.Domain.Features.Client;

namespace Neo.Application.Features.Outbox.Implementation;

/// <summary>
/// Base implementation for Outbox execution with idempotency and background job scheduling.
/// Now supports tenant-scoped idempotency with hashed keys for better security and isolation.
/// </summary>
public class OutboxMessageProcessor<TOutboxMessage>(
    IOutboxStore outboxStore,
    IIdempotencyStore<TOutboxMessage> idempotencyStore,
    IOutboxJobScheduler outboxJobScheduler,
    IRequesterUser requesterUser
    ) : IOutboxMessageProcessor<TOutboxMessage>
    where TOutboxMessage : IOutboxMessage
{
    private const int MaxRetryAttempts = 3;
    /// <summary>
    /// Enqueue a message that already contains its idempotency key.
    /// </summary>
    public async Task<OutboxResponse> EnqueueAsync<TIdempotenceMessage>(
        TIdempotenceMessage message, CancellationToken ct)
        where TIdempotenceMessage : IIdempotenceOutboxMessage, TOutboxMessage
    {
        return !string.IsNullOrEmpty(message.IdempotencyKey)
            ? await EnqueueAsync(message, message.IdempotencyKey, ct)
            : await EnqueueNonIdempotentAsync(message, ct);
    }

    /// <summary>
    /// Enqueue a message with an explicit idempotency key.
    /// Now uses tenant-scoped idempotency for better isolation.
    /// </summary>
    public async Task<OutboxResponse> EnqueueAsync(
        TOutboxMessage message, string idempotencyKey, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        
        var existing = await idempotencyStore.GetAsync(idempotencyKey, tenantId, ct);
        if (existing != null)
        {
            var existingResponse = await outboxStore.GetOutboxResponseAsync(existing.OutboxId, ct);
            if (existingResponse != null)
                return existingResponse;

            // Cleanup dangling keys
            await idempotencyStore.RemoveAsync(idempotencyKey, tenantId, ct);
        }
        return await PersistAndScheduleAsync(message, idempotencyKey, ct);
    }

    /// <summary>
    /// Enqueue a message without idempotency.
    /// </summary>
    public async Task<OutboxResponse> EnqueueNonIdempotentAsync(
        TOutboxMessage message, CancellationToken ct)
    {
        return await PersistAndScheduleAsync(message, null, ct);
    }

    /// <summary>
    /// Internal helper: save message to outbox, ensure idempotency, and enqueue job.
    /// Now supports tenant-scoped idempotency for better isolation.
    /// </summary>
    private async Task<OutboxResponse> PersistAndScheduleAsync(
        TOutboxMessage message, string? idempotencyKey, CancellationToken ct)
    {
        var tenantId = GetTenantId();
        
        var outboxMessage = new OutboxMessage
        {
            MessageName = typeof(TOutboxMessage).Name,
            MessageType = typeof(TOutboxMessage).FullName!,
            MessageContent = message.ToJson(),
            IdempotencyKey = idempotencyKey,
            OutboxState = OutboxState.Requested // ابتدا Requested، بعداً Queued می‌شود
        };

        await outboxStore.AddAsync(outboxMessage, ct);

        // 🔑 Register idempotency key (atomic add) with tenant scope
        if (!string.IsNullOrEmpty(idempotencyKey))
        {
            var added = await idempotencyStore.AddAsync(idempotencyKey, tenantId, outboxMessage.Id, ct);
            if (!added)
            {
                // اگر کلید همزمان توسط درخواست دیگه ثبت شده بود
                var existing = await idempotencyStore.GetAsync(idempotencyKey, tenantId, ct);
                if (existing != null)
                {
                    var existingResponse = await outboxStore.GetOutboxResponseAsync(existing.OutboxId, ct);
                    if (existingResponse != null)
                        return existingResponse;
                }
                outboxMessage.OutboxState = OutboxState.DuplicateIdempotencyKey;
                outboxMessage.ProcessError = $"Duplicate IdempotencyKey={idempotencyKey} for Tenant={tenantId}";
                await outboxStore.FinishAsync(outboxMessage, ct);
                throw new DuplicateKeyException(outboxMessage.ProcessError);
            }
        }

        // 👇 Register and schedule job 
        try
        {
            outboxMessage.JobId = await outboxJobScheduler.ScheduleOnlineAsync(message, ct);
            if (!string.IsNullOrEmpty(outboxMessage.JobId))
            {
                outboxMessage.OutboxState = OutboxState.Queued;
            }
            else
            {
                outboxMessage.PublishError = "Failed to schedule job - no job ID returned";
                outboxMessage.PublishTryCount = (outboxMessage.PublishTryCount ?? 0) + 1;
                outboxMessage.OutboxState = outboxMessage.PublishTryCount >= MaxRetryAttempts ? OutboxState.Failed : OutboxState.Retrying;
            }
        }
        catch (Exception ex)
        {
            outboxMessage.PublishError = ex.Message;
            outboxMessage.PublishTryCount = (outboxMessage.PublishTryCount ?? 0) + 1;
            outboxMessage.OutboxState = outboxMessage.PublishTryCount >= MaxRetryAttempts ? OutboxState.Failed : OutboxState.Retrying;
        }

        await outboxStore.UpdateAsync(outboxMessage, ct);

        return new OutboxResponse(outboxMessage.Id, outboxMessage.OutboxState, outboxMessage.JobId, outboxMessage.IdempotencyKey);
    }

    /// <summary>
    /// Get the tenant ID from the requester context.
    /// Uses tenant_id claim from JWT token.
    /// </summary>
    private string GetTenantId()
    {
        return requesterUser.TenantId ?? throw new InvalidOperationException("TenantId is required for idempotency operations");
    }

    /// <summary>
    /// Get the current outbox message by its ID.
    /// </summary>
    public async Task<OutboxResponse?> GetMessageAsync(long outboxId, CancellationToken ct)
    {
        OutboxMessage? outboxMessage = await outboxStore.GetAsync(outboxId, ct);
        if (outboxMessage != null)
        {
            return new OutboxResponse(
                outboxMessage.Id,
                outboxMessage.OutboxState,
                outboxMessage.JobId,
                outboxMessage.IdempotencyKey);
        }
        return null;
    }

    /// <summary>
    /// Get the current status of an outbox message by its ID.
    /// </summary>
    public async Task<MessageState?> GetStatusAsync(long outboxId, CancellationToken ct)
    {
        return await outboxStore.GetStatusAsync(outboxId, ct);
    }

    /// <summary>
    /// Update status and optional fields of an outbox message.
    /// </summary>
    public async Task UpdateStatusAsync(
        long outboxId, OutboxState state, string? jobId, string? content, CancellationToken ct)
    {
        OutboxMessage? outboxMessage = await outboxStore.GetAsync(outboxId, ct);
        if (outboxMessage != null)
        {
            outboxMessage.JobId = jobId;
            outboxMessage.OutboxState = state;
            if (content != null && outboxMessage.MessageContent == null)
                outboxMessage.MessageContent = content;

            await outboxStore.UpdateAsync(outboxMessage, ct);
        }
    }
}
