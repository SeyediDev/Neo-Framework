namespace Neo.Samples.Messaging;

// OrderId is the Saga correlation key; each real business operation also needs a stable idempotency key.
public sealed record StartOrder(Guid OrderId, bool DeclinePayment = false, int ReleaseFailures = 0, bool TimeoutPayment = false);
public sealed record ReserveInventory(Guid OrderId);
public sealed record InventoryReserved(Guid OrderId);
public sealed record ChargePayment(Guid OrderId, bool Decline, bool Timeout = false);
public sealed record PaymentSucceeded(Guid OrderId);
public sealed record PaymentDeclined(Guid OrderId);
public sealed record ReleaseInventory(Guid OrderId, int Failures);
public sealed record InventoryReleased(Guid OrderId);
// Operator-driven in this demo; never treat an unconfirmed payment timeout as a decline.
public sealed record RetryOrderCompensation(Guid OrderId);
