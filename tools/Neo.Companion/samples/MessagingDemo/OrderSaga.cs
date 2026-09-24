using System.Collections.Concurrent;
using MassTransit;

namespace Neo.Samples.Messaging;

public sealed class OrderSagaState : SagaStateMachineInstance
{
    public Guid CorrelationId { get; set; }
    public string CurrentState { get; set; } = "";
    public bool DeclinePayment { get; set; }
    public int ReleaseFailures { get; set; }
    public bool TimeoutPayment { get; set; }
    public bool CompensationRequired { get; set; }
}

public sealed record SagaObservation(string State, string? Reason);
public sealed class SagaObservations
{
    private readonly ConcurrentDictionary<Guid, SagaObservation> states = new();
    public void Record(Guid id, string state, string? reason = null) => states[id] = new(state, reason);
    public IReadOnlyDictionary<Guid, SagaObservation> Snapshot() => new Dictionary<Guid, SagaObservation>(states);
}

public sealed class OrderSaga : MassTransitStateMachine<OrderSagaState>
{
    public State Reserving { get; private set; } = null!;
    public State Charging { get; private set; } = null!;
    public State Compensating { get; private set; } = null!;
    public State Completed { get; private set; } = null!;
    public State Cancelled { get; private set; } = null!;
    public State ManualReview { get; private set; } = null!;
    public Event<StartOrder> Started { get; private set; } = null!;
    public Event<InventoryReserved> Reserved { get; private set; } = null!;
    public Event<PaymentSucceeded> Paid { get; private set; } = null!;
    public Event<PaymentDeclined> Declined { get; private set; } = null!;
    public Event<InventoryReleased> Released { get; private set; } = null!;
    public Event<RetryOrderCompensation> RetryCompensation { get; private set; } = null!;
    public Event<Fault<ReserveInventory>> ReserveFault { get; private set; } = null!;
    public Event<Fault<ChargePayment>> PaymentFault { get; private set; } = null!;
    public Event<Fault<ReleaseInventory>> ReleaseFault { get; private set; } = null!;

    public OrderSaga(IEndpointNameFormatter names, SagaObservations observations)
    {
        InstanceState(x => x.CurrentState);
        Event(() => Started, e => e.CorrelateById(x => x.Message.OrderId));
        Event(() => Reserved, e => e.CorrelateById(x => x.Message.OrderId));
        Event(() => Paid, e => e.CorrelateById(x => x.Message.OrderId));
        Event(() => Declined, e => e.CorrelateById(x => x.Message.OrderId));
        Event(() => Released, e => e.CorrelateById(x => x.Message.OrderId));
        Event(() => RetryCompensation, e => e.CorrelateById(x => x.Message.OrderId));
        Event(() => ReserveFault, e => e.CorrelateById(x => x.Message.Message.OrderId));
        Event(() => PaymentFault, e => e.CorrelateById(x => x.Message.Message.OrderId));
        Event(() => ReleaseFault, e => e.CorrelateById(x => x.Message.Message.OrderId));
        var reserveQueue = new Uri("queue:" + names.Consumer<ReserveInventoryConsumer>());
        var paymentQueue = new Uri("queue:" + names.Consumer<ChargePaymentConsumer>());
        var releaseQueue = new Uri("queue:" + names.Consumer<ReleaseInventoryConsumer>());

        Initially(When(Started)
            .Then(x => { x.Saga.DeclinePayment = x.Message.DeclinePayment; x.Saga.ReleaseFailures = x.Message.ReleaseFailures; x.Saga.TimeoutPayment = x.Message.TimeoutPayment; })
            .TransitionTo(Reserving)
            .Then(x => observations.Record(x.Saga.CorrelationId, "Reserving"))
            .ThenAsync(x => x.Send(reserveQueue, new ReserveInventory(x.Saga.CorrelationId))));

        During(Reserving,
            When(Reserved).TransitionTo(Charging)
                .Then(x => observations.Record(x.Saga.CorrelationId, "Charging"))
                .ThenAsync(x => x.Send(paymentQueue, new ChargePayment(x.Saga.CorrelationId, x.Saga.DeclinePayment, x.Saga.TimeoutPayment))),
            When(ReserveFault).TransitionTo(ManualReview)
                .Then(x => observations.Record(x.Saga.CorrelationId, "ManualReview", "Reservation outcome needs reconciliation")));
        During(Charging,
            When(Paid).TransitionTo(Completed).Then(x => observations.Record(x.Saga.CorrelationId, "Completed")),
            When(Declined).Then(x => x.Saga.CompensationRequired = true).TransitionTo(Compensating)
                .Then(x => observations.Record(x.Saga.CorrelationId, "Compensating"))
                .ThenAsync(x => x.Send(releaseQueue, new ReleaseInventory(x.Saga.CorrelationId, x.Saga.ReleaseFailures))),
            When(PaymentFault).TransitionTo(ManualReview)
                .Then(x => observations.Record(x.Saga.CorrelationId, "ManualReview", "Payment outcome unknown; reconcile before compensation")));
        During(Compensating,
            When(Released).Then(x => x.Saga.CompensationRequired = false).TransitionTo(Cancelled)
                .Then(x => observations.Record(x.Saga.CorrelationId, "Cancelled")),
            When(ReleaseFault).TransitionTo(ManualReview)
                .Then(x => observations.Record(x.Saga.CorrelationId, "ManualReview", "Compensation failed after bounded retries")));
        During(ManualReview,
            When(RetryCompensation).If(x => x.Saga.CompensationRequired, action => action
                .TransitionTo(Compensating)
                .Then(x => observations.Record(x.Saga.CorrelationId, "Compensating"))
                .ThenAsync(x => x.Send(releaseQueue, new ReleaseInventory(x.Saga.CorrelationId, 0)))),
            When(Released).If(x => x.Saga.CompensationRequired, action => action
                .Then(x => x.Saga.CompensationRequired = false).TransitionTo(Cancelled)
                .Then(x => observations.Record(x.Saga.CorrelationId, "Cancelled"))));

        // Keep terminal instances as deduplication tombstones for the life of this demo process.
        // Durable production persistence and retention are deliberately not implied by InMemoryRepository.
        During(Reserving, Ignore(Started));
        During(Charging, Ignore(Started), Ignore(Reserved));
        During(Compensating, Ignore(Started), Ignore(Reserved), Ignore(Declined), Ignore(RetryCompensation));
        During(ManualReview, Ignore(Started), Ignore(Reserved), Ignore(Declined), Ignore(ReleaseFault));
        During(Completed, Ignore(Started), Ignore(Reserved), Ignore(Paid), Ignore(RetryCompensation));
        During(Cancelled, Ignore(Started), Ignore(Reserved), Ignore(Declined), Ignore(Released), Ignore(ReleaseFault), Ignore(RetryCompensation));
    }
}

public sealed class OrderSagaDefinition : SagaDefinition<OrderSagaState>
{
    public OrderSagaDefinition() => ConcurrentMessageLimit = 1;
    protected override void ConfigureSaga(IReceiveEndpointConfigurator endpoint,
        ISagaConfigurator<OrderSagaState> saga, IRegistrationContext context) => endpoint.UseInMemoryOutbox(context);
}
