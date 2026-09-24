# Explicit RabbitMQ registration

`AddNeoRabbitMq` is opt-in and uses the repository's MassTransit 8.4.1 packages.
It does not replace Hangfire or activate a broker through `AddNeoInfrastructureServices`.

- [Runnable publisher and consumers](../../../../tools/Neo.Companion/samples/MessagingDemo)
- [Persian guide and Hangfire comparison](../../../../tools/Neo.Companion/docs/MESSAGING.fa.md)

Register consumers and definitions in the callback, configure the `RabbitMq` section and keep
production credentials outside checked-in configuration. Consumer definitions own retry and outbox policies.
The optional transport callback supports advanced transport settings, including TLS. The helper calls
ConfigureEndpoints last and waits for broker startup with a bounded timeout.

MassTransit/RabbitMQ is not an atomic database transaction or an exactly-once guarantee.
The guide explains durable inbox/outbox requirements and the sample's limitations.
