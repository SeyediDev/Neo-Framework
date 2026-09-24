using MassTransit;
using Microsoft.Extensions.DependencyInjection;
using Neo.Samples.Messaging;
using Xunit;

namespace Neo.Companion.Tests;

public sealed class OrderSagaTests
{
    [Theory]
    [InlineData(false, 0, "Completed")]
    [InlineData(true, 0, "Cancelled")]
    [InlineData(true, 1, "Cancelled")]
    [InlineData(true, 3, "ManualReview")]
    public async Task Saga_coordinates_payment_and_bounded_compensation(bool decline, int failures, string terminal)
    {
        await using var fixture = new SagaFixture();
        await fixture.Start();
        var id = Guid.NewGuid();
        await fixture.Bus.Publish(new StartOrder(id, decline, failures), fixture.Token);
        await fixture.Until(id, terminal);
        var reservation = fixture.Effects.Reservation(id)!;
        Assert.Equal(1, reservation.ReserveEffects);
        Assert.Equal(decline ? 0 : 1, fixture.Effects.Payment(id)!.Effects);
        Assert.Equal(terminal == "Cancelled" ? 1 : 0, reservation.ReleaseEffects);
        Assert.Equal(terminal != "Cancelled", reservation.Reserved);
        if (failures == 3)
        {
            Assert.Equal(3, reservation.ReleaseAttempts);
            await fixture.Bus.Publish(new RetryOrderCompensation(id), fixture.Token);
            await fixture.Until(id, "Cancelled");
            Assert.Equal(1, fixture.Effects.Reservation(id)!.ReleaseEffects);
            Assert.Equal(4, fixture.Effects.Reservation(id)!.ReleaseAttempts);
        }
    }

    [Fact]
    public async Task Unknown_payment_outcome_requires_reconciliation_and_keeps_reservation()
    {
        await using var fixture = new SagaFixture();
        await fixture.Start();
        var id = Guid.NewGuid();
        await fixture.Bus.Publish(new StartOrder(id, TimeoutPayment: true), fixture.Token);
        await fixture.Until(id, "ManualReview");
        Assert.Contains("Payment outcome unknown", fixture.Observations.Snapshot()[id].Reason);
        Assert.True(fixture.Effects.Reservation(id)!.Reserved);
        Assert.Equal(0, fixture.Effects.Reservation(id)!.ReleaseEffects);
        Assert.Null(fixture.Effects.Payment(id));
    }

    [Fact]
    public void Participant_effects_and_compensation_are_repeatable_with_same_business_key()
    {
        var service = new DemoOrderServices();
        var id = Guid.NewGuid();
        service.Reserve(id);
        service.Reserve(id);
        Assert.False(service.Charge(id, true));
        Assert.False(service.Charge(id, false)); // A replay cannot change the original decision.
        service.Release(id, 0);
        service.Release(id, 0);
        service.Reserve(id); // A delayed reserve command must not undo completed compensation.
        Assert.Equal(new ReservationObservation(false, 1, 1, 1), service.Reservation(id));
        Assert.Equal(new PaymentObservation(false, 0), service.Payment(id));
    }

    private sealed class SagaFixture : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        private readonly CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
        public CancellationToken Token => timeout.Token;
        public IBusControl Bus { get; }
        public DemoOrderServices Effects { get; }
        public SagaObservations Observations { get; }
        public SagaFixture()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton<DemoOrderServices>();
            services.AddSingleton<SagaObservations>();
            services.AddMassTransit(bus =>
            {
                bus.SetKebabCaseEndpointNameFormatter();
                bus.AddDemoOrderSaga();
                bus.UsingInMemory((context, cfg) => cfg.ConfigureEndpoints(context));
            });
            provider = services.BuildServiceProvider(true);
            Bus = provider.GetRequiredService<IBusControl>();
            Effects = provider.GetRequiredService<DemoOrderServices>();
            Observations = provider.GetRequiredService<SagaObservations>();
        }
        public Task Start() => Bus.StartAsync(Token);
        public async Task Until(Guid id, string state)
        {
            while (!Observations.Snapshot().TryGetValue(id, out var item) || item.State != state)
                await Task.Delay(20, Token);
        }
        public async ValueTask DisposeAsync()
        {
            await Bus.StopAsync(CancellationToken.None);
            await provider.DisposeAsync();
            timeout.Dispose();
        }
    }
}
