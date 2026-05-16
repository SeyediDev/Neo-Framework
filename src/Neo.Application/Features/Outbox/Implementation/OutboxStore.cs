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

	public void UpdateOnly(OutboxMessage outboxMessage)
	{
		outboxMessageCmdRepository.Update(outboxMessage);
	}

	public async Task SaveChangesAsync(CancellationToken ct)
	{
		_ = await outboxMessageCmdRepository.UnitOfWork.SaveChangesAsync(ct);
	}

	public async Task FinishAsync(OutboxMessage outboxMessage, CancellationToken ct)
	{
		outboxMessage.ExpireDate = DateTime.UtcNow;
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
		var x = await outboxMessageRepository.FirstOrDefaultAsync(x => x.Id == outboxId, ct);
		return x != null ? new MessageState(x.OutboxState, x.JobId) : null;
	}

	public async Task<OutboxResponse?> GetOutboxResponseAsync(long outboxId, CancellationToken ct)
	{
		var x = await outboxMessageRepository.FirstOrDefaultAsync(x => x.Id == outboxId, ct);
		return x != null ? new OutboxResponse(x.Id, x.OutboxState, x.JobId, x.IdempotencyKey) : null;
	}

	public async Task<IEnumerable<OutboxMessage>> GetRequested(int batchSize, CancellationToken cancellationToken)
	{
		return await outboxMessageRepository.GetAllAsync(cancellationToken,
		   x => x.OutboxState == OutboxState.Requested || x.OutboxState == OutboxState.Retrying, null, null, batchSize);
	}
}