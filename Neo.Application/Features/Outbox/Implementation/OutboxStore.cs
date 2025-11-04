namespace Neo.Application.Features.Outbox.Implementation;

public class OutboxStore(
    IQueryRepository<OutboxMessage, long> outboxMessageRepository, 
    ICommandRepository<OutboxMessage, long> outboxMessageCmdRepository
    ) : IOutboxStore
{
    public async Task AddAsync(OutboxMessage outboxMessage, CancellationToken ct)
    {
        outboxMessageCmdRepository.Add(outboxMessage);
        _ = await outboxMessageCmdRepository.UnitOfWork.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(OutboxMessage outboxMessage, CancellationToken ct)
    {
        outboxMessageCmdRepository.Update(outboxMessage);
        _ = await outboxMessageCmdRepository.UnitOfWork.SaveChangesAsync(ct);
    }
    
    public void UpdateOnlyAsync(OutboxMessage outboxMessage)
    {
        outboxMessageCmdRepository.Update(outboxMessage);
    }
    
    public async Task SaveChangesAsync(CancellationToken ct)
    {
        _ = await outboxMessageCmdRepository.UnitOfWork.SaveChangesAsync(ct);
    }

    public async Task FinishAsync(OutboxMessage outboxMessage, CancellationToken ct)
    {
        outboxMessage.ExpireDate = DateTime.Now;
        outboxMessage.IsDeleted = true;
        outboxMessageCmdRepository.Update(outboxMessage);
        _ = await outboxMessageCmdRepository.UnitOfWork.SaveChangesAsync(ct);
    }

    public async Task<OutboxMessage?> GetAsync(long outboxId, CancellationToken ct)
    {
        return await outboxMessageRepository.GetByIdAsync(outboxId, ct);
    }

    public async Task<MessageState?> GetStatusAsync(long outboxId, CancellationToken ct)
    {
        return await outboxMessageRepository.GetEntityAsQueryable()
            .Where(x => x.Id == outboxId)
            .Select(x => new MessageState(x.OutboxState, x.JobId))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<OutboxResponse?> GetOutboxResponseAsync(long outboxId, CancellationToken ct)
    {
        return await outboxMessageRepository.GetEntityAsQueryable()
            .Where(x => x.Id == outboxId)
            .Select(x => new OutboxResponse(x.Id, x.OutboxState, x.JobId, x.IdempotencyKey))
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IEnumerable<OutboxMessage>> GetRequested(int batchSize, CancellationToken cancellationToken)
    {
        return await outboxMessageRepository.GetAllAsync(cancellationToken,
           x => x.OutboxState == OutboxState.Requested || x.OutboxState == OutboxState.Queued, null, null, batchSize);
    }
}