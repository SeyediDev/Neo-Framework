namespace Neo.Samples.Messaging;

// Keep contracts in a shared contracts package in a multi-service application.
// No DbContext, EF entities, user credentials or method expressions cross the broker.
public sealed record OrderSubmitted(Guid EventId, Guid OrderId, DateTimeOffset OccurredAt,
    bool FailOnce = false, bool FailPermanently = false);
